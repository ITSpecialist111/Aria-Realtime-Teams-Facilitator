using System.Text;

namespace RtBot.Facilitator;

/// <summary>One finished utterance, attributed to whoever was speaking when it started.</summary>
public sealed record TranscriptTurn(DateTimeOffset At, string Speaker, string Text, bool IsAgent);

public sealed record ActionItem(string Owner, string Task, string? Due, DateTimeOffset RecordedAt);

public sealed record MeetingParticipant(string Id, string Name, string? TenantId, bool IsExternal, string? UserId = null);

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
        lock (_gate)
        {
            if (!_decisions.Contains(decision, StringComparer.OrdinalIgnoreCase))
            {
                _decisions.Add(decision);
            }
        }
    }

    public void AddAction(ActionItem action)
    {
        lock (_gate)
        {
            bool duplicate = _actions.Any(a =>
                a.Owner.Equals(action.Owner, StringComparison.OrdinalIgnoreCase) &&
                a.Task.Equals(action.Task, StringComparison.OrdinalIgnoreCase));

            if (!duplicate)
            {
                _actions.Add(action);
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
        lock (_gate)
        {
            _summary = summary;

            foreach (string d in decisions)
            {
                if (!_decisions.Contains(d, StringComparer.OrdinalIgnoreCase))
                {
                    _decisions.Add(d);
                }
            }

            foreach (ActionItem a in actions)
            {
                bool duplicate = _actions.Any(x =>
                    x.Owner.Equals(a.Owner, StringComparison.OrdinalIgnoreCase) &&
                    x.Task.Equals(a.Task, StringComparison.OrdinalIgnoreCase));
                if (!duplicate)
                {
                    _actions.Add(a);
                }
            }

            _openQuestions.Clear();
            _openQuestions.AddRange(openQuestions);
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
