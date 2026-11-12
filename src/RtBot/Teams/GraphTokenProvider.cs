using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Identity.Client;
using RtBot.Configuration;

namespace RtBot.Teams;

/// <summary>
/// Supplies app-only Graph tokens to the communications SDK.
/// </summary>
/// <remarks>
/// Paired with <c>CommunicationsClientBuilder.SetAuthentication</c>, which wires the SDK's own
/// inbound notification validation. That matters: the notification endpoint is reachable from
/// the internet, and issuer and signing-key rotation is better handled by the SDK than by
/// hand-rolled validation here.
/// </remarks>
public sealed class GraphTokenProvider : ITokenProvider
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    private readonly AzureAdOptions _options;
    private readonly IConfidentialClientApplication _app;

    public GraphTokenProvider(AzureAdOptions options)
    {
        _options = options;

        _app = ConfidentialClientApplicationBuilder
            .Create(options.AppId)
            .WithClientSecret(options.AppSecret)
            .WithAuthority($"https://login.microsoftonline.com/{options.TenantId}")
            .Build();
    }

    public async Task<string> AcquireTokenAsync(string tenantId)
    {
        AuthenticationResult result = await _app
            .AcquireTokenForClient([GraphScope])
            .WithTenantId(string.IsNullOrWhiteSpace(tenantId) ? _options.TenantId : tenantId)
            .ExecuteAsync()
            .ConfigureAwait(false);

        return result.AccessToken;
    }
}
