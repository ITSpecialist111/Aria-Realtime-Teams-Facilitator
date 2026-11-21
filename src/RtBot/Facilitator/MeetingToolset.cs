using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.VoiceLive;
using RtBot.Tools;

namespace RtBot.Facilitator;

/// <summary>
/// The tools available in one meeting: the organisation-wide catalogue plus tools that act on
/// this meeting itself.
/// </summary>
/// <remarks>
/// When the meeting is restricted (an external participant is present), tools that reach
/// organisation data are both withheld from the model and refused at dispatch. The model's
/// instructions alone are not a security boundary.
/// </remarks>
public sealed class MeetingToolset
{
    private readonly ToolCatalog _catalog;
    private readonly MeetingContext _meeting;
    private readonly IMeetingActions _actions;
    private readonly TextModelClient _textModel;
    private readonly string _visionModel;
    private readonly bool _visionEnabled;
    private readonly string _agentName;
    private readonly IReadOnlyList<MeetingSkill> _availableSkills;
    private readonly ILogger _logger;
    private readonly Dictionary<string, LocalTool> _local = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Timer> _timers = [];

    public MeetingToolset(
        ToolCatalog catalog,
        MeetingContext meeting,
        IMeetingActions actions,
        TextModelClient textModel,
        string visionModel,
        bool visionEnabled,
        string agentName,
        IReadOnlyList<MeetingSkill> availableSkills,
        ILogger logger)
    {
        _catalog = catalog;
        _meeting = meeting;
        _actions = actions;
        _textModel = textModel;
        _visionModel = visionModel;
        _visionEnabled = visionEnabled;
        _agentName = agentName;
        _availableSkills = availableSkills;
        _logger = logger;

        Register();
    }

    /// <summary>Tools the model may currently see.</summary>
    public IReadOnlyList<ToolSpec> AvailableSpecs()
    {
        IEnumerable<ToolSpec> remote = _catalog.Specs.Where(s => !_meeting.IsRestricted || !s.AccessesOrganisationData);
        IEnumerable<ToolSpec> local = _local.Values.Select(t => t.Spec);
        return [.. local, .. remote];
    }

    public IReadOnlyList<VoiceLiveToolDefinition> VoiceDefinitions() =>
        [.. AvailableSpecs().Select(s => (VoiceLiveToolDefinition)new VoiceLiveFunctionDefinition(s.Name)
        {
            Description = s.Description,
            Parameters = BinaryData.FromString(s.ParametersJson),
        })];

    public IReadOnlyList<TextTool> TextTools() =>
        [.. AvailableSpecs().Select(s => new TextTool(s.Name, s.Description, JsonNode.Parse(s.ParametersJson)!))];

    public async Task<string> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        if (_local.TryGetValue(name, out LocalTool? local))
        {
            JsonElement args = Parse(argumentsJson);
            try
            {
                return await local.Handler(args, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Facilitator tool {Tool} failed.", name);
                return Error(ex.Message);
            }
        }

        ToolSpec? spec = _catalog.Specs.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (spec is { AccessesOrganisationData: true } && _meeting.IsRestricted)
        {
            _logger.LogWarning("Refused {Tool}: an external participant is present.", name);
            return Error("Organisation data is unavailable while someone from outside the organisation is in the meeting.");
        }

        return await _catalog.InvokeAsync(name, argumentsJson, cancellationToken).ConfigureAwait(false);
    }

    public void DisposeTimers()
    {
        lock (_timers)
        {
            foreach (Timer t in _timers)
            {
                t.Dispose();
            }

            _timers.Clear();
        }
    }

