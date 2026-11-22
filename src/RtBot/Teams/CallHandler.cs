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
    private readonly MeetingScheduler _scheduler;
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
    private readonly object _lookupGate = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lookupQueue = new();
    private readonly HashSet<string> _askedLookups = [];
    private readonly Queue<string> _askedOrder = new();
    private TextModelClient? _textModel;
    private bool _halfwayAnnounced;
    private bool _wrapUpAnnounced;
    private bool _externalAnnounced;
    private bool _recordingSet;
    private volatile bool _noticeGiven;
    private int _greetStarted;

    public CallHandler(
        ICall call,
        VoiceLiveBridge bridge,
        ToolCatalog catalog,
        TextModelClient textModel,
        BotChatClient chat,
        MeetingDetailsLookup details,
        MeetingMailer mailer,
        MeetingScheduler scheduler,
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
        _scheduler = scheduler;
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

    public string AgentName => _options.Voice.AgentName;

    public AgentPresence Presence => _bridge.Presence;

    public bool IsAgentSpeaking => _bridge.IsAgentSpeaking;

    public string? LatestCaption =>
        !string.IsNullOrWhiteSpace(_caption) && DateTimeOffset.UtcNow - _captionAt < TimeSpan.FromSeconds(16)
            ? _caption
            : null;

    /// <summary>Link to this call's companion dashboard, or null when the public host is not configured.</summary>
    private string? DashboardUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_options.Media.ServiceFqdn))
            {
                return null;
            }

            string portSuffix = _options.Media.SignalingPublicPort == 443 ? string.Empty : ":" + _options.Media.SignalingPublicPort;
            string hostOrigin = $"https://{_options.Media.ServiceFqdn.Trim()}{portSuffix}";
            if (!string.IsNullOrWhiteSpace(_options.Avatar.CompanionAppUrl))
            {
                return $"{_options.Avatar.CompanionAppUrl.TrimEnd('/')}?host={Uri.EscapeDataString(hostOrigin)}&callId={Uri.EscapeDataString(_call.Id)}#k={Meeting.ViewKey}";
            }

            return $"{hostOrigin}/live/{Uri.EscapeDataString(_call.Id)}#k={Meeting.ViewKey}";
        }
    }

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
            Meeting.IsRestricted
                ? [.. Meeting.Insights.Where(i => !i.Kind.Equals("knowledge", StringComparison.OrdinalIgnoreCase))]
                : Meeting.Insights,
            Meeting.Alignment,
            Meeting.AlignmentNote,
            Meeting.AlignmentTrend,
            Meeting.Voices(),
            Meeting.IsRestricted ? null : Meeting.Searching,
            Meeting.ActiveSkillBadges,
            Meeting.Milestones);
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

        if (chosen.Count > 0)
        {
            Meeting.AddInsight(new MeetingInsight(
                "skill",
                "info",
                "Specialist skills loaded",
                string.Join("; ", chosen.Select(s => $"{s.Name}: {s.Description}")),
                null,
                DateTimeOffset.UtcNow));
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
        await ApplyExternalPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (Meeting.Participants.Count > 0 &&
            !(_facilitator.ExternalParticipants == ExternalParticipantPolicy.Leave && Meeting.HasExternalParticipants))
        {
            await GreetOnceAsync(cancellationToken).ConfigureAwait(false);
        }

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

    public async Task<ScheduledFollowUp> ScheduleFollowUpAsync(
        string? subject,
        double? startInMinutes,
        string? startAtIso,
        double durationMinutes,
        IReadOnlyList<string> additionalAttendees,
        IReadOnlyList<string> agendaItems,
        CancellationToken cancellationToken)
    {
        ScheduledFollowUp booked = await _scheduler.ScheduleAsync(
            Meeting, subject, startInMinutes, startAtIso, durationMinutes, additionalAttendees, agendaItems, cancellationToken).ConfigureAwait(false);

        if (booked.Succeeded)
        {
            Meeting.AddDecision($"Follow-up booked: {booked.Summary}");
            if (!string.IsNullOrWhiteSpace(booked.JoinUrl))
            {
                _ = PostToChatAsync(
                    $"**Follow-up meeting scheduled:** {booked.Summary}  \n[Join link]({booked.JoinUrl})",
                    _cts.Token);
            }
        }

        return booked;
    }

    private async Task ActOnScribeAsync(ScribeOutcome outcome, CancellationToken cancellationToken)
    {
        Meeting.RecordScribePass();
        List<MeetingSkill> newlyActivated = [];
        foreach (string shift in outcome.SkillShifts)
        {
            if (_availableSkills.FirstOrDefault(s => s.Name.Equals(shift, StringComparison.OrdinalIgnoreCase)) is { } skill &&
                Meeting.ActivateSkill(skill, dynamic: true))
            {
                newlyActivated.Add(skill);
                Meeting.AddInsight(new MeetingInsight(
                    "skill",
                    "info",
                    $"Skill activated: {MeetingContext.Titleise(skill.Name)}",
                    skill.Description,
                    MeetingContext.Titleise(skill.Name),
                    DateTimeOffset.UtcNow));
                _logger.LogWarning("Skill {Skill} activated from conversation.", skill.Name);
            }
        }

        if (newlyActivated.Count > 0)
        {
            await _bridge.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
            foreach (MeetingSkill skill in newlyActivated)
            {
                MeetingSkill captured = skill;
                _ = Task.Run(() => ApplySkillLensAsync(captured, _cts.Token), CancellationToken.None);
            }
        }

        EnqueueLookups(outcome.Lookups);
    }

    private void EnqueueLookups(IEnumerable<string> questions)
    {
        if (!_facilitator.LiveKnowledge ||
            Meeting.IsRestricted ||
            !Tools.AvailableSpecs().Any(s => s.Name is "workiq_retrieve" or "workiq_ask"))
        {
            return;
        }

        lock (_lookupGate)
        {
            foreach (string question in questions)
            {
                string key = question.Trim().ToLowerInvariant();
                if (key.Length > 6 && _lookupQueue.Count < 4 && _askedLookups.Add(key))
                {
                    _askedOrder.Enqueue(key);
                    if (_askedOrder.Count > 40)
                    {
                        _askedLookups.Remove(_askedOrder.Dequeue());
                    }

                    _lookupQueue.Enqueue(question.Trim());
                }
            }
        }

        if (!_lookupQueue.IsEmpty && Interlocked.CompareExchange(ref _lookupRunning, 1, 0) == 0)
        {
            _ = Task.Run(() => DrainLookupsAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <summary>Immediately evaluates recent conversation through a newly activated specialist skill.</summary>
    private async Task ApplySkillLensAsync(MeetingSkill skill, CancellationToken cancellationToken)
    {
        if (_textModel is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<TranscriptTurn> recent = Meeting.RecentTurns(12).Where(t => !t.IsAgent).ToList();
            if (recent.Count == 0)
            {
                return;
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            string turns = string.Join("\n", recent.Select(t => $"{t.Speaker}: {t.Text}"));
            JsonNode? node = await _textModel.CompleteJsonAsync(
                _facilitator.ScribeModel,
                """
                Apply the specialist meeting skill below to the recent conversation lines.
                Return JSON: {"has_insight": true|false, "severity": "high|medium|info", "title": "3-6 words", "text": "one crisp actionable sentence grounded in what was said", "lookup": "one focused M365 search query (4-10 keywords) if an internal playbook, policy, contract or report would help, else null"}
                Set has_insight to false if there is nothing concrete to add yet.
                """,
                $"SKILL: {skill.Name} - {skill.Description}\nGUIDANCE:\n{(skill.Guidance.Length > 1600 ? skill.Guidance[..1600] : skill.Guidance)}\n\nRECENT CONVERSATION:\n{turns}",
                timeout.Token).ConfigureAwait(false);

            if (node?["has_insight"]?.GetValue<bool>() == true)
            {
                string? title = node["title"]?.GetValue<string>();
                string? text = node["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(text))
                {
                    Meeting.AddInsight(new MeetingInsight(
                        "skill",
                        node["severity"]?.GetValue<string>() ?? "info",
                        title,
                        text,
                        MeetingContext.Titleise(skill.Name),
                        DateTimeOffset.UtcNow));
                }
            }

            if (node?["lookup"] is JsonValue lv && lv.TryGetValue(out string? lookup) && !string.IsNullOrWhiteSpace(lookup))
            {
                EnqueueLookups([lookup]);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Immediate skill-lens evaluation failed for {Skill}.", skill.Name);
        }
        catch (OperationCanceledException)
        {
            // Call ended.
        }
    }

    private async Task DrainLookupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !Meeting.IsRestricted && _lookupQueue.TryDequeue(out string? question))
            {
                TimeSpan wait = TimeSpan.FromSeconds(_facilitator.KnowledgeCooldownSeconds) - (DateTimeOffset.UtcNow - _lastLookup);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                // A guest may have joined during the wait.
                if (Meeting.IsRestricted)
                {
                    break;
                }

                _lastLookup = DateTimeOffset.UtcNow;
                Meeting.Searching = question;
                try
                {
                    await LookupKnowledgeAsync(question, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Meeting.Searching = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Call ended.
        }
        finally
        {
            // Questions queued before a guest arrived must not run after they leave.
            if (Meeting.IsRestricted)
            {
                _lookupQueue.Clear();
            }

            Interlocked.Exchange(ref _lookupRunning, 0);

            if (!cancellationToken.IsCancellationRequested &&
                !Meeting.IsRestricted &&
                !_lookupQueue.IsEmpty &&
                Interlocked.CompareExchange(ref _lookupRunning, 1, 0) == 0)
            {
                _ = Task.Run(() => DrainLookupsAsync(cancellationToken), CancellationToken.None);
            }
        }
    }

    private async Task LookupKnowledgeAsync(string question, CancellationToken cancellationToken)
    {
        if (_textModel is null)
        {
            return;
        }

        try
        {
            string? raw = null;
            IReadOnlyList<RtBot.Tools.ToolSpec> specs = Tools.AvailableSpecs();

            // Prefer workiq_retrieve (~7 s semantic M365 search); fall back to workiq_ask only if needed.
            if (specs.Any(s => s.Name == "workiq_retrieve"))
            {
                using CancellationTokenSource retrieveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                retrieveTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    string candidate = await Tools
                        .InvokeAsync("workiq_retrieve", JsonSerializer.Serialize(new { query = question }), retrieveTimeout.Token)
                        .ConfigureAwait(false);
                    Meeting.RecordWorkIqCall(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (!IsToolError(candidate) && !IsEmptyRetrieval(candidate))
                    {
                        raw = candidate;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "workiq_retrieve failed for '{Question}'; trying workiq_ask.", question);
                }
            }

            if (raw is null && specs.Any(s => s.Name == "workiq_ask"))
            {
                using CancellationTokenSource askTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                askTimeout.CancelAfter(TimeSpan.FromSeconds(65));
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                string candidate = await Tools
                    .InvokeAsync("workiq_ask", JsonSerializer.Serialize(new { question }), askTimeout.Token)
                    .ConfigureAwait(false);
                Meeting.RecordWorkIqCall(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (!IsToolError(candidate))
                {
                    raw = candidate;
                }
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Live knowledge lookup returned nothing usable for: {Question}", question);
                return;
            }

            // Separate timeout so a slow Work IQ call never starves card synthesis.
            using CancellationTokenSource synthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            synthTimeout.CancelAfter(TimeSpan.FromSeconds(20));

            JsonNode? card = await _textModel.CompleteJsonAsync(
                _facilitator.ScribeModel,
                """
                Turn M365 search passages or answers from Work IQ into a crisp, high-value card for a live meeting.
                Return JSON: {"relevant": true|false, "title": "3-6 words", "text": "the concrete operational fact, deadline, SLA or policy rule in at most two short sentences", "source": "short title of the email, playbook, document or meeting it came from, if named, else null"}
                Prefer authoritative playbooks, policies, contracts and operational reports. Ignore AI-generated 'Meeting recap:' emails unless QUERY specifically asks about a previous meeting; if only an AI meeting recap matched, set relevant to false.
                Set relevant to false if the results are unrelated, empty, or say nothing was found.
                """,
                $"QUERY: {question}\nWORK IQ RESULTS:\n{(raw.Length > 8000 ? raw[..8000] : raw)}",
                synthTimeout.Token).ConfigureAwait(false);

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
    }

    private static bool IsToolError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        ReadOnlySpan<char> trimmed = raw.AsSpan().TrimStart();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(raw) is JsonObject obj && obj.ContainsKey("error");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEmptyRetrieval(string raw) =>
        raw.Contains("\"results\":[]", StringComparison.Ordinal) ||
        raw.Contains("\"results\": []", StringComparison.Ordinal) ||
        raw.Contains("The tool returned no content.", StringComparison.Ordinal);

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

    private readonly SemaphoreSlim _boardSummaryGate = new(1, 1);
    private string? _cachedBoardSummary;
    private DateTimeOffset _cachedBoardSummaryAt;
    private bool _cachedBoardSummaryRestricted;

    /// <summary>Synthesises a concise 20-second spoken executive summary from the live meeting board (throttled to 1 synthesis per 20 s).</summary>
    public async Task<string> SynthesiseBoardSummaryAsync(CancellationToken cancellationToken)
    {
        MeetingNotes notes = Meeting.SnapshotNotes();
        if (_textModel is null)
        {
            return !string.IsNullOrWhiteSpace(notes.Summary)
                ? notes.Summary
                : $"We are {Math.Max(1, (int)Meeting.Elapsed.TotalMinutes)} minutes into {Meeting.Subject ?? "the session"} with {notes.Decisions.Count} locked decisions and {notes.Actions.Count} assigned actions.";
        }

        await _boardSummaryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool restrictedNow = Meeting.IsRestricted;
            if (!string.IsNullOrWhiteSpace(_cachedBoardSummary) &&
                _cachedBoardSummaryRestricted == restrictedNow &&
                DateTimeOffset.UtcNow - _cachedBoardSummaryAt < TimeSpan.FromSeconds(20))
            {
                return _cachedBoardSummary;
            }

            JsonNode? result = await _textModel.CompleteJsonAsync(
                _facilitator.ScribeModel,
                """
                Write a crisp 20-second spoken executive status summary (45 to 65 words, natural British English, no bullet points, no markdown) for Aria, the AI meeting facilitator.
                State current alignment, key locked decisions or active risks, and the single highest-priority next action.
                Return JSON: {"speech": "..."}
                """,
                Meeting.RenderBriefing(),
                cancellationToken).ConfigureAwait(false);

            string? speech = result?["speech"]?.GetValue<string>();
            string resolved = !string.IsNullOrWhiteSpace(speech) ? speech : notes.Summary;
            _cachedBoardSummary = resolved;
            _cachedBoardSummaryAt = DateTimeOffset.UtcNow;
            _cachedBoardSummaryRestricted = restrictedNow;
            return resolved;
        }
        finally
        {
            _boardSummaryGate.Release();
        }
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

    private Task GreetOnceAsync(CancellationToken cancellationToken) =>
        Interlocked.CompareExchange(ref _greetStarted, 1, 0) == 0
            ? GreetAsync(cancellationToken)
            : Task.CompletedTask;

    private async Task GreetAsync(CancellationToken cancellationToken)
    {
        Meeting.ResetInitialAgendaClockIfNoSpeech();
        if (!_facilitator.AnnounceOnJoin)
        {
            return;
        }

        string name = _options.Voice.AgentName;

        bool posted = await PostToChatAsync(
            $"""
            **{name} (AI facilitator) has joined and is taking notes.**
            Say "{name}" to ask a question out loud, or @mention me here. I'm keeping notes, decisions and action items{(_facilitator.PostRecapOnEnd ? " and will post a recap when the meeting ends" : string.Empty)}. Ask me to leave at any time.{(DashboardUrl is { } dash ? $"\n\n[Open the live dashboard]({dash}) for my insights, skills and findings from your Microsoft 365 data. The link is for people in this meeting only." : string.Empty)}
            """,
            cancellationToken).ConfigureAwait(false);

        AgendaStatus openingAgenda = Meeting.SnapshotAgenda();
        string greetingInstruction = openingAgenda.Current is { } first
            ? $"Introduce yourself in one crisp sentence as {name}, the AI facilitator for '{Meeting.Subject ?? "today's session"}'. Confirm you already have the {openingAgenda.Items.Count}-item agenda from the invite and walk us straight into item 1: '{first.Title}'{(first.Minutes is { } fm ? $" ({fm:0.#} minutes)" : string.Empty)}, inviting the group to kick off. NEVER ask what the agenda is."
            : $"Introduce yourself in one sentence as {name}, the AI facilitator. Say you're taking notes and people can address you by name at any time.";

        await _bridge.AnnounceAsync(greetingInstruction, cancellationToken).ConfigureAwait(false);

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

        if (_facilitator.ProactiveTimeChecks && Meeting.Participants.Count > 0 && Meeting.TryClaimOverrun(out AgendaItem? overrun) && overrun is not null)
        {
            AgendaItem? next = Meeting.SnapshotAgenda().Next;
            string ask = next is null
                ? "and ask whether to wrap it up or take a couple more minutes."
                : $"and ask whether to move on to '{next.Title}', take a couple more minutes, or park the rest.";
            await _bridge.AnnounceAsync($"Politely say the timebox for '{overrun.Title}' is up, {ask}", cancellationToken).ConfigureAwait(false);
        }

        if (_facilitator.ProactiveTimeChecks && Meeting.Participants.Count > 0 && Meeting.ScheduledStart is { } start && Meeting.ScheduledEnd is { } end && end > start)
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
            TimeSpan patience = Meeting.Attendees.Count == 0
                ? TimeSpan.FromMinutes(_facilitator.WaitForFirstAttendeeMinutes)
                : TimeSpan.FromSeconds(_facilitator.LeaveWhenAloneSeconds);
            if (now - _aloneSince.Value >= patience)
            {
                _logger.LogInformation("Nobody in the meeting after {Seconds}s; leaving.", (int)(now - _aloneSince.Value).TotalSeconds);
                await LeaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            _aloneSince = null;
            if (!(_facilitator.ExternalParticipants == ExternalParticipantPolicy.Leave && Meeting.HasExternalParticipants) &&
                !_cts.IsCancellationRequested)
            {
                await GreetOnceAsync(cancellationToken).ConfigureAwait(false);
            }
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
                    if (Meeting.Participants.Count > 0 &&
                        !(_facilitator.ExternalParticipants == ExternalParticipantPolicy.Leave && Meeting.HasExternalParticipants) &&
                        !_cts.IsCancellationRequested)
                    {
                        await GreetOnceAsync(_cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Applying participant update failed.");
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
