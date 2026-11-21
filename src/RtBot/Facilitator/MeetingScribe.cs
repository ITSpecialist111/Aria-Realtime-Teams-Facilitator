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
/// <summary>What the scribe suggests acting on after a pass, beyond updating the notes.</summary>
public sealed record ScribeOutcome(IReadOnlyList<string> Lookups, IReadOnlyList<string> SkillShifts)
{
    public static ScribeOutcome None { get; } = new([], []);
}

public sealed class MeetingScribe
{
    private const string SystemPrompt = """
        You keep the notes for a business meeting in progress, and you watch it the way an expert facilitator
        would. You receive the current notes and the transcript lines since they were last updated. Return JSON:

        {
          "summary": "3-6 sentences on what has been discussed so far, most recent topic last",
          "decisions": ["each decision that was actually agreed, phrased as a statement"],
          "actions": [{"owner": "person's name as spoken", "task": "what they will do", "due": "date/time if stated, else null"}],
          "open_questions": ["questions raised and not yet answered"],
          "tracked": {"<category>": ["item"]},
          "covered": ["checklist items that were dealt with in these lines"],
          "alignment": {"score": 0-100, "note": "at most 8 words on why"},
          "topic_milestone": "2-4 word label for the distinct topic now being discussed if it shifted from RECENT TOPIC MILESTONES, else null",
          "agenda_index": 0-based integer index into AGENDA if the room moved forward onto a later item, else null,
          "insights": [{"kind": "skill|risk|alignment|facilitation|pace", "severity": "high|medium|info", "title": "3-6 words", "text": "one or two sentences", "skill": "skill-name if kind is skill, else null"}],
          "lookups": ["up to two concise search queries for the organisation's Microsoft 365 data (mail, files, playbooks, policies, meetings)"],
          "skill_shifts": ["up to two AVAILABLE SKILL names whose domain or triggers appear in NEW TRANSCRIPT LINES"]
        }

        Rules:
        - Only record a decision if people agreed to it, not if it was merely proposed.
        - Only record an action if a specific person took it on or was assigned it. Use their name as it
          appears in the transcript. Never invent owners or dates. Lines marked (in chat) count too.
        - Keep previous decisions and actions unless the transcript explicitly reverses them.
        - "tracked" uses only the categories given under SPECIALIST TRACKING, and only new items from these lines.
          "covered" uses only the wording given under CHECKLIST. Omit both when none are given.
        - "alignment" is your read of how aligned the room is right now: 90+ shared understanding and converging,
          60-89 broadly aligned with open points, 30-59 people disagreeing or talking past each other,
          below 30 conflict or confusion. Give a score on every pass and keep it stable unless something changed.
        - "topic_milestone": when the substantive subject in NEW TRANSCRIPT LINES begins or shifts to a distinct new
          topic (not already the latest entry in RECENT TOPIC MILESTONES), give a crisp 2-4 word noun-phrase label
          (for example "EHR outage triage", "Ambulance diversion", "ICO & press risk", "Ransom vs rebuild").
          Always emit one on the first substantive pass; emit null if the room is still on the same topic.
        - "agenda_index": if AGENDA is present and the conversation has clearly moved forward onto a later item than
          the one marked [NOW], return that item's 0-based index; otherwise null. Never move backwards.
        - "insights" are things worth a facilitator's attention in THESE lines: a risk or dependency
          nobody owns, people disagreeing or talking past each other (alignment), a decision being avoided,
          a topic going round in circles, someone not heard from, too much detail for this forum (pace).
          When ACTIVE SKILLS are given, also apply their expertise: use kind "skill" for a gap, risk or next step
          that the skill's guidance says matters (for example an unnamed incident owner, an unvetted contractual
          promise, a missing checklist item). Be specific to what was said. Up to three; usually zero to two.
          Never restate the notes or repeat an insight already raised (see INSIGHTS ALREADY RAISED).
        - "lookups" are focused search queries whose answer lives in the organisation's Microsoft 365 emails,
          playbooks, policies, contracts or reports and would immediately ground this discussion (for example
          "Northwind Health immutable backup EHR restore time", "ICO NHS DSPT regulatory notification deadline",
          "cyber insurance policy extortion ransom terms", "board crisis authority matrix"). Keep queries crisp
          (4-10 keywords or a short factual question). Empty list when nothing would help. Never ask about this meeting itself.
        - "skill_shifts": proactively include any AVAILABLE SKILL whose domain or trigger concepts appear in NEW
          TRANSCRIPT LINES so the facilitator brings that specialist lens in automatically without waiting to be asked.
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
    public async Task<ScribeOutcome> UpdateAsync(
        MeetingContext meeting,
        CancellationToken cancellationToken,
        IReadOnlyList<MeetingSkill>? availableSkills = null)
    {
        (IReadOnlyList<TranscriptTurn> turns, int through) = meeting.TakeUnscribedTurns();
        if (turns.Count == 0)
        {
            return ScribeOutcome.None;
        }

        MeetingNotes current = meeting.SnapshotNotes();
        IReadOnlyList<MeetingSkill> skills = meeting.ActiveSkills;
        string categories = string.Join(", ", skills.SelectMany(s => s.Track).Distinct(StringComparer.OrdinalIgnoreCase));
        string checklist = string.Join("; ", meeting.UncoveredChecklist);
        IReadOnlyList<MeetingSkill> inactiveSkills = [.. (availableSkills ?? [])
            .Where(a => !skills.Any(s => s.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))];
        string inactive = string.Join("\n", inactiveSkills
            .Select(a => $"- {a.Name}: {a.Description} (triggers: {string.Join(", ", a.Triggers.Take(10))})"));
        string guidance = string.Join("\n\n", skills.Select(s =>
            $"## {s.Name}: {s.Description}\n{(s.Guidance.Length > 1400 ? s.Guidance[..1400] : s.Guidance)}"));
        string raised = string.Join("; ", meeting.Insights.TakeLast(10).Select(i => i.Title));
        AgendaStatus agenda = meeting.SnapshotAgenda();
        string agendaLine = agenda.Items.Count == 0
            ? "(none)"
            : string.Join("; ", agenda.Items.Select((a, idx) => $"[{idx}]{(idx == agenda.CurrentIndex ? "[NOW]" : agenda.Completed.Contains(idx) ? "[done]" : string.Empty)} {a.Title}"));
        string recentMilestones = string.Join(" -> ", meeting.Milestones.TakeLast(6).Select(m => m.Label));
        string user = $"""
            CURRENT NOTES:
            {RenderNotesForModel(current)}

