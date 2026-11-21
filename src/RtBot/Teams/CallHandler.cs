using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly MeetingAgendaLoader _agendaLoader;
    private readonly SkillLibrary _skills;
    private readonly IReadOnlyList<MeetingSkill> _availableSkills;
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
    private VideoTilePump? _tile;
    private VisualCapture? _visual;
    private string? _caption;
    private DateTimeOffset? _captionAt;
    private Task? _inboundPump;
    private Task? _facilitatorLoop;
    private IAudioSocket? _audioSocket;
    private int _disposed;
    private string _currentSpeaker = "Someone";
    private DateTimeOffset? _aloneSince;
    private DateTimeOffset _lastScribe = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastLookup = DateTimeOffset.MinValue;
    private int _lookupRunning;
    private TextModelClient? _textModel;
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
        MeetingAgendaLoader agendaLoader,
        SkillLibrary skills,
        RtBotOptions options,
        string? joinUrl,
        ILogger logger)
    {
        _call = call;
        _bridge = bridge;
        _chat = chat;
        _details = details;
        _mailer = mailer;
        _agendaLoader = agendaLoader;
        _skills = skills;
        _textModel = textModel;
        _options = options;
        _facilitator = options.Facilitator;
        _joinUrl = joinUrl;
        _logger = logger;

        Meeting = new MeetingContext(call.Id, call.Resource?.ChatInfo?.ThreadId, options.AzureAd.TenantId)
        {
            Profile = skills.LoadProfile(),
        };
        _availableSkills = skills.LoadSkills();
        Tools = new MeetingToolset(
            catalog,
            Meeting,
            this,
            textModel,
            _facilitator.ScribeModel,
            _facilitator.EnableScreenVision,
            options.Voice.AgentName,
            _availableSkills,
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

    /// <summary>The live video tile, when the call has a video socket.</summary>
    public VideoTilePump? Tile => _tile;

    /// <summary>Whether the agent's spoken greeting and chat notice have gone out.</summary>
    public bool NoticeGiven => _noticeGiven;

    private TileState BuildTileState()
    {
        MeetingNotes notes = Meeting.SnapshotNotes();
        TimeSpan? length = Meeting.ScheduledStart is { } s && Meeting.ScheduledEnd is { } e && e > s ? e - s : null;
        return new TileState(
            _options.Voice.AgentName,
            _bridge.Presence,
            Meeting.Subject,
            Meeting.Elapsed,
            Meeting.Remaining,
            length,
            Meeting.SnapshotAgenda(),
            Meeting.ActiveTimer,
            notes.Decisions,
            notes.Actions,
            Meeting.ParkingLot,
            Meeting.Tracked,
            Meeting.Participants.Count,
            Meeting.IsRestricted,
            _caption,
            _captionAt,
            Meeting.SpecialismLabel,
            Meeting.CurrentView,
            Meeting.Visuals.LastOrDefault(),
            Meeting.Insights);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _audioSocket = _call.GetLocalMediaSession().AudioSocket
            ?? throw new InvalidOperationException("The call has no audio socket.");

        _pacer = new OutboundAudioPacer(_audioSocket, _logger);
        _pacer.Start();

        if (_call.GetLocalMediaSession().VideoSockets?.FirstOrDefault() is { } videoSocket)
        {
            _tile = new VideoTilePump(videoSocket, BuildTileState, _logger);
            _tile.Start();
        }

        ILocalMediaSession media = _call.GetLocalMediaSession();
        _visual = new VisualCapture(media.VideoSockets?.FirstOrDefault(), media.VbssSocket, _logger);

        _bridge.AgentSpoke += text =>
        {
            _caption = text;
            _captionAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        };

        // The roster may already be populated by the time the handler exists.
        foreach (IParticipant participant in _call.Participants)
        {
            TrackParticipant(participant);
        }

        await LoadMeetingDetailsAsync(cancellationToken).ConfigureAwait(false);

        if (Meeting.ThreadId is { } thread)
        {
            IReadOnlyList<AgendaItem> agenda = await _agendaLoader
                .LoadAsync(thread, _joinUrl, _facilitator.ScribeModel, cancellationToken).ConfigureAwait(false);
            if (agenda.Count > 0)
            {
                Meeting.SetAgenda(agenda);
            }
        }

        IReadOnlyList<MeetingSkill> chosen = await _skills.SelectAsync(
            _availableSkills, Meeting.Profile, Meeting.Subject, Meeting.SnapshotAgenda().Items, cancellationToken).ConfigureAwait(false);
        foreach (MeetingSkill skill in chosen)
        {
            Meeting.ActivateSkill(skill);
        }

        _logger.LogWarning(
            "Profile {Profile}; skills available {Available}; active {Active}.",
            Meeting.Profile.Name,
            _availableSkills.Count,
            string.Join(", ", chosen.Select(s => s.Name)));

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

    public Task SkillsChangedAsync(CancellationToken cancellationToken) =>
        _bridge.RefreshSessionAsync(cancellationToken);

    private async Task ActOnScribeAsync(ScribeOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.SkillShift is { } shift &&
            _availableSkills.FirstOrDefault(s => s.Name.Equals(shift, StringComparison.OrdinalIgnoreCase)) is { } skill &&
            Meeting.ActivateSkill(skill))
        {
            Meeting.AddInsight(new MeetingInsight("skill", "info", "Specialist skill activated", $"{skill.Name}: {skill.Description}", null, DateTimeOffset.UtcNow));
            await _bridge.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Skill {Skill} activated from conversation.", skill.Name);
        }

        // Live knowledge: look it up quietly and put it on the tile; Aria mentions it only if asked or it matters.
        if (outcome.Lookup is { } question &&
            _facilitator.LiveKnowledge &&
            !Meeting.IsRestricted &&
            DateTimeOffset.UtcNow - _lastLookup > TimeSpan.FromSeconds(_facilitator.KnowledgeCooldownSeconds) &&
            Tools.AvailableSpecs().Any(s => s.Name == "workiq_ask") &&
            Interlocked.CompareExchange(ref _lookupRunning, 1, 0) == 0)
        {
            _lastLookup = DateTimeOffset.UtcNow;
            _ = Task.Run(() => LookupKnowledgeAsync(question, _cts.Token), CancellationToken.None);
        }
    }

    private async Task LookupKnowledgeAsync(string question, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            string raw = await Tools.InvokeAsync("workiq_ask", JsonSerializer.Serialize(new { question }), timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains("\"error\"", StringComparison.Ordinal) || _textModel is null)
            {
                _logger.LogWarning("Live knowledge lookup returned nothing usable for: {Question}", question);
                return;
            }

            JsonNode? card = await _textModel.CompleteJsonAsync(
                _facilitator.ScribeModel,
                """
                Turn an answer from the organisation's Microsoft 365 data into a short card for a live meeting.
                Return JSON: {"relevant": true|false, "title": "3-6 words", "text": "the useful fact in at most two sentences", "source": "the document, email, meeting or chat it came from, if named, else null"}
                Set relevant to false if the answer is empty, says nothing was found, or doesn't answer the question.
                """,
                $"QUESTION: {question}\nANSWER: {(raw.Length > 6000 ? raw[..6000] : raw)}",
                timeout.Token).ConfigureAwait(false);

            if (card?["relevant"]?.GetValue<bool>() != true)
            {
                _logger.LogWarning("Live knowledge lookup not relevant for: {Question}", question);
                return;
            }

            string? source = card["source"] is JsonValue sv && sv.TryGetValue(out string? s) ? s : null;

            // An external guest may have joined while the lookup ran.
            if (Meeting.IsRestricted)
            {
                _logger.LogWarning("Discarded a live knowledge result: the meeting became restricted.");
                return;
            }

            bool added = Meeting.AddInsight(new MeetingInsight(
                "knowledge",
                "info",
                card["title"]?.GetValue<string>() ?? "From your Microsoft 365 data",
                card["text"]?.GetValue<string>() ?? string.Empty,
                $"Work IQ{(string.IsNullOrWhiteSpace(source) ? string.Empty : " \u00B7 " + source)}",
                DateTimeOffset.UtcNow));
            _logger.LogWarning("Live knowledge surfaced ({Added}) for: {Question}", added, question);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Live knowledge lookup failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _lookupRunning, 0);
        }
    }

    public Task<bool> PostToChatAsync(string markdown, CancellationToken cancellationToken) =>
        Meeting.ThreadId is { } thread
            ? _chat.PostAsync(thread, markdown, cancellationToken)
            : Task.FromResult(false);

    public async Task<(byte[]? Jpeg, string? From, string? Failure)> CaptureVisualAsync(bool screen, CancellationToken cancellationToken)
    {
        if (_visual is null)
        {
            return (null, null, "Video isn't available in this call.");
        }

        // Screen: whoever is sharing. Camera: the current speaker if their camera is on, then anyone else's.
        Modality wanted = screen ? Modality.VideoBasedScreenSharing : Modality.Video;
        List<(IParticipant P, MediaStream S)> sources = [.. _call.Participants
            .Where(p => p.Resource?.IsInLobby != true && p.Resource?.Info?.Identity?.Application is null)
            .SelectMany(p => (p.Resource?.MediaStreams ?? []).Select(s => (p, s)))
            .Where(x => x.s.MediaType == wanted &&
                        x.s.Direction is MediaDirection.SendOnly or MediaDirection.SendReceive &&
                        x.s.SourceId is not null && uint.TryParse(x.s.SourceId, out _))];

        if (sources.Count == 0)
        {
            _logger.LogWarning("No {Kind} stream to capture.", screen ? "screen-share" : "camera");
            return (null, null, screen
                ? "Nobody is sharing their screen."
                : "Nobody has their camera on. Ask the person to turn their camera on and hold it up again.");
        }

        IEnumerable<(IParticipant P, MediaStream S)> ordered = sources
            .OrderByDescending(x => string.Equals(x.P.Resource?.Info?.Identity?.User?.DisplayName, _currentSpeaker, StringComparison.OrdinalIgnoreCase))
            .Take(2);

        // The first stream gets the longest wait (keyframe, ramp-up); fallbacks get less so the
        // whole attempt stays inside the voice tool timeout.
        TimeSpan wait = TimeSpan.FromSeconds(12);
        foreach ((IParticipant p, MediaStream s) in ordered)
        {
            string? from = p.Resource?.Info?.Identity?.User?.DisplayName;
            byte[]? jpeg = await _visual.CaptureAsync(screen, uint.Parse(s.SourceId!), wait, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Capture {Kind} from {From}: {Bytes} bytes.", screen ? "screen" : "camera", from, jpeg?.Length ?? 0);
            if (jpeg is not null)
            {
                return (jpeg, from, null);
            }

            wait = TimeSpan.FromSeconds(6);
        }

        return (null, null, screen
            ? "The screen share sent no picture in time. Ask them to keep sharing and try again."
            : "The camera sent no picture in time. Ask the person to hold it steady in front of their camera and try again.");
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
            $"Introduce yourself in one sentence as {name}, the AI facilitator. Say you're taking notes, and people can ask you anything by saying your name." +
            (Meeting.SnapshotAgenda().Current is { } first
                ? $" Then say you have the agenda from the invite, {Meeting.SnapshotAgenda().Items.Count} items, starting with '{first.Title}', and you'll keep time."
                : string.Empty),
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
            ScribeOutcome outcome = await _scribe.UpdateAsync(Meeting, cancellationToken, _availableSkills).ConfigureAwait(false);
            await ActOnScribeAsync(outcome, cancellationToken).ConfigureAwait(false);
        }

        if (_facilitator.ProactiveTimeChecks && Meeting.TryClaimOverrun(out AgendaItem? overrun) && overrun is not null)
        {
            AgendaItem? next = Meeting.SnapshotAgenda().Next;
            string ask = next is null
                ? "and ask whether to wrap it up or take a couple more minutes."
                : $"and ask whether to move on to '{next.Title}', take a couple more minutes, or park the rest.";
            await _bridge.AnnounceAsync($"Politely say the timebox for '{overrun.Title}' is up, {ask}", cancellationToken).ConfigureAwait(false);
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
                IReadOnlyList<string> uncovered = Meeting.UncoveredChecklist;
                await _bridge.AnnounceAsync(
                    $"Give a brief heads-up that about {(int)Math.Ceiling((end - now).TotalMinutes)} minutes remain" +
                    (open > 0 ? $", there are {open} open questions" : string.Empty) +
                    (uncovered.Count > 0 ? $", and these haven't been covered yet: {string.Join("; ", uncovered.Take(3))}" : string.Empty) +
                    ".",
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
        // Only the agent talked (e.g. it was alone and left): nothing worth recapping.
        if (!Meeting.RecentTurns(600).Any(t => !t.IsAgent))
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

                if (_tile is not null)
                {
                    await _tile.DisposeAsync().ConfigureAwait(false);
                }

                _visual?.Dispose();

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
