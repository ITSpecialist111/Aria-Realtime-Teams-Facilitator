using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using Microsoft.Skype.Bots.Media;
using RtBot.Chat;
using RtBot.Configuration;
using RtBot.Facilitator;
using RtBot.Media;
using RtBot.Observability;
using RtBot.Tools;
using RtBot.Voice;

namespace RtBot.Teams;

/// <summary>
/// Owns one Teams call: pumps audio to the voice model, keeps the meeting record, and runs the
/// facilitator duties (greeting, notes, time checks, external-guest policy, recap).
/// </summary>
public sealed class CallHandler : IMeetingActions, IDisposable
{
    /// <summary>Calls are reaped by the service if the bot stops asserting that it is still there.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FacilitatorTick = TimeSpan.FromSeconds(5);

    private readonly ICall _call;
    private readonly VoiceLiveBridge _bridge;
    private readonly BotChatClient _chat;
    private readonly MeetingDetailsLookup _details;
    private readonly MeetingMailer _mailer;
    private readonly MeetingScribe _scribe;
    private readonly RtBotOptions _options;
    private readonly FacilitatorOptions _facilitator;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _inbound;
    private readonly CancellationTokenSource _cts = new();
    private readonly Activity? _meetingSpan;
    private readonly Timer _keepAlive;
    private readonly string? _joinUrl;

    private OutboundAudioPacer? _pacer;
    private Task? _inboundPump;
    private Task? _facilitatorLoop;
    private IAudioSocket? _audioSocket;
    private int _disposed;
    private string _currentSpeaker = "Someone";
    private DateTimeOffset? _aloneSince;
    private DateTimeOffset _lastScribe = DateTimeOffset.UtcNow;
    private bool _halfwayAnnounced;
    private bool _wrapUpAnnounced;
    private bool _externalAnnounced;
    private bool _recordingSet;
    private bool _noticeGiven;

    public CallHandler(
        ICall call,
        VoiceLiveBridge bridge,
        ToolCatalog catalog,
        TextModelClient textModel,
        BotChatClient chat,
        MeetingDetailsLookup details,
        MeetingMailer mailer,
        RtBotOptions options,
        string? joinUrl,
        ILogger logger)
    {
        _call = call;
        _bridge = bridge;
        _chat = chat;
        _details = details;
        _mailer = mailer;
        _options = options;
        _facilitator = options.Facilitator;
        _joinUrl = joinUrl;
        _logger = logger;

        Meeting = new MeetingContext(call.Id, call.Resource?.ChatInfo?.ThreadId, options.AzureAd.TenantId);
        Tools = new MeetingToolset(
            catalog,
            Meeting,
            this,
            textModel,
            _facilitator.ScribeModel,
            _facilitator.EnableScreenVision,
            options.Voice.AgentName,
            logger);
        _scribe = new MeetingScribe(textModel, _facilitator.ScribeModel, logger);

        _bridge.Attach(Meeting, Tools, () => _currentSpeaker);
        _bridge.SessionLost += () => LeaveAsync(CancellationToken.None);

        // Bounded so a stalled model connection sheds old audio instead of growing without limit.
        _inbound = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(AudioConstants.FramesPerSecond * 10)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        _meetingSpan = AgentTelemetry.StartMeetingSpan(call.Id, Meeting.ThreadId);
        _call.OnUpdated += OnCallUpdated;
        _call.Participants.OnUpdated += OnParticipantsUpdated;

        _keepAlive = new Timer(_ => SendKeepAlive(), null, KeepAliveInterval, KeepAliveInterval);
    }

    public MeetingContext Meeting { get; }

