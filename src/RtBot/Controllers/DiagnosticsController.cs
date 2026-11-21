using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Calls;
using RtBot.Configuration;
using RtBot.Teams;
using RtBot.Tools;

namespace RtBot.Controllers;

/// <summary>Reports what the agent actually loaded, for verifying a deployment.</summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Operator)]
[Route("api/diagnostics")]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly ToolCatalog _tools;
    private readonly TeamsCallingService _calling;
    private readonly VoiceOptions _voice;
    private readonly ToolsOptions _toolOptions;

    public DiagnosticsController(
        ToolCatalog tools,
        TeamsCallingService calling,
        VoiceOptions voice,
        ToolsOptions toolOptions)
    {
        _tools = tools;
        _calling = calling;
        _voice = voice;
        _toolOptions = toolOptions;
    }

    [HttpGet]
    public IActionResult Get() =>
        Ok(new
        {
            agent = _voice.AgentName,
            model = _voice.Model,
            voice = _voice.VoiceName,
            respondsOnlyWhenAddressed = !_voice.RespondToEveryTurn,
            tools = new
            {
                count = _tools.Definitions.Count,
                names = _tools.ToolNames.OrderBy(n => n).ToArray(),
                workIqEnabled = _toolOptions.EnableWorkIq,
                recentCalls = _tools.RecentInvocations,
            },
            activeCalls = _calling.Client.Calls().Count,
            calls = _calling.ActiveHandlers.Select(h =>
            {
                var agenda = h.Meeting.SnapshotAgenda();
                var notes = h.Meeting.SnapshotNotes();
                return new
                {
                    callId = h.Meeting.CallId,
                    subject = h.Meeting.Subject,
                    elapsedMinutes = Math.Round(h.Meeting.Elapsed.TotalMinutes, 1),
                    participants = h.Meeting.Participants.Count,
                    restricted = h.Meeting.IsRestricted,
                    noticeGiven = h.NoticeGiven,
                    video = h.Tile is { } t ? new { active = t.IsActive, framesSent = t.FramesSent, format = t.Format } : null,
                    agenda = new { items = agenda.Items.Select(i => i.Title), current = agenda.Current?.Title, minutesIn = Math.Round(agenda.CurrentElapsed.TotalMinutes, 1) },
                    turns = h.Meeting.RecentTurns(600).Count,
                    decisions = notes.Decisions.Count,
                    actions = notes.Actions.Count,
                    parked = h.Meeting.ParkingLot.Count,
                    profile = h.Meeting.Profile.Name,
                    skills = h.Meeting.ActiveSkills.Select(s => s.Name),
                    tracked = h.Meeting.Tracked.ToDictionary(kv => kv.Key, kv => kv.Value.Count),
                    uncovered = h.Meeting.UncoveredChecklist.Count,
                };
            }),
        });

    /// <summary>The call's current video tile as a PNG.</summary>
    [HttpGet("calls/{callId}/tile")]
    public IActionResult Tile(string callId) =>
        _calling.TryGetHandler(callId, out CallHandler? handler) && handler?.Tile?.Snapshot() is { } png
            ? File(png, "image/png")
            : NotFound();
}
