using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Calls;
using RtBot.Teams;

namespace RtBot.Controllers;

/// <summary>Operator API for putting the agent into a meeting.</summary>
/// <remarks>
/// This host is reachable from the public internet because the Teams calling service requires
/// it to be, so every route here is authorised. An unauthenticated caller who holds any join
/// URL could otherwise drive the bot into that meeting.
/// </remarks>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Operator)]
[Route("api/meetings")]
public sealed class MeetingsController : ControllerBase
{
    private readonly TeamsCallingService _calling;
    private readonly ILogger<MeetingsController> _logger;

    public MeetingsController(TeamsCallingService calling, ILogger<MeetingsController> logger)
    {
        _calling = calling;
        _logger = logger;
    }

    [HttpPost("join")]
    public async Task<IActionResult> JoinAsync([FromBody] JoinRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JoinUrl))
        {
            return BadRequest(new { error = "joinUrl is required." });
        }

        try
        {
            ICall call = await _calling.JoinMeetingAsync(request.JoinUrl, cancellationToken).ConfigureAwait(false);
            return Ok(new { callId = call.Id, threadId = call.Resource?.ChatInfo?.ThreadId });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Join failed.");
            return StatusCode(500, new { error = "Join failed. See server logs." });
        }
    }

    [HttpPost("{callId}/leave")]
    public async Task<IActionResult> LeaveAsync(string callId, CancellationToken cancellationToken)
    {
        ICall? call = _calling.Client.Calls().FirstOrDefault(c => c.Id == callId);
        if (call is null)
        {
            return NotFound();
        }

        await call.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return Accepted();
    }

    /// <summary>Makes the agent speak into the meeting without waiting to be addressed.</summary>
    [HttpPost("{callId}/say")]
    public async Task<IActionResult> SayAsync(
        string callId,
        [FromBody] SayRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return BadRequest(new { error = "text is required." });
        }

        if (!_calling.TryGetHandler(callId, out CallHandler? handler) || handler is null)
        {
            return NotFound();
        }

        await handler.SayAsync(request.Text, cancellationToken).ConfigureAwait(false);
        return Accepted();
    }

    public sealed record SayRequest(string Text);

    [HttpGet]
    public IActionResult List() =>
        Ok(_calling.Client.Calls().Select(c => new
        {
            callId = c.Id,
            state = c.Resource?.State?.ToString(),
            threadId = c.Resource?.ChatInfo?.ThreadId,
        }));

    public sealed record JoinRequest(string JoinUrl);
}
