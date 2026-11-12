using System.Text;
using System.Text.Json.Nodes;

namespace RtBot.Facilitator;

/// <summary>
/// Keeps the meeting's notes current while the conversation happens.
/// </summary>
/// <remarks>
/// Runs on a timer rather than per utterance: a meeting produces several turns a minute, and
/// re-summarising on every one is slower and costlier without being meaningfully fresher.
/// Each pass sees the previous notes plus only the turns since, so cost stays flat over a long call.
/// </remarks>
public sealed class MeetingScribe
{
    private const string SystemPrompt = """
        You keep the notes for a business meeting in progress. You receive the current notes and the
        transcript lines since they were last updated. Return the updated notes as JSON:

        {
          "summary": "3-6 sentences on what has been discussed so far, most recent topic last",
          "decisions": ["each decision that was actually agreed, phrased as a statement"],
          "actions": [{"owner": "person's name as spoken", "task": "what they will do", "due": "date/time if stated, else null"}],
          "open_questions": ["questions raised and not yet answered"]
        }

        Rules:
        - Only record a decision if people agreed to it, not if it was merely proposed.
        - Only record an action if a specific person took it on or was assigned it. Use their name as it
          appears in the transcript. Never invent owners or dates.
        - Keep previous decisions and actions unless the transcript explicitly reverses them.
        - Lines marked [agent] are the AI facilitator; do not credit it with decisions.
        - Be terse. These notes are read aloud and posted to chat.
        """;

    private readonly TextModelClient _model;
    private readonly string _deployment;
    private readonly ILogger _logger;

    public MeetingScribe(TextModelClient model, string deployment, ILogger logger)
    {
        _model = model;
        _deployment = deployment;
        _logger = logger;
    }

    /// <summary>Folds any new turns into the notes. A no-op when nothing has been said since the last pass.</summary>
    public async Task UpdateAsync(MeetingContext meeting, CancellationToken cancellationToken)
    {
        (IReadOnlyList<TranscriptTurn> turns, int through) = meeting.TakeUnscribedTurns();
        if (turns.Count == 0)
        {
            return;
        }

        MeetingNotes current = meeting.SnapshotNotes();
        string user = $"""
            CURRENT NOTES:
            {RenderNotesForModel(current)}

            NEW TRANSCRIPT LINES:
            {RenderTurns(turns)}
            """;

        JsonNode? result = await _model
            .CompleteJsonAsync(_deployment, SystemPrompt, user, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return;
        }

        string summary = result["summary"]?.GetValue<string>() ?? current.Summary;
        List<string> decisions = ReadStrings(result["decisions"]);
        List<string> questions = ReadStrings(result["open_questions"]);
        List<ActionItem> actions = [];

        if (result["actions"] is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                string? owner = node?["owner"]?.GetValue<string>();
                string? task = node?["task"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(task))
                {
                    continue;
                }

                string? due = node?["due"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
                actions.Add(new ActionItem(owner.Trim(), task.Trim(), due, DateTimeOffset.UtcNow));
            }
        }

        meeting.ApplyScribeUpdate(summary, decisions, actions, questions, through);
        _logger.LogInformation(
            "Notes updated: {Decisions} decisions, {Actions} actions, {Questions} open questions.",
            decisions.Count,
            actions.Count,
            questions.Count);
    }

    private const string ReportPrompt = """
        A business meeting has just ended. You receive the running notes and the transcript. Write the
        follow-up report the attendees will receive by email, as JSON:

        {
          "summary": "4-8 sentences: purpose, what was covered, where it landed",
          "insights": ["3-6 observations a sharp chief of staff would flag: themes, tensions, risks, dependencies, things said but not resolved, signals worth acting on. Each one sentence, specific to this meeting."],
          "decisions": ["each decision actually agreed"],
          "actions": [{"owner": "name as spoken", "task": "what they will do", "due": "date/time if stated, else null"}],
          "open_questions": ["unresolved questions"],
          "next_steps": "one sentence on what should happen next"
        }

        Rules: only record agreed decisions and actions someone actually took on. Never invent owners, dates,
        figures or names. Lines marked [agent] are the AI facilitator. If the transcript is thin, say so in the
        summary and keep the lists short or empty rather than padding them.
        """;

