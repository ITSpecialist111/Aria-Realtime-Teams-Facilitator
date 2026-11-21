using System.Text;

namespace RtBot.Facilitator;

/// <summary>One finished utterance, attributed to whoever was speaking when it started.</summary>
public sealed record TranscriptTurn(DateTimeOffset At, string Speaker, string Text, bool IsAgent);

public sealed record ActionItem(string Owner, string Task, string? Due, DateTimeOffset RecordedAt);

public sealed record MeetingParticipant(string Id, string Name, string? TenantId, bool IsExternal, string? UserId = null);

public sealed record AgendaItem(string Title, double? Minutes);

public sealed record MeetingTimer(string Label, DateTimeOffset EndsAt);

/// <summary>Something Aria noticed or found: a risk, a facilitation nudge, organisation knowledge, a skill switch.</summary>
public sealed record MeetingInsight(
    string Kind,
    string Severity,
    string Title,
    string Text,
    string? Citation,
    DateTimeOffset At);

/// <summary>A still captured from a camera or screen share, with what the vision model made of it.</summary>
public sealed record VisualNote(
    string Title,
    string Summary,
    IReadOnlyList<string> KeyPoints,
    string Source,
    string? CapturedFrom,
    byte[] Jpeg,
    DateTimeOffset At);

/// <summary>Where the agenda stands right now.</summary>
public sealed record AgendaStatus(
    IReadOnlyList<AgendaItem> Items,
    int CurrentIndex,
    TimeSpan CurrentElapsed,
    IReadOnlyList<int> Completed)
{
    public AgendaItem? Current => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    public AgendaItem? Next => CurrentIndex + 1 < Items.Count ? Items[CurrentIndex + 1] : null;

    public TimeSpan? CurrentRemaining => Current?.Minutes is { } m ? TimeSpan.FromMinutes(m) - CurrentElapsed : null;
}

/// <summary>
/// Everything the agent knows about one meeting while it is in it.
/// </summary>
/// <remarks>
/// The realtime model hears audio but cannot tell speakers apart, has no clock, and forgets the
/// early part of a long meeting when its context window fills. This is the durable record that
/// fills those gaps: it is what gets injected back into the model and what the recap is built from.
/// </remarks>
public sealed class MeetingContext
{
    private const int MaxTurns = 600;

    private readonly object _gate = new();
    private readonly List<TranscriptTurn> _turns = [];
    private readonly Dictionary<string, MeetingParticipant> _participants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MeetingParticipant> _attendees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _talkTime = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _decisions = [];
    private readonly List<ActionItem> _actions = [];
    private readonly List<string> _openQuestions = [];
    private readonly List<AgendaItem> _agenda = [];
    private readonly List<int> _agendaDone = [];
    private readonly List<string> _parkingLot = [];
    private readonly List<MeetingTimer> _timers = [];
    private readonly List<MeetingSkill> _skills = [];
    private readonly Dictionary<string, List<string>> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _covered = new(StringComparer.OrdinalIgnoreCase);

    private int _agendaIndex = -1;
    private DateTimeOffset _agendaItemStartedAt;
    private int _overrunAnnouncedFor = -1;

    private string _summary = string.Empty;
    private int _scribedTurnCount;

    public MeetingContext(string callId, string? threadId, string tenantId)
    {
        CallId = callId;
        ThreadId = threadId;
        TenantId = tenantId;
        JoinedAt = DateTimeOffset.UtcNow;
    }

    public string CallId { get; }

    public string? ThreadId { get; }

    /// <summary>The agent's home tenant; anyone else is external.</summary>
    public string TenantId { get; }

    public DateTimeOffset JoinedAt { get; }

    public string? Subject { get; set; }

    public DateTimeOffset? ScheduledStart { get; set; }

    public DateTimeOffset? ScheduledEnd { get; set; }

    /// <summary>Set while someone from outside the organisation is present and the policy restricts the agent.</summary>
    public bool IsRestricted { get; set; }

    public AgentProfile Profile { get; set; } = AgentProfile.Default;

    private RtBot.Media.TileView _view;
    private DateTimeOffset _viewSetAt;