    public MeetingToolset Tools { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _audioSocket = _call.GetLocalMediaSession().AudioSocket
            ?? throw new InvalidOperationException("The call has no audio socket.");

        _pacer = new OutboundAudioPacer(_audioSocket, _logger);
        _pacer.Start();

        // The roster may already be populated by the time the handler exists.
        foreach (IParticipant participant in _call.Participants)
        {
            TrackParticipant(participant);
        }

        await LoadMeetingDetailsAsync(cancellationToken).ConfigureAwait(false);

        await _bridge.StartAsync(_pacer, cancellationToken).ConfigureAwait(false);

        _audioSocket.AudioMediaReceived += OnAudioMediaReceived;
        _audioSocket.DominantSpeakerChanged += OnDominantSpeakerChanged;

        _inboundPump = Task.Run(() => PumpInboundAsync(_cts.Token), CancellationToken.None);

        await SetRecordingStatusAsync(RecordingStatus.Recording).ConfigureAwait(false);
        await GreetAsync(cancellationToken).ConfigureAwait(false);
        await ApplyExternalPolicyAsync(cancellationToken).ConfigureAwait(false);

        _facilitatorLoop = Task.Run(() => RunFacilitatorAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation(
            "Call {CallId} wired. Subject={Subject} Participants={Count} Restricted={Restricted}",
            _call.Id,
            Meeting.Subject,
            Meeting.Participants.Count,
            Meeting.IsRestricted);
    }

    // ---- IMeetingActions -------------------------------------------------------------------

    public Task SpeakAsync(string instruction, CancellationToken cancellationToken) =>
        _bridge.AnnounceAsync(instruction, cancellationToken);

    public Task<bool> PostToChatAsync(string markdown, CancellationToken cancellationToken) =>
        Meeting.ThreadId is { } thread
            ? _chat.PostAsync(thread, markdown, cancellationToken)
            : Task.FromResult(false);

    public Task<byte[]?> CaptureSharedScreenAsync(CancellationToken cancellationToken)
    {
        // Screen-share capture needs a VBSS receive socket; not wired in this build.
        return Task.FromResult<byte[]?>(null);
    }

    public async Task LeaveAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Leaving call {CallId}.", _call.Id);

        // Give a spoken goodbye a moment to finish playing before hanging up.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (_bridge.IsAgentSpeaking && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        await _call.DeleteAsync().ConfigureAwait(false);
    }

    /// <summary>Operator-injected prompt.</summary>
    public Task SayAsync(string text, CancellationToken cancellationToken) =>
        _bridge.SayAsync(text, cancellationToken);

    // ---- Facilitation ----------------------------------------------------------------------

    private async Task LoadMeetingDetailsAsync(CancellationToken cancellationToken)
    {
        if (_joinUrl is null || _call.Resource?.MeetingInfo is not OrganizerMeetingInfo { Organizer.User.Id: { } organizerId })
        {
            return;
        }

        Meeting.OrganizerUserId = organizerId;

        (string? subject, DateTimeOffset? start, DateTimeOffset? end) =
            await _details.FindAsync(organizerId, _joinUrl, cancellationToken).ConfigureAwait(false);

        Meeting.Subject = subject;
        Meeting.ScheduledStart = start;
        Meeting.ScheduledEnd = end;
    }

    private async Task GreetAsync(CancellationToken cancellationToken)
    {
        if (!_facilitator.AnnounceOnJoin)
        {
            return;
        }

        string name = _options.Voice.AgentName;

        bool posted = await PostToChatAsync(
            $"""
            **{name} (AI facilitator) has joined and is taking notes.**
            Say "{name}" to ask a question out loud, or @mention me here. I'm keeping notes, decisions and action items{(_facilitator.PostRecapOnEnd ? " and will post a recap when the meeting ends" : string.Empty)}. Ask me to leave at any time.
            """,
            cancellationToken).ConfigureAwait(false);

        await _bridge.AnnounceAsync(
            $"Introduce yourself in one sentence as {name}, the AI facilitator. Say you're taking notes, and people can ask you anything by saying your name.",
            cancellationToken).ConfigureAwait(false);

        _noticeGiven = true;
        _logger.LogInformation("Join notice given. Chat={Posted}", posted);
    }

