using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using RtBot.Configuration;

namespace RtBot.Tools;

/// <summary>
/// Acquires delegated tokens as the agent's own Entra user account (an Agent 365 AI teammate),
/// so Work IQ, mail and calendar run as "Aria", not as a borrowed human account.
/// </summary>
/// <remarks>
/// Implements the Agent ID user flow: (1) the blueprint authenticates, here with the host's
/// managed identity as a federated credential, and asks for an exchange token scoped to the
/// agent identity via <c>fmi_path</c>; (2) the agent identity exchanges that for its own
/// exchange token; (3) both are presented in a <c>user_fic</c> grant for the agent user.
/// No secret is stored on the host. Falls back to the service-account provider if the agent
/// user flow fails, so a misconfiguration degrades rather than breaks the meeting.
/// </remarks>
public sealed class AgentUserTokenProvider : IDelegatedTokenProvider
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    private const string ExchangeScope = "api://AzureADTokenExchange/.default";
    private const string JwtBearer = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly AgentIdentityOptions _agent;
    private readonly string _tenantId;
    private readonly TokenCredential _hostIdentity;
    private readonly IDelegatedTokenProvider _fallback;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AgentUserTokenProvider> _logger;
    private readonly ConcurrentDictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private DateTimeOffset _lastFailureLog = DateTimeOffset.MinValue;

    public AgentUserTokenProvider(
        AgentIdentityOptions agent,
        string tenantId,
        TokenCredential hostIdentity,
        IDelegatedTokenProvider fallback,
        IHttpClientFactory httpFactory,
        ILogger<AgentUserTokenProvider> logger)
    {
        _agent = agent;
        _tenantId = tenantId;
        _hostIdentity = hostIdentity;
        _fallback = fallback;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string?> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        // The agent user's grants are per resource, so ask for the resource's .default and let
        // Entra issue every scope granted on it.
        string resourceScope = ToDefault(scope);
        if (_cache.TryGetValue(resourceScope, out Cached hit) && hit.Usable)
        {
            return hit.Token;
        }

        // After a failure, go straight to the fallback for a while instead of three round trips per call.
        if (!(_failedUntil.TryGetValue(resourceScope, out DateTimeOffset until) && DateTimeOffset.UtcNow < until))
        {
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cache.TryGetValue(resourceScope, out hit) && hit.Usable)
                {
                    return hit.Token;
                }

                bool coolingDown = _failedUntil.TryGetValue(resourceScope, out until) && DateTimeOffset.UtcNow < until;
                TokenResponse? user = coolingDown ? null : await AcquireAsync(resourceScope, cancellationToken).ConfigureAwait(false);
                if (user?.AccessToken is { } token)
                {
                    if (!_cache.ContainsKey(resourceScope))
                    {
                        _logger.LogWarning("Acting as AI teammate {Upn} for {Scope}.", _agent.AgentUserUpn, resourceScope);
                    }

                    _failedUntil.TryRemove(resourceScope, out _);
                    _cache[resourceScope] = new Cached(token, DateTimeOffset.UtcNow.AddSeconds(user.ExpiresIn));
                    return token;
                }

                if (!coolingDown)
                {
                    _failedUntil[resourceScope] = DateTimeOffset.UtcNow.AddMinutes(2);
                }
            }
            finally
            {
                _mutex.Release();
            }
        }

        return _agent.FallbackToServiceAccount
            ? await _fallback.GetTokenAsync(scope, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<TokenResponse?> AcquireAsync(string resourceScope, CancellationToken cancellationToken)
    {
        try
        {
            // The managed identity token is the blueprint's client assertion (federated credential).
            AccessToken hostToken = await _hostIdentity
                .GetTokenAsync(new TokenRequestContext([ExchangeScope]), cancellationToken)
                .ConfigureAwait(false);

            string? blueprintToken = (await PostAsync(new()
            {
                ["client_id"] = _agent.BlueprintId,
                ["scope"] = ExchangeScope,
                ["fmi_path"] = _agent.AgentIdentityId,
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = JwtBearer,
                ["client_assertion"] = hostToken.Token,
            }, "blueprint", cancellationToken).ConfigureAwait(false))?.AccessToken;
            if (blueprintToken is null)
            {
                return null;
            }

            string? identityToken = (await PostAsync(new()
            {
                ["client_id"] = _agent.AgentIdentityId,
                ["scope"] = ExchangeScope,
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = JwtBearer,
                ["client_assertion"] = blueprintToken,
            }, "agent identity", cancellationToken).ConfigureAwait(false))?.AccessToken;
            if (identityToken is null)
            {
                return null;
            }

            return await PostAsync(new()
            {
                ["client_id"] = _agent.AgentIdentityId,
                ["scope"] = resourceScope,
                ["grant_type"] = "user_fic",
                ["client_assertion_type"] = JwtBearer,
                ["client_assertion"] = blueprintToken,
                ["user_id"] = _agent.AgentUserId,
                ["user_federated_identity_credential"] = identityToken,
            }, $"agent user ({resourceScope})", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure("Agent user token flow failed for {Scope}: {Message}", resourceScope, ex.Message);
            return null;
        }
    }

    private async Task<TokenResponse?> PostAsync(Dictionary<string, string> form, string step, CancellationToken cancellationToken)
    {
        HttpClient http = _httpFactory.CreateClient(nameof(AgentUserTokenProvider));
        using HttpResponseMessage response = await http.PostAsync(
            new Uri($"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token"),
            new FormUrlEncodedContent(form),
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LogFailure("Agent user token step {Step} failed: {Body}", step, body.Length > 400 ? body[..400] : body);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
    }

    private void LogFailure(string message, string arg0, string arg1)
    {
        // A broken grant would otherwise log on every tool call.
        if (DateTimeOffset.UtcNow - _lastFailureLog > TimeSpan.FromMinutes(5))
        {
            _lastFailureLog = DateTimeOffset.UtcNow;
            _logger.LogWarning(message, arg0, arg1);
        }
    }

    /// <summary><c>https://graph.microsoft.com/Mail.Send</c> becomes <c>https://graph.microsoft.com/.default</c>.</summary>
    public static string ToDefault(string scope)
    {
        if (scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase))
        {
            return scope;
        }

        int slash = scope.LastIndexOf('/');
        return slash > scope.IndexOf("//", StringComparison.Ordinal) + 1 ? $"{scope[..slash]}/.default" : scope;
    }

    private readonly record struct Cached(string Token, DateTimeOffset ExpiresAt)
    {
        public bool Usable => DateTimeOffset.UtcNow < ExpiresAt - ExpiryMargin;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
