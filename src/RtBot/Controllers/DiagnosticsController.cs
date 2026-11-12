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
        });
}