    private async Task RunFacilitatorAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(FacilitatorTick);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Facilitator tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now - _lastScribe >= TimeSpan.FromSeconds(_facilitator.ScribeIntervalSeconds))
        {
            _lastScribe = now;
            await _scribe.UpdateAsync(Meeting, cancellationToken).ConfigureAwait(false);
        }

        if (_facilitator.ProactiveTimeChecks && Meeting.ScheduledStart is { } start && Meeting.ScheduledEnd is { } end && end > start)
        {
            TimeSpan length = end - start;

            if (!_halfwayAnnounced && length >= TimeSpan.FromMinutes(20) && now >= start + length / 2 && now < end)
            {
                _halfwayAnnounced = true;
                await _bridge.AnnounceAsync(
                    $"Give a brief time check: we're halfway through, about {(int)Math.Round((end - now).TotalMinutes)} minutes left.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!_wrapUpAnnounced && now >= end - TimeSpan.FromMinutes(_facilitator.WrapUpWarningMinutes) && now < end)
            {
                _wrapUpAnnounced = true;
                int open = Meeting.SnapshotNotes().OpenQuestions.Count;
                await _bridge.AnnounceAsync(
                    $"Give a brief heads-up that about {(int)Math.Ceiling((end - now).TotalMinutes)} minutes remain" +
                    (open > 0 ? $" and there are {open} open questions to settle or take offline." : "."),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (Meeting.Participants.Count == 0)
        {
            _aloneSince ??= now;
            if (now - _aloneSince.Value >= TimeSpan.FromSeconds(_facilitator.LeaveWhenAloneSeconds))
            {
                _logger.LogInformation("Nobody left in the meeting; leaving.");
                await LeaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _aloneSince = null;
        }
    }

    /// <summary>Applies the external-participant policy whenever the roster changes.</summary>
    private async Task ApplyExternalPolicyAsync(CancellationToken cancellationToken)
    {
        if (_facilitator.ExternalParticipants == ExternalParticipantPolicy.Allow)
        {
            return;
        }

        bool external = Meeting.HasExternalParticipants;

        if (_facilitator.ExternalParticipants == ExternalParticipantPolicy.Leave && external)
        {
            await _bridge.AnnounceAsync(
                "Say that someone from outside the organisation has joined, so you are leaving the meeting.",
                cancellationToken).ConfigureAwait(false);
            await LeaveAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (external == Meeting.IsRestricted)
        {
            return;
        }

        Meeting.IsRestricted = external;
        await _bridge.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);

        if (external && !_externalAnnounced)
        {
            _externalAnnounced = true;
            await _bridge.AnnounceAsync(
                "Say briefly that someone from outside the organisation has joined, so you will keep helping with the meeting but won't look up internal information.",
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Restricted mode {State}.", external ? "on" : "off");
    }

    private void TrackParticipant(IParticipant participant)
    {
        ParticipantInfo? info = participant.Resource?.Info;
        if (info?.Identity is not IdentitySet identity || participant.Resource?.IsInLobby == true)
        {
            return;
        }

        // Bots, including this one, are not attendees.
        CommunicationsIdentitySet? comms = identity as CommunicationsIdentitySet;
        if (identity.Application is not null ||
            comms?.ApplicationInstance is not null ||
            identity.AdditionalData?.ContainsKey("applicationInstance") == true)
        {
            return;
        }

        (string name, string? tenant, bool external) = Classify(identity);
        Meeting.UpsertParticipant(new MeetingParticipant(participant.Id, name, tenant, external, identity.User?.Id));
    }

    /// <summary>
    /// Decides whether an attendee is outside the organisation. Anything that cannot be proven
    /// internal (guests, anonymous joiners, phone dial-ins, other tenants) counts as external.
    /// </summary>
    private (string Name, string? Tenant, bool External) Classify(IdentitySet identity)
    {
        CommunicationsIdentitySet? comms = identity as CommunicationsIdentitySet;

        if (identity.User is { } user)
        {
            string? tenant = user.AdditionalData?.TryGetValue("tenantId", out object? t) == true ? t?.ToString() : null;
            if (user is CommunicationsUserIdentity cu && !string.IsNullOrEmpty(cu.TenantId))
            {
                tenant = cu.TenantId;
            }

            bool external = !string.Equals(tenant, _options.AzureAd.TenantId, StringComparison.OrdinalIgnoreCase);
            if (external)
            {
                _logger.LogInformation("Participant {Name} treated as external (tenant {Tenant}).", user.DisplayName, tenant ?? "unknown");
            }

            return (user.DisplayName ?? "Someone", tenant, external);
        }

        string fallback =
            comms?.Guest?.DisplayName
            ?? comms?.Phone?.DisplayName
            ?? comms?.AzureCommunicationServicesUser?.DisplayName
            ?? (identity.AdditionalData?.TryGetValue("guest", out object? g) == true ? DisplayNameOf(g) : null)
            ?? (identity.AdditionalData?.TryGetValue("phone", out object? p) == true ? DisplayNameOf(p) : null)
            ?? (comms?.Phone is not null ? "Phone caller" : "Guest");

        return (fallback, null, true);
    }

    private static string? DisplayNameOf(object? value) =>
        value is Identity id ? id.DisplayName
        : value is IDictionary<string, object> dict && dict.TryGetValue("displayName", out object? n) ? n?.ToString()
        : null;

    private async Task SetRecordingStatusAsync(RecordingStatus status)
    {
        if (!_facilitator.SetRecordingStatus)
        {
            return;
        }

        try
        {
            await _call.UpdateRecordingStatusAsync(status).ConfigureAwait(false);
            _recordingSet = status == RecordingStatus.Recording;
            _logger.LogInformation("Recording status set to {Status}.", status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set recording status {Status}.", status);
        }
    }

    // ---- Media -----------------------------------------------------------------------------

    /// <summary>Copies the frame out of unmanaged memory and returns immediately.</summary>
    /// <remarks>
    /// This fires 50 times a second and the platform serialises the events, so anything slow here
    /// stalls the whole receive path. The buffer must be disposed even when handling throws.
    /// </remarks>
    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            long length = e.Buffer.Length;
            if (length > 0 && e.Buffer.Data != IntPtr.Zero)
            {
                byte[] frame = new byte[length];
                Marshal.Copy(e.Buffer.Data, frame, 0, (int)length);
                _inbound.Writer.TryWrite(frame);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy an inbound audio frame.");
        }
        finally
        {
            e.Buffer.Dispose();
        }
    }

    /// <summary>Forwards inbound audio to the model in ~100 ms chunks.</summary>
    /// <remarks>
    /// Teams delivers 20 ms frames, but one WebSocket send per frame is 50 sends a second and
    /// the service expects roughly 100 ms chunks. Batching keeps the socket stable at a cost of
    /// about 80 ms of added latency.
    /// </remarks>
    private async Task PumpInboundAsync(CancellationToken cancellationToken)
    {
        const int framesPerChunk = 5;
        byte[] chunk = new byte[AudioConstants.FrameSizeBytes * framesPerChunk];
        int offset = 0;

        try
        {
            await foreach (byte[] frame in _inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (frame.Length + offset > chunk.Length)
                {
                    await _bridge.SendAudioAsync(chunk[..offset], cancellationToken).ConfigureAwait(false);
                    offset = 0;
                }

                Array.Copy(frame, 0, chunk, offset, frame.Length);
                offset += frame.Length;

                if (offset == chunk.Length)
                {
                    await _bridge.SendAudioAsync(chunk, cancellationToken).ConfigureAwait(false);
                    offset = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inbound audio pump terminated for call {CallId}.", _call.Id);
        }
    }

    private void OnDominantSpeakerChanged(object? sender, DominantSpeakerChangedEventArgs e)
    {
        if (e.CurrentDominantSpeaker == DominantSpeakerChangedEventArgs.None)
        {
            return;
        }

        string? speaker = ResolveParticipantName(e.CurrentDominantSpeaker);
        if (speaker is not null)
        {
            _currentSpeaker = speaker;
        }
    }

    private string? ResolveParticipantName(uint mediaSourceId)
    {
        string msi = mediaSourceId.ToString();

        IParticipant? participant = _call.Participants.FirstOrDefault(p =>
            p.Resource?.IsInLobby == false &&
            p.Resource.MediaStreams?.Any(s => s.SourceId == msi) == true);

        if (participant is null)
        {
            return null;
        }

        return Meeting.Participants.FirstOrDefault(m => m.Id == participant.Id)?.Name
            ?? participant.Resource?.Info?.Identity?.User?.DisplayName;
    }

    // ---- Call events -----------------------------------------------------------------------

    private void OnCallUpdated(ICall sender, ResourceEventArgs<Call> args)
    {
        _logger.LogInformation("Call {CallId} state {State}.", sender.Id, sender.Resource?.State);
    }

    private void OnParticipantsUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
    {
        foreach (IParticipant participant in args.AddedResources)
        {
            TrackParticipant(participant);
            _logger.LogInformation(
                "Participant joined: {Name}",
                participant.Resource?.Info?.Identity?.User?.DisplayName ?? "(unknown)");
        }

        foreach (IParticipant participant in args.RemovedResources)
        {
            Meeting.RemoveParticipant(participant.Id);
        }

        if (_facilitatorLoop is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ApplyExternalPolicyAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Applying the external-participant policy failed.");
                }
            });
        }
    }

    private void SendKeepAlive()
    {
        _ = _call.KeepAliveAsync().ContinueWith(
            t => _logger.LogWarning(t.Exception, "Keep-alive failed for call {CallId}.", _call.Id),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // ---- Teardown --------------------------------------------------------------------------

    /// <summary>Final notes pass and recap, run before the voice session is torn down.</summary>
    private async Task WrapUpAsync()
    {
        if (Meeting.RecentTurns(1).Count == 0)
        {
            return;
        }

        // Graph only allows recording status for compliance-recording bots, so the spoken and
        // written join notice is the disclosure for everyone else.
        if (!_recordingSet && !_noticeGiven)
        {
            _logger.LogWarning("Participants were never told notes were being taken; not posting a recap.");
            return;
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
        await _scribe.UpdateAsync(Meeting, timeout.Token).ConfigureAwait(false);

        int emailed = 0;
        int spoken = Meeting.RecentTurns(600).Count(t => !t.IsAgent);
        if (_facilitator.EmailRecapOnEnd && spoken >= _facilitator.MinTurnsForEmail)
        {
            try
            {
                MeetingReport? report = await _scribe.BuildReportAsync(Meeting, timeout.Token).ConfigureAwait(false);
                if (report is not null)
                {
                    emailed = await _mailer.SendAsync(Meeting, report, _options.Voice.AgentName, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recap email failed.");
            }
        }

        if (_facilitator.PostRecapOnEnd)
        {
            string recap = MeetingScribe.RenderRecap(Meeting, _options.Voice.AgentName);
            if (emailed > 0)
            {
                recap += $"\n\n_Full recap with insights emailed to {emailed} attendee{(emailed == 1 ? string.Empty : "s")}._";
            }

            await PostToChatAsync(recap, timeout.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        _keepAlive.Dispose();

        _call.OnUpdated -= OnCallUpdated;
        _call.Participants.OnUpdated -= OnParticipantsUpdated;

        if (_audioSocket is not null)
        {
            _audioSocket.AudioMediaReceived -= OnAudioMediaReceived;
            _audioSocket.DominantSpeakerChanged -= OnDominantSpeakerChanged;
        }

        _inbound.Writer.TryComplete();
        _cts.Cancel();
        Tools.DisposeTimers();

        // Called synchronously from a media-platform callback, so the teardown runs off this
        // thread and the token source outlives every task that still holds it.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_inboundPump is not null)
                {
                    await _inboundPump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }

                if (_facilitatorLoop is not null)
                {
                    await _facilitatorLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }

                if (_pacer is not null)
                {
                    await _pacer.DisposeAsync().ConfigureAwait(false);
                }

                await _bridge.DisposeAsync().ConfigureAwait(false);

                await WrapUpAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error tearing down call {CallId}.", _call.Id);
            }
            finally
            {
                _meetingSpan?.Dispose();
            }
        });
    }
}