            AGENDA: {agendaLine}
            RECENT TOPIC MILESTONES: {(recentMilestones.Length > 0 ? recentMilestones : "(none yet)")}

            ACTIVE SKILLS:
            {(guidance.Length > 0 ? guidance : "(none)")}

            INSIGHTS ALREADY RAISED: {(raised.Length > 0 ? raised : "(none)")}

            SPECIALIST TRACKING: {(categories.Length > 0 ? categories : "(none)")}
            CHECKLIST: {(checklist.Length > 0 ? checklist : "(none)")}
            AVAILABLE SKILLS:
            {(inactive.Length > 0 ? inactive : "(none)")}

            NEW TRANSCRIPT LINES:
            {RenderTurns(turns)}
            """;

        JsonNode? result = await _model
            .CompleteJsonAsync(_deployment, SystemPrompt, user, cancellationToken)
            .ConfigureAwait(false);

        if (result is null)
        {
            return ScribeOutcome.None;
        }

        if (result["insights"] is JsonArray insights)
        {
            foreach (JsonNode? node in insights.Take(3))
            {
                string? title = node?["title"]?.GetValue<string>();
                string? text = node?["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(text))
                {
                    string kind = node?["kind"]?.GetValue<string>() ?? "facilitation";
                    string? skillName = node?["skill"] is JsonValue skv && skv.TryGetValue(out string? sn) && !string.IsNullOrWhiteSpace(sn)
                        ? MeetingContext.Titleise(sn)
                        : null;
                    meeting.AddInsight(new MeetingInsight(
                        kind,
                        node?["severity"]?.GetValue<string>() ?? "info",
                        title,
                        text,
                        kind.Equals("skill", StringComparison.OrdinalIgnoreCase) ? skillName : null,
                        DateTimeOffset.UtcNow));
                }
            }
        }

        if (result["alignment"] is JsonObject align && align["score"] is JsonValue sc && sc.TryGetValue(out double score))
        {
            meeting.SetAlignment((int)Math.Round(score), align["note"] is JsonValue nv && nv.TryGetValue(out string? note) ? note : null);
        }

        bool advancedAgenda = false;
        if (result["agenda_index"] is JsonValue agVal)
        {
            int parsedIdx = -1;
            if (agVal.TryGetValue(out int ai))
            {
                parsedIdx = ai;
            }
            else if (agVal.TryGetValue(out double ad))
            {
                parsedIdx = (int)Math.Round(ad);
            }
            else if (agVal.TryGetValue(out string? asStr) && int.TryParse(asStr, out int ap))
            {
                parsedIdx = ap;
            }

            if (parsedIdx >= 0)
            {
                advancedAgenda = meeting.AdvanceAgendaTo(parsedIdx);
            }
        }

        if (!advancedAgenda &&
            result["topic_milestone"] is JsonValue tmVal &&
            tmVal.TryGetValue(out string? milestone) &&
            !string.IsNullOrWhiteSpace(milestone) &&
            !milestone.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            meeting.AddMilestone(milestone, "topic");
        }

        List<string> lookups = [.. ReadStrings(result["lookups"]).Where(q => q.Length > 6).Take(2)];
        List<string> shifts = [.. ReadStrings(result["skill_shifts"]).Take(2)];
        if (shifts.Count == 0 && result["skill_shift"] is JsonValue sv && sv.TryGetValue(out string? singleShift) && !string.IsNullOrWhiteSpace(singleShift))
        {
            shifts.Add(singleShift);
        }

        // Deterministic trigger backstop so clear domain keywords always pull in the matching skill on demand.
        string spokenBatch = string.Join(" ", turns.Where(t => !t.IsAgent).Select(t => t.Text));
        foreach (MeetingSkill candidate in inactiveSkills)
        {
            if (shifts.Count >= 2 || shifts.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            int hits = candidate.Triggers.Count(trig => ContainsWholePhrase(spokenBatch, trig));
            bool phraseHit = candidate.Triggers.Any(trig => trig.Contains(' ') && ContainsWholePhrase(spokenBatch, trig));
            if (hits >= 2 || phraseHit)
            {
                shifts.Add(candidate.Name);
            }
        }

        HashSet<string> allowed = new(skills.SelectMany(s => s.Track), StringComparer.OrdinalIgnoreCase);
        if (result["tracked"] is JsonObject tracked)
        {
            foreach ((string category, JsonNode? items) in tracked)
            {
                if (allowed.Contains(category))
                {
                    foreach (string item in ReadStrings(items))
                    {
                        meeting.AddTracked(category, item);
                    }
                }
            }
        }

        HashSet<string> open = new(meeting.UncoveredChecklist, StringComparer.OrdinalIgnoreCase);
        foreach (string item in ReadStrings(result["covered"]).Where(open.Contains))
        {
            meeting.MarkCovered(item);
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
        return new ScribeOutcome(lookups, shifts);
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

        string specialism = string.Join("\n", meeting.ActiveSkills.Select(s =>
            $"- {s.Name}: {s.Description}. Tracks: {string.Join(", ", s.Track)}. Should cover: {string.Join("; ", s.Checklist)}"));

        string user = $"""
            MEETING: {meeting.Subject ?? "(untitled)"}, {(int)meeting.Elapsed.TotalMinutes} minutes
            FACILITATOR PURPOSE: {meeting.Profile.Purpose}
            SPECIALISMS (frame the insights through these lenses):
            {(specialism.Length > 0 ? specialism : "(general meeting)")}
            NOT COVERED: {string.Join("; ", meeting.UncoveredChecklist)}
            LIVE INSIGHTS RAISED DURING THE MEETING: {string.Join("; ", meeting.Insights.Select(i => $"[{i.Kind}] {i.Title}: {i.Text}"))}

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

