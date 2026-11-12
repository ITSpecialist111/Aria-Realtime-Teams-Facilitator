using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using RtBot.Configuration;

namespace RtBot.Teams;

/// <summary>Looks up a scheduled meeting's subject and times, which drive time checks and the recap.</summary>
/// <remarks>
/// App-only access to <c>/users/{id}/onlineMeetings</c> needs both <c>OnlineMeetings.Read.All</c>
/// and a Teams application access policy granted to the bot. Without the policy Graph returns 403;
/// the agent then works without a schedule rather than failing.
/// </remarks>
public sealed class MeetingDetailsLookup
{
    private readonly IConfidentialClientApplication _app;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MeetingDetailsLookup> _logger;

    public MeetingDetailsLookup(AzureAdOptions azureAd, IHttpClientFactory httpFactory, ILogger<MeetingDetailsLookup> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _app = ConfidentialClientApplicationBuilder
            .Create(azureAd.AppId)
            .WithClientSecret(azureAd.AppSecret)
            .WithAuthority($"https://login.microsoftonline.com/{azureAd.TenantId}")
            .Build();
    }

    public async Task<(string? Subject, DateTimeOffset? Start, DateTimeOffset? End)> FindAsync(
        string organizerId,
        string joinUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            AuthenticationResult token = await _app
                .AcquireTokenForClient(["https://graph.microsoft.com/.default"])
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            string filter = Uri.EscapeDataString($"JoinWebUrl eq '{joinUrl.Replace("'", "''")}'");
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(organizerId)}/onlineMeetings?$filter={filter}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            using HttpResponseMessage response = await _httpFactory.CreateClient(nameof(MeetingDetailsLookup))
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meeting details lookup failed: {Status} {Body}", (int)response.StatusCode, body);
                return (null, null, null);
            }

            JsonNode? meeting = (JsonNode.Parse(body)?["value"] as JsonArray)?.FirstOrDefault();
            if (meeting is null)
            {
                return (null, null, null);
            }

            return (
                meeting["subject"]?.GetValue<string>(),
                ParseTime(meeting["startDateTime"]),
                ParseTime(meeting["endDateTime"]));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meeting details lookup failed.");
            return (null, null, null);
        }
    }

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        DateTimeOffset.TryParse(node?.GetValue<string>(), out DateTimeOffset value) ? value : null;
}
