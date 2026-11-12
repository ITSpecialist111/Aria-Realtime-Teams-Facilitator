using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph.Communications.Client;
using RtBot.Teams;

namespace RtBot.Controllers;

/// <summary>Receives calling notifications from the Teams platform.</summary>
/// <remarks>
/// The route carries the instance ordinal because a media call is pinned to the machine that
/// created it, so the ingress must be able to route a notification back to that exact instance.
/// <para>
/// Left anonymous to ASP.NET Core deliberately: the caller is Microsoft's calling service, not
/// an Entra principal this app can authorise. The bearer token it presents is validated by the
/// communications SDK inside <c>ProcessNotificationAsync</c>.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
public sealed class PlatformCallController : ControllerBase
{
    private readonly TeamsCallingService _calling;
    private readonly ILogger<PlatformCallController> _logger;

    public PlatformCallController(TeamsCallingService calling, ILogger<PlatformCallController> logger)
    {
        _calling = calling;
        _logger = logger;
    }

    [HttpPost("api/calling")]
    [HttpPost("{ordinal:int}/api/calling")]
    public Task<IActionResult> OnIncomingRequestAsync() => ForwardAsync();

    [HttpPost("api/calling/notification")]
    [HttpPost("{ordinal:int}/api/calling/notification")]
    public Task<IActionResult> OnNotificationAsync() => ForwardAsync();

    private async Task<IActionResult> ForwardAsync()
    {
        using HttpRequestMessage message = await ToRequestMessageAsync(Request).ConfigureAwait(false);

        // The SDK validates the bearer token, acknowledges with 202 and queues the notification.
        using HttpResponseMessage response = await _calling.Client
            .ProcessNotificationAsync(message)
            .ConfigureAwait(false);

        string body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return StatusCode((int)response.StatusCode, body);
    }

    private static async Task<HttpRequestMessage> ToRequestMessageAsync(HttpRequest request)
    {
        HttpRequestMessage message = new(new HttpMethod(request.Method), request.GetEncodedUri());

        request.EnableBuffering();
        MemoryStream buffer = new();
        await request.Body.CopyToAsync(buffer).ConfigureAwait(false);
        buffer.Position = 0;
        request.Body.Position = 0;

        message.Content = new StreamContent(buffer);

        foreach ((string key, Microsoft.Extensions.Primitives.StringValues value) in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(key, (IEnumerable<string>)value))
            {
                message.Content.Headers.TryAddWithoutValidation(key, (IEnumerable<string>)value);
            }
        }

        return message;
    }
}

internal static class HttpRequestExtensions
{
    public static Uri GetEncodedUri(this HttpRequest request) =>
        new($"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}");
}