        IReadOnlyList<string> parked = meeting.ParkingLot;
        if (parked.Count > 0)
        {
            md.AppendLine("**Parking lot**");
            foreach (string p in parked)
            {
                md.Append("- ").AppendLine(p);
            }

            md.AppendLine();
        }

        foreach (VisualNote v in meeting.Visuals)
        {
            md.Append("**Captured: ").Append(v.Title).Append("** _(").Append(v.Source).Append(")_  \n").AppendLine(v.Summary).AppendLine();
        }

        // The chat is visible to everyone present, so organisation-sourced findings stay out while a guest is in the meeting.
        IReadOnlyList<MeetingInsight> insights = [.. meeting.Insights.Where(i =>
            !meeting.IsRestricted || !i.Kind.Equals("knowledge", StringComparison.OrdinalIgnoreCase))];
        if (insights.Count > 0)
        {
            md.AppendLine("**Insights**");
            foreach (MeetingInsight i in insights.TakeLast(6))
            {
                md.Append("- ").Append(i.Title).Append(": ").Append(i.Text);
                if (i.Citation is not null)
                {
                    md.Append(" _(").Append(i.Citation).Append(")_");
                }

                md.AppendLine();
            }

            md.AppendLine();
        }

        foreach ((string category, IReadOnlyList<string> items) in meeting.Tracked)
        {
            md.Append("**").Append(category).AppendLine("**");
            foreach (string item in items)
            {
                md.Append("- ").AppendLine(item);
            }

            md.AppendLine();
        }

        IReadOnlyList<string> uncovered = meeting.UncoveredChecklist;
        if (uncovered.Count > 0)
        {
            md.AppendLine("**Not covered this time**");
            foreach (string u in uncovered.Take(6))
            {
                md.Append("- ").AppendLine(u);
            }

            if (uncovered.Count > 6)
            {
                md.Append("- \u2026and ").Append(uncovered.Count - 6).AppendLine(" more (see the email)");
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

    public static bool ContainsWholePhrase(string text, string phrase) =>
        !string.IsNullOrWhiteSpace(phrase) &&
        System.Text.RegularExpressions.Regex.IsMatch(
            text,
            $@"(?<!\w){System.Text.RegularExpressions.Regex.Escape(phrase.Trim())}(?!\w)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