    /// <summary>The tile's current view; a breakdown reverts to the overview after 90 seconds, a capture after 45.</summary>
    public RtBot.Media.TileView CurrentView =>
        _view != RtBot.Media.TileView.Overview &&
        DateTimeOffset.UtcNow - _viewSetAt < TimeSpan.FromSeconds(_view == RtBot.Media.TileView.Capture ? 45 : 90)
            ? _view
            : RtBot.Media.TileView.Overview;

    public void ShowView(RtBot.Media.TileView view)
    {
        _viewSetAt = DateTimeOffset.UtcNow;
        _view = view;
    }

    private readonly List<VisualNote> _visuals = [];
    private readonly List<MeetingInsight> _insights = [];

    public IReadOnlyList<MeetingInsight> Insights
    {
        get
        {
            lock (_gate)
            {
                return [.. _insights];
            }
        }
    }

    /// <summary>Adds an insight unless a recent one has the same title. Returns whether it was added.</summary>
    public bool AddInsight(MeetingInsight insight)
    {
        insight = insight with
        {
            Kind = OneLine(insight.Kind, 20).ToLowerInvariant(),
            Severity = insight.Severity.ToLowerInvariant() is "high" or "medium" ? insight.Severity.ToLowerInvariant() : "info",
            Title = OneLine(insight.Title, 80),
            Text = OneLine(insight.Text, 360),
            Citation = insight.Citation is null ? null : OneLine(insight.Citation, 120),
        };

        if (insight.Title.Length == 0 || insight.Text.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_insights.TakeLast(12).Any(i => i.Title.Equals(insight.Title, StringComparison.OrdinalIgnoreCase)) || _insights.Count >= 100)
            {
                return false;
            }

            _insights.Add(insight);
            return true;
        }
    }

    public IReadOnlyList<VisualNote> Visuals
    {
        get
        {
            lock (_gate)
            {
                return [.. _visuals];
            }
        }
    }

    public void AddVisual(VisualNote note)
    {
        note = note with
        {
            Title = OneLine(note.Title, 80),
            Summary = OneLine(note.Summary, 600),
            KeyPoints = [.. note.KeyPoints.Select(p => OneLine(p, 200)).Where(p => p.Length > 0).Take(8)],
        };

        lock (_gate)
        {
            if (_visuals.Count < 20)
            {
                _visuals.Add(note);
            }
        }

        ShowView(RtBot.Media.TileView.Capture);
    }

    public IReadOnlyList<MeetingSkill> ActiveSkills
    {
        get
        {
            lock (_gate)
            {
                return [.. _skills];
            }
        }
    }

    /// <summary>Adds a skill if not already active. Returns false when it was.</summary>
    public bool ActivateSkill(MeetingSkill skill)
    {
        lock (_gate)
        {
            if (_skills.Any(s => s.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _skills.Add(skill);
            return true;
        }
    }

    /// <summary>Items captured under each skill category, e.g. "RFIs" or "Safety".</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Tracked
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Where(kv => kv.Value.Count > 0)
                    .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value], StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void AddTracked(string category, string item)
    {
        category = OneLine(category, 60);
        item = OneLine(item, 300);
        if (category.Length == 0 || item.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_tracked.TryGetValue(category, out List<string>? list))
            {
                if (_tracked.Count >= 30)
                {
                    return;
                }

                _tracked[category] = list = [];
            }

            if (list.Count < 200 && !list.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(item);
            }
        }
    }

    public void MarkCovered(string checklistItem)
    {
        lock (_gate)
        {
            _covered.Add(checklistItem);
        }
    }

    /// <summary>Checklist items from active skills that the meeting has not yet covered.</summary>
    public IReadOnlyList<string> UncoveredChecklist
    {
        get
        {
            lock (_gate)
            {
                return [.. _skills.SelectMany(s => s.Checklist).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !_covered.Contains(c))];
            }
        }
    }

    /// <summary>Short label for the tile, e.g. "Construction · Project governance".</summary>
    public string? SpecialismLabel
    {
        get
        {
            IReadOnlyList<MeetingSkill> skills = ActiveSkills;
            return skills.Count == 0 ? null : string.Join(" \u00B7 ", skills.Select(s => Titleise(s.Name)));
        }
    }

    private static string Titleise(string name) =>
        string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    public TimeSpan Elapsed => DateTimeOffset.UtcNow - (ScheduledStart is { } s && s < JoinedAt ? s : JoinedAt);

    public TimeSpan? Remaining => ScheduledEnd is { } end ? end - DateTimeOffset.UtcNow : null;

    public IReadOnlyList<MeetingParticipant> Participants
    {
        get
        {
            lock (_gate)
            {
                return [.. _participants.Values];
            }
        }
    }

    /// <summary>Everyone who was present at any point, including people who have since left.</summary>
    public IReadOnlyList<MeetingParticipant> Attendees
    {
        get
        {
            lock (_gate)
            {
                return [.. _attendees.Values];
            }
        }
    }

    public string? OrganizerUserId { get; set; }

    public bool HasExternalParticipants
    {
        get
        {
            lock (_gate)
            {
                return _participants.Values.Any(p => p.IsExternal);
            }
        }
    }

    public void UpsertParticipant(MeetingParticipant participant)
    {
        lock (_gate)
        {
            _participants[participant.Id] = participant;
            _attendees[participant.UserId ?? participant.Id] = participant;
        }
    }

    public void RemoveParticipant(string id)
    {
        lock (_gate)
        {
            _participants.Remove(id);
        }
    }

    public void AddTurn(TranscriptTurn turn)
    {
        lock (_gate)
        {
            _turns.Add(turn);

            if (_turns.Count > MaxTurns)
            {
                int drop = _turns.Count - MaxTurns;
                _turns.RemoveRange(0, drop);
                _scribedTurnCount = Math.Max(0, _scribedTurnCount - drop);
            }
        }
    }

    public void AddTalkTime(string speaker, TimeSpan duration)
    {
        lock (_gate)
        {
            _talkTime[speaker] = _talkTime.GetValueOrDefault(speaker) + duration;
        }
    }

    public void AddDecision(string decision)
    {
        decision = OneLine(decision, 300);
        lock (_gate)
        {
            if (decision.Length > 0 && _decisions.Count < 200 && !_decisions.Contains(decision, StringComparer.OrdinalIgnoreCase))
            {
                _decisions.Add(decision);
            }
        }
    }

    public void AddAction(ActionItem action)
    {
        action = action with
        {
            Owner = OneLine(action.Owner, 80),
            Task = OneLine(action.Task, 300),
            Due = action.Due is null ? null : OneLine(action.Due, 60),
        };

        if (action.Owner.Length == 0 || action.Task.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            bool duplicate = _actions.Any(a =>
                a.Owner.Equals(action.Owner, StringComparison.OrdinalIgnoreCase) &&
                a.Task.Equals(action.Task, StringComparison.OrdinalIgnoreCase));

            if (!duplicate && _actions.Count < 200)
            {
                _actions.Add(action);
            }
        }
    }

    /// <summary>Replaces the agenda and starts its first item.</summary>
    public void SetAgenda(IEnumerable<AgendaItem> items)
    {
        lock (_gate)
        {
            _agenda.Clear();
            _agenda.AddRange(items
                .Select(i => new AgendaItem(OneLine(i.Title, 80), i.Minutes is > 0 ? Math.Min(i.Minutes.Value, 480) : null))
                .Where(i => i.Title.Length > 0)
                .Take(30));
            _agendaDone.Clear();
            _agendaIndex = _agenda.Count > 0 ? 0 : -1;
            _agendaItemStartedAt = DateTimeOffset.UtcNow;
            _overrunAnnouncedFor = -1;
        }
    }

    /// <summary>Closes the current item and starts the next. Returns the new current item, or null when the agenda is finished.</summary>
    public AgendaItem? AdvanceAgenda()
    {
        lock (_gate)
        {
            if (_agendaIndex < 0)
            {
                return null;
            }

            if (!_agendaDone.Contains(_agendaIndex))
            {
                _agendaDone.Add(_agendaIndex);
            }

            _agendaIndex++;
            _agendaItemStartedAt = DateTimeOffset.UtcNow;
            if (_agendaIndex >= _agenda.Count)
            {
                _agendaIndex = _agenda.Count;
                return null;
            }

            return _agenda[_agendaIndex];
        }
    }

    /// <summary>Adds time to the current item's timebox.</summary>
    public AgendaItem? ExtendCurrentItem(double minutes)
    {
        lock (_gate)
        {
            if (_agendaIndex < 0 || _agendaIndex >= _agenda.Count)
            {
                return null;
            }

            AgendaItem item = _agenda[_agendaIndex];
            double elapsed = (DateTimeOffset.UtcNow - _agendaItemStartedAt).TotalMinutes;
            _agenda[_agendaIndex] = item with { Minutes = Math.Max(item.Minutes ?? 0, elapsed) + minutes };
            _overrunAnnouncedFor = -1;
            return _agenda[_agendaIndex];
        }
    }

    public AgendaStatus SnapshotAgenda()
    {
        lock (_gate)
        {
            return new AgendaStatus([.. _agenda], _agendaIndex, DateTimeOffset.UtcNow - _agendaItemStartedAt, [.. _agendaDone]);
        }
    }

    /// <summary>True once per item, the first time it is checked after running past its timebox.</summary>
    public bool TryClaimOverrun(out AgendaItem? item)
    {
        lock (_gate)
        {
            item = null;
            if (_agendaIndex < 0 || _agendaIndex >= _agenda.Count || _overrunAnnouncedFor == _agendaIndex)
            {
                return false;
            }

            AgendaItem current = _agenda[_agendaIndex];
            if (current.Minutes is not { } minutes || DateTimeOffset.UtcNow - _agendaItemStartedAt < TimeSpan.FromMinutes(minutes))
            {
                return false;
            }

            _overrunAnnouncedFor = _agendaIndex;
            item = current;
            return true;
        }
    }

    public void ParkTopic(string topic)
    {
        topic = OneLine(topic, 200);
        lock (_gate)
        {
            if (topic.Length > 0 && _parkingLot.Count < 100 && !_parkingLot.Contains(topic, StringComparer.OrdinalIgnoreCase))
            {
                _parkingLot.Add(topic);
            }
        }
    }

    /// <summary>
    /// Collapses text to one bounded line. Items here come from model output and invite text, and
    /// end up in later prompts, so a newline must not be able to start a fake instruction.
    /// </summary>
    public static string OneLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "\u2026";
    }

    public IReadOnlyList<string> ParkingLot
    {
        get
        {
            lock (_gate)
            {
                return [.. _parkingLot];
            }
        }
    }

    public void AddTimer(MeetingTimer timer)
    {
        timer = timer with { Label = OneLine(timer.Label, 80) };
        lock (_gate)
        {
            _timers.RemoveAll(t => t.EndsAt <= DateTimeOffset.UtcNow);
            _timers.Add(timer);
        }
    }

    /// <summary>The soonest timer still running, if any.</summary>
    public MeetingTimer? ActiveTimer
    {
        get
        {
            lock (_gate)
            {
                return _timers.Where(t => t.EndsAt > DateTimeOffset.UtcNow).OrderBy(t => t.EndsAt).FirstOrDefault();
            }
        }
    }

    /// <summary>Replaces the scribe-maintained notes with a fresh consolidation.</summary>
    public void ApplyScribeUpdate(
        string summary,
        IEnumerable<string> decisions,
        IEnumerable<ActionItem> actions,
        IEnumerable<string> openQuestions,
        int scribedThroughTurn)
    {
        foreach (string d in decisions)
        {
            AddDecision(d);
        }

        foreach (ActionItem a in actions)
        {
            AddAction(a);
        }

        lock (_gate)
        {
            _summary = OneLine(summary, 2000);
            _openQuestions.Clear();
            _openQuestions.AddRange(openQuestions.Select(q => OneLine(q, 300)).Where(q => q.Length > 0).Take(50));
            _scribedTurnCount = scribedThroughTurn;
        }
    }

    /// <summary>Turns the scribe has not yet folded into the notes, and the index to record once it has.</summary>
    public (IReadOnlyList<TranscriptTurn> Turns, int Through) TakeUnscribedTurns()
    {
        lock (_gate)
        {
            return (_turns.Skip(_scribedTurnCount).ToList(), _turns.Count);
        }
    }

    public IReadOnlyList<TranscriptTurn> RecentTurns(int count)
    {
        lock (_gate)
        {
            return _turns.Skip(Math.Max(0, _turns.Count - count)).ToList();
        }
    }

    public MeetingNotes SnapshotNotes()
    {
        lock (_gate)
        {
            return new MeetingNotes(
                _summary,
                [.. _decisions],
                [.. _actions],
                [.. _openQuestions],
                _talkTime.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value));
        }
    }

    /// <summary>A compact briefing for the realtime model: who is here, the clock, and the notes so far.</summary>
    public string RenderBriefing()
    {
        MeetingNotes notes = SnapshotNotes();
        StringBuilder sb = new();

        sb.Append("Meeting: ").AppendLine(string.IsNullOrWhiteSpace(Subject) ? "(untitled)" : Subject);
        sb.Append("Elapsed: ").Append((int)Elapsed.TotalMinutes).Append(" min");

        if (Remaining is { } remaining)
        {
            sb.Append(remaining > TimeSpan.Zero
                ? $", {(int)Math.Ceiling(remaining.TotalMinutes)} min remaining"
                : $", {(int)Math.Ceiling(-remaining.TotalMinutes)} min over time");
        }

        sb.AppendLine();
        sb.Append("Present: ").AppendLine(string.Join(", ", Participants.Select(p => p.IsExternal ? $"{p.Name} (external)" : p.Name)));

        AgendaStatus agenda = SnapshotAgenda();
        if (agenda.Items.Count > 0)
        {
            sb.Append("Agenda: ").AppendLine(string.Join("; ", agenda.Items.Select((a, i) =>
                $"{i + 1}. {a.Title}{(a.Minutes is { } m ? $" ({m:0.#} min)" : string.Empty)}{(agenda.Completed.Contains(i) ? " [done]" : i == agenda.CurrentIndex ? " [NOW]" : string.Empty)}")));

            if (agenda.Current is { } current)
            {
                sb.Append("Current item: ").Append(current.Title).Append(", ").Append((int)agenda.CurrentElapsed.TotalMinutes).Append(" min in");
                if (agenda.CurrentRemaining is { } left)
                {
                    sb.Append(left > TimeSpan.Zero ? $", {(int)Math.Ceiling(left.TotalMinutes)} min left in its timebox" : $", {(int)Math.Ceiling(-left.TotalMinutes)} min over its timebox");
                }

                sb.AppendLine();
            }
        }

        IReadOnlyList<string> parked = ParkingLot;
        if (parked.Count > 0)
        {
            sb.Append("Parking lot: ").AppendLine(string.Join("; ", parked));
        }

        foreach ((string category, IReadOnlyList<string> items) in Tracked)
        {
            sb.Append(category).Append(": ").AppendLine(string.Join("; ", items));
        }

        IReadOnlyList<string> uncovered = UncoveredChecklist;
        if (uncovered.Count > 0)
        {
            sb.Append("Not yet covered: ").AppendLine(string.Join("; ", uncovered));
        }

        IReadOnlyList<MeetingInsight> insights = Insights;
        if (insights.Count > 0)
        {
            sb.Append("Recent insights (shown on your tile, not yet spoken): ").AppendLine(string.Join("; ", insights.TakeLast(5).Select(i =>
                $"[{i.Kind}] {i.Title}: {i.Text}{(i.Citation is null ? string.Empty : $" (source: {i.Citation})")}")));
        }

        IReadOnlyList<VisualNote> visuals = Visuals;
        if (visuals.Count > 0)
        {
            sb.Append("Captured visuals: ").AppendLine(string.Join("; ", visuals.Select(v => $"{v.Title}: {v.Summary}")));
        }

        if (!string.IsNullOrWhiteSpace(notes.Summary))
        {
            sb.Append("Discussion so far: ").AppendLine(notes.Summary);
        }

        if (notes.Decisions.Count > 0)
        {
            sb.Append("Decisions: ").AppendLine(string.Join("; ", notes.Decisions));
        }

        if (notes.Actions.Count > 0)
        {
            sb.Append("Action items: ").AppendLine(string.Join("; ", notes.Actions.Select(a => $"{a.Owner}: {a.Task}")));
        }

        if (notes.OpenQuestions.Count > 0)
        {
            sb.Append("Open questions: ").AppendLine(string.Join("; ", notes.OpenQuestions));
        }

        return sb.ToString();
    }
}

public sealed record MeetingNotes(
    string Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ActionItem> Actions,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyDictionary<string, TimeSpan> TalkTime);

public sealed record MeetingReport(
    string Summary,
    IReadOnlyList<string> Insights,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ActionItem> Actions,
    IReadOnlyList<string> OpenQuestions,
    string? NextSteps,
    IReadOnlyDictionary<string, TimeSpan> TalkTime);