    private void Register()
    {
        Add(
            "get_meeting_status",
            "Who is in the meeting, how long it has run, how long remains, who has spoken most, and the notes so far. Call this before answering anything about the meeting itself.",
            """{"type":"object","properties":{}}""",
            (_, _) =>
            {
                MeetingNotes notes = _meeting.SnapshotNotes();
                JsonObject result = new()
                {
                    ["briefing"] = _meeting.RenderBriefing(),
                    ["talk_time_minutes"] = new JsonObject(notes.TalkTime.Select(kv =>
                        KeyValuePair.Create(kv.Key, (JsonNode?)Math.Round(kv.Value.TotalMinutes, 1)))),
                };
                return Task.FromResult(result.ToJsonString());
            });

        Add(
            "record_action_item",
            "Record an action item when someone takes on or is given a task. Use this as soon as an owner is clear, and confirm it aloud briefly.",
            """
            {"type":"object","properties":{
              "owner":{"type":"string","description":"Person responsible, as named in the meeting"},
              "task":{"type":"string","description":"What they will do"},
              "due":{"type":"string","description":"Due date or time if stated"}},
             "required":["owner","task"]}
            """,
            (args, _) =>
            {
                string owner = Str(args, "owner");
                string task = Str(args, "task");
                _meeting.AddAction(new ActionItem(owner, task, Opt(args, "due"), DateTimeOffset.UtcNow));
                return Task.FromResult(Ok($"Recorded for {owner}."));
            });

        Add(
            "record_decision",
            "Record a decision the group has explicitly agreed. Do not use for proposals that were not agreed.",
            """{"type":"object","properties":{"decision":{"type":"string"}},"required":["decision"]}""",
            (args, _) =>
            {
                _meeting.AddDecision(Str(args, "decision"));
                return Task.FromResult(Ok("Decision recorded."));
            });

        Add(
            "set_timer",
            "Start a timer, e.g. for a timeboxed discussion. The agent announces when it ends.",
            """
            {"type":"object","properties":{
              "minutes":{"type":"number","minimum":0.5,"maximum":120},
              "label":{"type":"string","description":"What the timer is for"}},
             "required":["minutes"]}
            """,
            (args, _) =>
            {
                double minutes = args.TryGetProperty("minutes", out JsonElement m) && m.TryGetDouble(out double d) ? d : 5;
                string label = Opt(args, "label") ?? "the timer";
                Timer? timer = null;
                timer = new Timer(
                    _ =>
                    {
                        _ = _actions.SpeakAsync($"Say briefly that time is up for {label}.", CancellationToken.None);
                        timer?.Dispose();
                    },
                    null,
                    TimeSpan.FromMinutes(minutes),
                    Timeout.InfiniteTimeSpan);

                lock (_timers)
                {
                    _timers.Add(timer);
                }

                _meeting.AddTimer(new MeetingTimer(label, DateTimeOffset.UtcNow.AddMinutes(minutes)));
                return Task.FromResult(Ok($"Timer set for {minutes:0.#} minutes."));
            });

        Add(
            "set_agenda",
            "Set or replace the meeting agenda when someone states it or asks you to run one, optionally with a timebox per item. Starts the first item. Confirm it back in one short sentence.",
            """
            {"type":"object","properties":{
              "items":{"type":"array","minItems":1,"items":{"type":"object","properties":{
                "title":{"type":"string"},
                "minutes":{"type":"number","minimum":0.5,"maximum":240,"description":"Timebox, if one was given"}},
               "required":["title"]}}},
             "required":["items"]}
            """,
            (args, _) =>
            {
                List<AgendaItem> items = [];
                if (args.TryGetProperty("items", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in list.EnumerateArray())
                    {
                        string title = Str(item, "title");
                        double? minutes = item.TryGetProperty("minutes", out JsonElement m) && m.TryGetDouble(out double d) ? d : null;
                        if (title.Length > 0)
                        {
                            items.Add(new AgendaItem(title, minutes));
                        }
                    }
                }

                if (items.Count == 0)
                {
                    return Task.FromResult(Error("No agenda items were given."));
                }

                _meeting.SetAgenda(items);
                return Task.FromResult(Ok($"Agenda set with {items.Count} items. Now on: {items[0].Title}."));
            });

        Add(
            "next_agenda_item",
            "Close the current agenda item and move to the next. Use when the group moves on or asks to.",
            """{"type":"object","properties":{}}""",
            (_, _) =>
            {
                AgendaItem? next = _meeting.AdvanceAgenda();
                return Task.FromResult(next is null
                    ? Ok("That was the last agenda item. Offer to recap or wrap up.")
                    : Ok($"Now on: {next.Title}{(next.Minutes is { } m ? $", {m:0.#} minutes" : string.Empty)}."));
            });

        Add(
            "extend_agenda_item",
            "Give the current agenda item more time when the group agrees to keep going.",
            """{"type":"object","properties":{"minutes":{"type":"number","minimum":0.5,"maximum":60}},"required":["minutes"]}""",
            (args, _) =>
            {
                double minutes = args.TryGetProperty("minutes", out JsonElement m) && m.TryGetDouble(out double d) ? d : 5;
                AgendaItem? item = _meeting.ExtendCurrentItem(minutes);
                return Task.FromResult(item is null ? Error("There is no agenda item in progress.") : Ok($"Added {minutes:0.#} minutes to {item.Title}."));
            });

        Add(
            "park_topic",
            "Put a topic in the parking lot when it is off-agenda or needs a separate conversation. Parked topics go into the recap.",
            """{"type":"object","properties":{"topic":{"type":"string"}},"required":["topic"]}""",
            (args, _) =>
            {
                _meeting.ParkTopic(Str(args, "topic"));
                return Task.FromResult(Ok("Parked."));
            });

        Add(
            "show_on_screen",
            "Change what your video tile shows. Use when someone asks to see or show the actions, decisions, tracked items, parking lot or agenda, or to go back to the overview. A list stays up for 90 seconds. After switching, summarise it aloud in one sentence.",
            """{"type":"object","properties":{"view":{"type":"string","enum":["overview","actions","decisions","tracked","parked","agenda","insights","capture"]}},"required":["view"]}""",
            (args, _) =>
            {
                RtBot.Media.TileView view = Str(args, "view").ToLowerInvariant() switch
                {
                    "actions" => RtBot.Media.TileView.Actions,
                    "decisions" => RtBot.Media.TileView.Decisions,
                    "tracked" => RtBot.Media.TileView.Tracked,
                    "parked" => RtBot.Media.TileView.Parked,
                    "agenda" => RtBot.Media.TileView.Agenda,
                    "insights" => RtBot.Media.TileView.Insights,
                    "capture" when _meeting.Visuals.Count > 0 => RtBot.Media.TileView.Capture,
                    _ => RtBot.Media.TileView.Overview,
                };
                _meeting.ShowView(view);
                return Task.FromResult(Ok(view == RtBot.Media.TileView.Overview ? "Showing the overview." : $"Showing {view.ToString().ToLowerInvariant()} on screen."));
            });

        Add(
            "track_item",
            "Record something your active specialist skills say to listen for (for example an RFI, a variation, a risk or a safety issue) under one of their categories. Confirm briefly.",
            """
            {"type":"object","properties":{
              "category":{"type":"string","description":"One of the categories your active skills list"},
              "item":{"type":"string","description":"The specific item, with owner or reference if stated"}},
             "required":["category","item"]}
            """,
            (args, _) =>
            {
                string category = Str(args, "category");
                string item = Str(args, "item");
                if (category.Length == 0 || item.Length == 0)
                {
                    return Task.FromResult(Error("Both a category and an item are needed."));
                }

                _meeting.AddTracked(category, item);
                return Task.FromResult(Ok($"Tracked under {category}."));
            });

        Add(
            "mark_covered",
            "Mark an item from your skills' coverage checklist as dealt with in this meeting.",
            """{"type":"object","properties":{"item":{"type":"string","description":"The checklist item, as worded in your instructions"}},"required":["item"]}""",
            (args, _) =>
            {
                string wanted = Str(args, "item");
                string? match = _meeting.ActiveSkills.SelectMany(s => s.Checklist)
                    .FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? _meeting.ActiveSkills.SelectMany(s => s.Checklist)
                        .FirstOrDefault(c => c.Contains(wanted, StringComparison.OrdinalIgnoreCase) || wanted.Contains(c, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    return Task.FromResult(Error("That isn't on the checklist."));
                }

                _meeting.MarkCovered(match);
                return Task.FromResult(Ok($"Marked covered: {match}. {_meeting.UncoveredChecklist.Count} left."));
            });

        if (_availableSkills.Count > 0)
        {
            Add(
                "activate_skill",
                "Switch on a specialist skill when the meeting turns out to be about that domain, or someone asks. Available: " +
                string.Join("; ", _availableSkills.Select(s => $"{s.Name} ({s.Description})")),
                """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
                async (args, ct) =>
                {
                    string name = Str(args, "name");
                    MeetingSkill? skill = _availableSkills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (skill is null)
                    {
                        return Error($"No skill called '{name}'.");
                    }

                    if (_meeting.ActivateSkill(skill, dynamic: true))
                    {
                        _meeting.AddInsight(new MeetingInsight("skill", "info", $"Skill activated: {MeetingContext.Titleise(skill.Name)}", skill.Description, MeetingContext.Titleise(skill.Name), DateTimeOffset.UtcNow));
                        await _actions.SkillsChangedAsync(ct).ConfigureAwait(false);
                    }

                    return Ok($"{skill.Name} is active. Its guidance applies from your next reply.");
                });
        }

        Add(
            "post_to_meeting_chat",
            "Post a message into the meeting chat. Use for anything people need to read rather than hear: links, lists longer than three items, figures, drafted text, or a plan.",
            """{"type":"object","properties":{"markdown":{"type":"string","description":"Message body in Markdown"}},"required":["markdown"]}""",
            async (args, ct) =>
            {
                bool posted = await _actions.PostToChatAsync(Str(args, "markdown"), ct).ConfigureAwait(false);
                return posted ? Ok("Posted to the meeting chat.") : Error("The meeting chat is not available.");
            });

        Add(
            "post_recap_now",
            "Post the current notes, decisions and action items to the meeting chat. Use when asked to recap or to share the notes.",
            """{"type":"object","properties":{}}""",
            async (_, ct) =>
            {
                bool posted = await _actions.PostToChatAsync(MeetingScribe.RenderRecap(_meeting, _agentName), ct).ConfigureAwait(false);
                return posted ? Ok("Recap posted to the chat.") : Error("The meeting chat is not available.");
            });

        if (_visionEnabled)
        {
            const string sourceSchema = "\"source\":{\"type\":\"string\",\"enum\":[\"camera\",\"screen\"],\"description\":\"camera: something held up to the speaker's webcam (paper, whiteboard, object). screen: the active screen share.\"}";

            Add(
                "look_at",
                "Look at the active screen share or at what the current speaker is holding up to their camera, and answer a question about it. Use for 'this slide', 'on screen', 'what I'm holding up', 'this drawing'. Does not record anything.",
                "{\"type\":\"object\",\"properties\":{" + sourceSchema + ",\"question\":{\"type\":\"string\",\"description\":\"What to find out\"}},\"required\":[\"source\",\"question\"]}",
                async (args, ct) =>
                {
                    bool screen = Str(args, "source").Equals("screen", StringComparison.OrdinalIgnoreCase);
                    (byte[]? jpeg, string? from, string? failure) = await _actions.CaptureVisualAsync(screen, ct).ConfigureAwait(false);
                    if (jpeg is null)
                    {
                        return Error(failure ?? "No picture arrived.");
                    }

                    string answer = await _textModel.DescribeImageAsync(_visionModel, Str(args, "question"), jpeg, ct).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { answer, from });
                });

            Add(
                "capture_visual",
                "Capture what is on the screen share or held up to the camera (a diagram, sketch, whiteboard, slide) as a record of the meeting: it is analysed, shown on your video tile, and included in the recap and email. Use when asked to capture, snap, save or remember something visual. Then summarise in one or two sentences what you captured.",
                "{\"type\":\"object\",\"properties\":{" + sourceSchema + ",\"purpose\":{\"type\":\"string\",\"description\":\"What it is or why it is being captured, if said\"}},\"required\":[\"source\"]}",
                async (args, ct) =>
                {
                    bool screen = Str(args, "source").Equals("screen", StringComparison.OrdinalIgnoreCase);
                    (byte[]? jpeg, string? from, string? failure) = await _actions.CaptureVisualAsync(screen, ct).ConfigureAwait(false);
                    if (jpeg is null)
                    {
                        return Error(failure ?? "No picture arrived.");
                    }

                    string categories = string.Join(", ", _meeting.ActiveSkills.SelectMany(s => s.Track).Distinct(StringComparer.OrdinalIgnoreCase));
                    JsonNode? result = await _textModel.AnalyseImageJsonAsync(
                        _visionModel,
                        """
                        You capture a visual from a business meeting (a diagram, sketch, whiteboard, document or slide) for the meeting record.
                        Return JSON: {"title":"3-6 words","summary":"2-3 sentences on what it shows and means","key_points":["..."],
                        "actions":[{"owner":"name","task":"...","due":null}],"decisions":["..."],"tracked":{"<category>":["..."]}}
                        Only include actions, decisions or tracked items that are written or drawn on it. Use only the tracked categories given.
                        If it is unreadable, say so in the summary and leave the lists empty. Never invent names, dates or figures.
                        """,
                        $"Purpose: {Opt(args, "purpose") ?? "not stated"}. Tracked categories: {(categories.Length > 0 ? categories : "none")}.",
                        jpeg,
                        ct).ConfigureAwait(false);

                    if (result is null)
                    {
                        return Error("I couldn't analyse the image.");
                    }

                    string title = result["title"]?.GetValue<string>() ?? "Captured visual";
                    string summary = result["summary"]?.GetValue<string>() ?? string.Empty;
                    List<string> points = Strings(result["key_points"]);

                    foreach (string d in Strings(result["decisions"]))
                    {
                        _meeting.AddDecision(d);
                    }

                    if (result["actions"] is JsonArray acts)
                    {
                        foreach (JsonNode? a in acts)
                        {
                            string? owner = a?["owner"]?.GetValue<string>();
                            string? task = a?["task"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(task))
                            {
                                string? due = a?["due"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
                                _meeting.AddAction(new ActionItem(owner, task, due, DateTimeOffset.UtcNow));
                            }
                        }
                    }

                    HashSet<string> allowed = new(_meeting.ActiveSkills.SelectMany(s => s.Track), StringComparer.OrdinalIgnoreCase);
                    if (result["tracked"] is JsonObject tracked)
                    {
                        foreach ((string category, JsonNode? items) in tracked)
                        {
                            if (allowed.Contains(category))
                            {
                                foreach (string item in Strings(items))
                                {
                                    _meeting.AddTracked(category, item);
                                }
                            }
                        }
                    }

                    _meeting.AddVisual(new VisualNote(title, summary, points, screen ? "Screen share" : "Camera", from, jpeg, DateTimeOffset.UtcNow));
                    return JsonSerializer.Serialize(new { captured = title, summary, key_points = points, shown_on_tile = true, in_recap = true });
                });
        }

        Add(
            "leave_meeting",
            "Leave the meeting. Only when explicitly asked to leave.",
            """{"type":"object","properties":{}}""",
            async (_, ct) =>
            {
                await _actions.LeaveAsync(ct).ConfigureAwait(false);
                return Ok("Leaving.");
            });
    }

    private void Add(string name, string description, string schema, Func<JsonElement, CancellationToken, Task<string>> handler) =>
        _local[name] = new LocalTool(new ToolSpec(name, description, schema, AccessesOrganisationData: false), handler);

    private static JsonElement Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private static string Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;

    private static string? Opt(JsonElement args, string name)
    {
        string value = Str(args, name);
        return value.Length == 0 ? null : value;
    }

    private static string Ok(string message) => JsonSerializer.Serialize(new { ok = true, message });

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? [.. array.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!)]
            : [];

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });

    private sealed record LocalTool(ToolSpec Spec, Func<JsonElement, CancellationToken, Task<string>> Handler);
}