    /// <summary>Final end-of-meeting report: the notes reconciled against the whole transcript, plus insights.</summary>
    public async Task<MeetingReport?> BuildReportAsync(MeetingContext meeting, CancellationToken cancellationToken)
    {
        MeetingNotes notes = meeting.SnapshotNotes();
        IReadOnlyList<TranscriptTurn> turns = meeting.RecentTurns(600);
        if (turns.Count == 0)
        {
            return null;
        }

        string user = $"""
            MEETING: {meeting.Subject ?? "(untitled)"}, {(int)meeting.Elapsed.TotalMinutes} minutes

            RUNNING NOTES:
            {RenderNotesForModel(notes)}

            TRANSCRIPT:
            {RenderTurns(turns)}
            """;

        JsonNode? result = await _model
            .CompleteJsonAsync(_deployment, ReportPrompt, user, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            // Fall back to the running notes rather than sending nothing.
            return new MeetingReport(notes.Summary, [], notes.Decisions, notes.Actions, notes.OpenQuestions, null, notes.TalkTime);
        }

        List<ActionItem> actions = [];
        if (result["actions"] is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                string? owner = node?["owner"]?.GetValue<string>();
                string? task = node?["task"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(task))
                {
                    string? due = node?["due"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
                    actions.Add(new ActionItem(owner.Trim(), task.Trim(), due, DateTimeOffset.UtcNow));
                }
            }
        }

        return new MeetingReport(
            result["summary"]?.GetValue<string>() ?? notes.Summary,
            ReadStrings(result["insights"]),
            ReadStrings(result["decisions"]),
            actions,
            ReadStrings(result["open_questions"]),
            result["next_steps"] is JsonValue n && n.TryGetValue(out string? next) ? next : null,
            notes.TalkTime);
    }

    /// <summary>Markdown recap for the meeting chat.</summary>
    public static string RenderRecap(MeetingContext meeting, string agentName)
    {
        MeetingNotes notes = meeting.SnapshotNotes();
        StringBuilder md = new();

        md.Append("**").Append(agentName).Append(" — meeting recap");
        if (!string.IsNullOrWhiteSpace(meeting.Subject))
        {
            md.Append(": ").Append(meeting.Subject);
        }

        md.AppendLine("**").AppendLine();

        if (!string.IsNullOrWhiteSpace(notes.Summary))
        {
            md.AppendLine(notes.Summary).AppendLine();
        }

        if (notes.Decisions.Count > 0)
        {
            md.AppendLine("**Decisions**");
            foreach (string d in notes.Decisions)
            {
                md.Append("- ").AppendLine(d);
            }

            md.AppendLine();
        }

        if (notes.Actions.Count > 0)
        {
            md.AppendLine("**Action items**");
            foreach (ActionItem a in notes.Actions)
            {
                md.Append("- **").Append(a.Owner).Append("**: ").Append(a.Task);
                if (!string.IsNullOrWhiteSpace(a.Due))
                {
                    md.Append(" _(due ").Append(a.Due).Append(")_");
                }

                md.AppendLine();
            }

            md.AppendLine();
        }

        if (notes.OpenQuestions.Count > 0)
        {
            md.AppendLine("**Still open**");
            foreach (string q in notes.OpenQuestions)
            {
                md.Append("- ").AppendLine(q);
            }

            md.AppendLine();
        }

        md.Append("_").Append((int)meeting.Elapsed.TotalMinutes).Append(" minutes. Notes generated by AI; check before acting on them._");
        return md.ToString();
    }

    private static string RenderNotesForModel(MeetingNotes notes) =>
        new JsonObject
        {
            ["summary"] = notes.Summary,
            ["decisions"] = new JsonArray([.. notes.Decisions.Select(d => (JsonNode)JsonValue.Create(d))]),
            ["actions"] = new JsonArray([.. notes.Actions.Select(a => (JsonNode)new JsonObject
            {
                ["owner"] = a.Owner,
                ["task"] = a.Task,
                ["due"] = a.Due,
            })]),
            ["open_questions"] = new JsonArray([.. notes.OpenQuestions.Select(q => (JsonNode)JsonValue.Create(q))]),
        }.ToJsonString();

    private static string RenderTurns(IEnumerable<TranscriptTurn> turns)
    {
        StringBuilder sb = new();
        foreach (TranscriptTurn t in turns)
        {
            sb.Append('[').Append(t.At.ToString("HH:mm")).Append("] ");
            sb.Append(t.IsAgent ? "[agent] " : string.Empty);
            sb.Append(t.Speaker).Append(": ").AppendLine(t.Text);
        }

        return sb.ToString();
    }

    private static List<string> ReadStrings(JsonNode? node) =>
        node is JsonArray array
            ? [.. array.Select(n => n?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())]
            : [];
}
