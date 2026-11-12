using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using RtBot.Configuration;

namespace RtBot.Chat;

/// <summary>
/// Posts into a meeting's chat as the bot, through the Bot Connector service.
/// </summary>
/// <remarks>
/// Graph's app-only <c>POST /chats/{id}/messages</c> is reserved for migration and returns
/// Forbidden. The supported route is a proactive Bot Framework message, which requires the
/// Teams app to be installed in that chat first; this client installs it on first use.
/// </remarks>
public sealed class BotChatClient
{
    private const string BotScope = "https://api.botframework.com/.default";
    private const string GraphScope = "https://graph.microsoft.com/.default";

    private readonly AzureAdOptions _azureAd;
    private readonly ToolsOptions _tools;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BotChatClient> _logger;
    private readonly IConfidentialClientApplication _app;
    private readonly ConcurrentDictionary<string, Task<bool>> _installs = new(StringComparer.Ordinal);

    public BotChatClient(
        AzureAdOptions azureAd,
        ToolsOptions tools,
        IHttpClientFactory httpFactory,
        ILogger<BotChatClient> logger)
    {
        _azureAd = azureAd;
        _tools = tools;
        _httpFactory = httpFactory;
        _logger = logger;

        _app = ConfidentialClientApplicationBuilder
            .Create(azureAd.AppId)
            .WithClientSecret(azureAd.AppSecret)
            .WithAuthority($"https://login.microsoftonline.com/{azureAd.TenantId}")
            .Build();
    }

    /// <summary>Posts markdown to the meeting chat. Returns false (and logs) on any failure.</summary>
    public async Task<bool> PostAsync(string threadId, string markdown, CancellationToken cancellationToken)
    {
        if (!_tools.EnableMeetingChat || string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        try
        {
            await EnsureInstalledAsync(threadId, cancellationToken).ConfigureAwait(false);
            return await SendActivityAsync(threadId, markdown, replyToId: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed chat post must never take down the voice session.
            _logger.LogWarning(ex, "Could not post to meeting chat {ThreadId}.", threadId);
            return false;
        }
    }

    /// <summary>Replies to an inbound activity, using the service URL that activity arrived from.</summary>
    public async Task<bool> ReplyAsync(
        string serviceUrl,
        string conversationId,
        string? replyToId,
        string markdown,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendActivityAsync(conversationId, markdown, replyToId, cancellationToken, serviceUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reply in conversation {ConversationId}.", conversationId);
            return false;
        }
    }

    private async Task<bool> SendActivityAsync(
        string conversationId,
        string markdown,
        string? replyToId,
        CancellationToken cancellationToken,
        string? serviceUrl = null)
    {
        string baseUrl = (serviceUrl ?? _tools.BotServiceUrl).TrimEnd('/') + "/";

        // Only post to Bot Connector hosts; serviceUrl on an inbound activity is attacker-influenced
        // until the activity's token has been validated, and the bot's token must never go elsewhere.
        Uri target = new(baseUrl);
        if (!target.Host.EndsWith(".trafficmanager.net", StringComparison.OrdinalIgnoreCase) &&
            !target.Host.EndsWith(".botframework.com", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing to post to unexpected service URL {Host}.", target.Host);
            return false;
        }

        string path = replyToId is null
            ? $"v3/conversations/{Uri.EscapeDataString(conversationId)}/activities"
            : $"v3/conversations/{Uri.EscapeDataString(conversationId)}/activities/{Uri.EscapeDataString(replyToId)}";

        JsonObject activity = new()
        {
            ["type"] = "message",
            ["textFormat"] = "markdown",
            ["text"] = markdown,
        };

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(target, path))
        {
            Content = JsonContent.Create(activity),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(BotScope, cancellationToken).ConfigureAwait(false));

        using HttpResponseMessage response = await _httpFactory.CreateClient(nameof(BotChatClient))
            .SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Bot Connector post to {Conversation} failed: {Status} {Body}", conversationId, (int)response.StatusCode, body);
        return false;
    }

    /// <summary>Installs the Teams app into the chat once per thread. Already-installed is success.</summary>
    private async Task<bool> EnsureInstalledAsync(string threadId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_tools.TeamsAppCatalogId))
        {
            return true;
        }

        Task<bool> install = _installs.GetOrAdd(threadId, InstallAsync);
        bool installed = await install.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Retry a failed install next time rather than caching the failure forever.
        if (!installed)
        {
            _installs.TryRemove(new KeyValuePair<string, Task<bool>>(threadId, install));
        }

        return installed;
    }

    private async Task<bool> InstallAsync(string threadId)
    {
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(threadId)}/installedApps")
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["teamsApp@odata.bind"] = $"https://graph.microsoft.com/v1.0/appCatalogs/teamsApps/{_tools.TeamsAppCatalogId}",
                    ["consentedPermissionSet"] = new JsonObject
                    {
                        ["resourceSpecificPermissions"] = new JsonArray(_tools.TeamsAppRscPermissions
                            .Select(p => (JsonNode)new JsonObject { ["permissionValue"] = p, ["permissionType"] = "application" })
                            .ToArray()),
                    },
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(GraphScope, CancellationToken.None).ConfigureAwait(false));

            using HttpResponseMessage response = await _httpFactory.CreateClient(nameof(BotChatClient))
                .SendAsync(request).ConfigureAwait(false);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogInformation("App installed in meeting chat {ThreadId} ({Status}).", threadId, (int)response.StatusCode);
                return true;
            }

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogWarning("Installing the app in chat {ThreadId} failed: {Status} {Body}", threadId, (int)response.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Installing the app in chat {ThreadId} failed.", threadId);
            return false;
        }
    }

    private async Task<string> TokenAsync(string scope, CancellationToken cancellationToken)
    {
        AuthenticationResult result = await _app
            .AcquireTokenForClient([scope])
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.AccessToken;
    }
}
