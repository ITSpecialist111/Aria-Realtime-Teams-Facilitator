using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RtBot.Configuration;

namespace RtBot.Tools;

/// <summary>
/// Acquires delegated access tokens for services that refuse app-only auth.
/// </summary>
/// <remarks>
/// Work IQ MCP is delegated-only by design: every call runs as a signed-in user so that
/// Microsoft 365 permissions are enforced and the agent can never read data the user cannot.
/// A meeting bot has no interactive user, so a nominated service account consents once via
/// <c>scripts/Connect-WorkIq.ps1</c> and the resulting refresh token is kept in the token
/// store. This class exchanges it for access tokens and persists each rotated refresh token.
/// </remarks>
public sealed class DelegatedTokenProvider : IDelegatedTokenProvider
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly ITokenStore _store;
    private readonly AzureAdOptions _azureAd;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DelegatedTokenProvider> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<string, CachedToken> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DelegatedTokenProvider(
        ITokenStore store,
        AzureAdOptions azureAd,
        IHttpClientFactory httpClientFactory,
        ILogger<DelegatedTokenProvider> logger)
    {
        _store = store;
        _azureAd = azureAd;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(scope, out CachedToken cached) && cached.IsUsable)
        {
            return cached.AccessToken;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(scope, out cached) && cached.IsUsable)
            {
                return cached.AccessToken;
            }

            string? refreshToken = await _store.ReadRefreshTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                _logger.LogWarning(
                    "No delegated refresh token available. Run scripts/Connect-WorkIq.ps1 to consent once.");
                return null;
            }

            TokenResponse? response = await RedeemAsync(refreshToken, scope, cancellationToken).ConfigureAwait(false);
            if (response?.AccessToken is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(response.RefreshToken) && response.RefreshToken != refreshToken)
            {
                await _store.WriteRefreshTokenAsync(response.RefreshToken, cancellationToken).ConfigureAwait(false);
            }

            _cache[scope] = new CachedToken(
                response.AccessToken,
                DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn));

            return response.AccessToken;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<TokenResponse?> RedeemAsync(
        string refreshToken,
        string scope,
        CancellationToken cancellationToken)
    {
        HttpClient http = _httpClientFactory.CreateClient(nameof(DelegatedTokenProvider));

        // No client_secret: the refresh token was issued to this app acting as a public client
        // during the device code flow, and Entra rejects a secret on redemption (AADSTS700025).
        Dictionary<string, string> form = new()
        {
            ["client_id"] = _azureAd.AppId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = $"{scope} offline_access",
        };

        Uri endpoint = new($"https://login.microsoftonline.com/{_azureAd.TenantId}/oauth2/v2.0/token");
        using HttpResponseMessage http_response = await http
            .PostAsync(endpoint, new FormUrlEncodedContent(form), cancellationToken)
            .ConfigureAwait(false);

        if (!http_response.IsSuccessStatusCode)
        {
            string body = await http_response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "Delegated token request for {Scope} failed: {Status}. {Body}",
                scope,
                http_response.StatusCode,
                Redact(body));
            return null;
        }

        return await http_response.Content
            .ReadFromJsonAsync<TokenResponse>(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Strips anything token-shaped out of an error body before it reaches a log.</summary>
    private static string Redact(string body) =>
        body.Length > 512 ? string.Concat(body.AsSpan(0, 512), "...") : body;

    private readonly record struct CachedToken(string AccessToken, DateTimeOffset ExpiresAt)
    {
        public bool IsUsable => DateTimeOffset.UtcNow < ExpiresAt - ExpiryMargin;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
