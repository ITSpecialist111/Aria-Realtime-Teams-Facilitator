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
        ILogger logger)
    {
        _catalog = catalog;
        _meeting = meeting;
        _actions = actions;
        _textModel = textModel;
        _visionModel = visionModel;
        _visionEnabled = visionEnabled;
        _agentName = agentName;
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

                return Task.FromResult(Ok($"Timer set for {minutes:0.#} minutes."));
            });

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
            Add(
                "look_at_shared_screen",
                "Look at whatever is being screen-shared right now and answer a question about it. Use when someone refers to 'this slide', 'this chart', 'on screen', and similar.",
                """{"type":"object","properties":{"question":{"type":"string","description":"What to find out from the screen"}},"required":["question"]}""",
                async (args, ct) =>
                {
                    byte[]? jpeg = await _actions.CaptureSharedScreenAsync(ct).ConfigureAwait(false);
                    if (jpeg is null)
                    {
                        return Error("Nobody is sharing their screen.");
                    }

                    string answer = await _textModel
                        .DescribeImageAsync(_visionModel, Str(args, "question"), jpeg, ct)
                        .ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { answer });
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

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });

    private sealed record LocalTool(ToolSpec Spec, Func<JsonElement, CancellationToken, Task<string>> Handler);
}
