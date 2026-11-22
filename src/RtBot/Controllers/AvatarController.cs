using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using RtBot.Configuration;
using RtBot.Teams;

namespace RtBot.Controllers;

/// <summary>
/// Mints short-lived, domain-scoped D-ID Agents SDK client keys so the raw D-ID Basic API key
/// never leaves the Windows host or appears in browser bundles.
/// </summary>
[ApiController]
[EnableCors("CmrWorkspace")]
[Route("api/avatar")]
public sealed class AvatarController : ControllerBase
{
    private const string DidBaseUrl = "https://api.d-id.com";

    private readonly AvatarOptions _avatar;
    private readonly MediaOptions _media;
    private readonly VoiceOptions _voice;
    private readonly TeamsCallingService _calling;
    private readonly IAuthorizationService _authorization;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AvatarController> _logger;
    private static readonly SemaphoreSlim _agentGate = new(1, 1);
    private static volatile string? _cachedAgentId;

    public AvatarController(
        AvatarOptions avatar,
        MediaOptions media,
        VoiceOptions voice,
        TeamsCallingService calling,
        IAuthorizationService authorization,
        IHttpClientFactory httpFactory,
        ILogger<AvatarController> logger)
    {
        _avatar = avatar;
        _media = media;
        _voice = voice;
        _calling = calling;
        _authorization = authorization;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<IActionResult> Status([FromQuery] string? callId, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForAvatarAsync(callId).ConfigureAwait(false))
        {
            return Unauthorized();
        }

        string? apiKey = ResolveApiKey();
        return Ok(new
        {
            configured = !string.IsNullOrWhiteSpace(apiKey),
            agentId = ResolveSavedAgentId(),
            voiceId = _avatar.VoiceId,
            agentName = _voice.AgentName,
        });
    }

    [HttpPost("session")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateSession([FromBody] AvatarSessionRequest? request, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForAvatarAsync(request?.CallId).ConfigureAwait(false))
        {
            return Unauthorized();
        }

        string? apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Ok(new
            {
                configured = false,
                mode = "halo",
                voiceId = _avatar.VoiceId,
                message = "D-ID API key is not configured on this host; using 60fps GSAP radial voice aura.",
            });
        }

        List<string> allowedDomains = BuildAllowedDomains(request?.Origin);
        if (allowedDomains.Count == 0)
        {
            return BadRequest(new { error = "No valid HTTPS or localhost origin could be resolved for D-ID client key scoping." });
        }

        try
        {
            using HttpClient http = CreateDidClient(apiKey);
            string? agentId = await ResolveOrCreateAgentIdAsync(http, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(agentId))
            {
                return StatusCode(502, new { configured = false, mode = "halo", error = "Could not resolve or provision a D-ID agent." });
            }

            string payload = JsonSerializer.Serialize(new
            {
                allowed_domains = allowedDomains,
                ttl_seconds = 3600,
            });

            using HttpRequestMessage keyReq = new(HttpMethod.Post, $"{DidBaseUrl}/agents/{Uri.EscapeDataString(agentId)}/client-keys")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            using HttpResponseMessage keyResp = await http.SendAsync(keyReq, cancellationToken).ConfigureAwait(false);
            string body = await keyResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!keyResp.IsSuccessStatusCode)
            {
                // D-ID returns 400 or 409 when per-agent client-key cap is reached; recover and patch allowed_domains.
                if ((int)keyResp.StatusCode is 400 or 409)
                {
                    string? existingKey = await TryRecoverExistingClientKeyAsync(http, agentId, allowedDomains, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(existingKey))
                    {
                        return Ok(new
                        {
                            configured = true,
                            mode = "did-webrtc",
                            agentId,
                            clientKey = existingKey,
                            voiceId = _avatar.VoiceId,
                            expiresInSeconds = 3600,
                        });
                    }
                }

                _logger.LogWarning("D-ID client-key mint failed with HTTP {Status}.", (int)keyResp.StatusCode);
                return StatusCode(502, new { configured = false, mode = "halo", error = $"D-ID returned HTTP {(int)keyResp.StatusCode}." });
            }

            JsonNode? parsed = JsonNode.Parse(body);
            string? clientKey = parsed?["client_key"]?.GetValue<string>() ?? parsed?["key"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(clientKey))
            {
                return StatusCode(502, new { configured = false, mode = "halo", error = "D-ID response did not contain a client_key." });
            }

            return Ok(new
            {
                configured = true,
                mode = "did-webrtc",
                agentId,
                clientKey,
                voiceId = _avatar.VoiceId,
                expiresInSeconds = 3600,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to mint D-ID client session.");
            return StatusCode(502, new { configured = false, mode = "halo", error = "Failed to contact D-ID API." });
        }
    }

    [HttpPost("configure")]
    [Authorize(Policy = AuthorizationPolicies.Operator)]
    public IActionResult Configure([FromBody] AvatarConfigureRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest(new { error = "apiKey is required." });
        }

        if (OperatingSystem.IsWindows())
        {
            ProtectedSecretFile.Write(_avatar.ApiKeyFile, request.ApiKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.AgentId))
        {
            _cachedAgentId = request.AgentId.Trim();
            PersistAgentId(_cachedAgentId);
        }

        return Ok(new
        {
            configured = true,
            agentId = ResolveSavedAgentId(),
            voiceId = _avatar.VoiceId,
        });
    }

    private async Task<bool> IsAuthorizedForAvatarAsync(string? callId)
    {
        string liveKey = Request.Headers["X-Live-Key"].ToString();
        if (!string.IsNullOrEmpty(callId) &&
            !string.IsNullOrEmpty(liveKey) &&
            _calling.TryGetHandler(callId, out CallHandler? handler) &&
            handler is not null)
        {
            byte[] given = Encoding.UTF8.GetBytes(liveKey);
            byte[] expected = Encoding.UTF8.GetBytes(handler.Meeting.ViewKey);
            if (given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected))
            {
                return true;
            }
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            AuthorizationResult auth = await _authorization.AuthorizeAsync(User, null, AuthorizationPolicies.Operator).ConfigureAwait(false);
            return auth.Succeeded;
        }

        return false;
    }

    private string? ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_avatar.ApiKey))
        {
            return _avatar.ApiKey.Trim();
        }

        if (OperatingSystem.IsWindows())
        {
            string? fromDisk = ProtectedSecretFile.TryRead(_avatar.ApiKeyFile, _logger);
            if (!string.IsNullOrWhiteSpace(fromDisk))
            {
                return fromDisk.Trim();
            }
        }

        string? env = Environment.GetEnvironmentVariable("DID_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    private string? ResolveSavedAgentId()
    {
        if (!string.IsNullOrWhiteSpace(_cachedAgentId))
        {
            return _cachedAgentId;
        }

        if (!string.IsNullOrWhiteSpace(_avatar.AgentId))
        {
            return _avatar.AgentId.Trim();
        }

        string idPath = AgentIdCachePath();
        if (System.IO.File.Exists(idPath))
        {
            string saved = System.IO.File.ReadAllText(idPath).Trim();
            if (!string.IsNullOrWhiteSpace(saved))
            {
                _cachedAgentId = saved;
                return saved;
            }
        }

        return null;
    }

    private void PersistAgentId(string agentId)
    {
        try
        {
            string path = AgentIdCachePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            System.IO.File.WriteAllText(path, agentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist D-ID agent id cache.");
        }
    }

    private string AgentIdCachePath()
    {
        string? dir = Path.GetDirectoryName(_avatar.ApiKeyFile);
        return Path.Combine(string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir, "did-agent.id");
    }

    private HttpClient CreateDidClient(string rawKey)
    {
        HttpClient http = _httpFactory.CreateClient(nameof(AvatarController));
        string encoded = rawKey.Contains(':', StringComparison.Ordinal)
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(rawKey))
            : rawKey;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    private async Task<string?> ResolveOrCreateAgentIdAsync(HttpClient http, CancellationToken cancellationToken)
    {
        string? existing = ResolveSavedAgentId();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        await _agentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = ResolveSavedAgentId();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            using (HttpResponseMessage listResp = await http.GetAsync($"{DidBaseUrl}/agents/me?limit=25", cancellationToken).ConfigureAwait(false))
            {
                if (listResp.IsSuccessStatusCode)
                {
                    JsonNode? listJson = JsonNode.Parse(await listResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    JsonArray? agents = listJson?["agents"] as JsonArray ?? listJson as JsonArray;
                    if (agents is not null)
                    {
                        foreach (JsonNode? item in agents)
                        {
                            string? name = item?["preview_name"]?.GetValue<string>();
                            string? id = item?["id"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(id) &&
                                string.Equals(name, _voice.AgentName, StringComparison.OrdinalIgnoreCase))
                            {
                                _cachedAgentId = id;
                                PersistAgentId(id);
                                return id;
                            }
                        }

                        string? firstId = agents.FirstOrDefault()?["id"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(firstId))
                        {
                            _cachedAgentId = firstId;
                            PersistAgentId(firstId);
                            return firstId;
                        }
                    }
                }
            }

            // Provision a default Aria agent using a public D-ID Clips presenter and SoniaNeural voice.
            string? presenterId = await TryPickPublicPresenterAsync(http, cancellationToken).ConfigureAwait(false);
            var createBody = new JsonObject
            {
                ["preview_name"] = _voice.AgentName,
                ["embed"] = true,
                ["presenter"] = new JsonObject
                {
                    ["type"] = "clip",
                    ["presenter_id"] = presenterId ?? "amy-jcwCkr1grs",
                    ["voice"] = new JsonObject
                    {
                        ["type"] = "microsoft",
                        ["voice_id"] = _avatar.VoiceId,
                    },
                },
                ["llm"] = new JsonObject
                {
                    ["provider"] = "openai",
                    ["model"] = "gpt-4.1-mini",
                    ["instructions"] = $"You are {_voice.AgentName}, a concise British executive AI meeting facilitator. Answer in at most two crisp sentences.",
                },
            };

            using HttpRequestMessage createReq = new(HttpMethod.Post, $"{DidBaseUrl}/agents")
            {
                Content = new StringContent(createBody.ToJsonString(), Encoding.UTF8, "application/json"),
            };

            using HttpResponseMessage createResp = await http.SendAsync(createReq, cancellationToken).ConfigureAwait(false);
            if (!createResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("D-ID agent auto-create returned HTTP {Status}.", (int)createResp.StatusCode);
                return null;
            }

            JsonNode? created = JsonNode.Parse(await createResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string? createdId = created?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(createdId))
            {
                _cachedAgentId = createdId;
                PersistAgentId(createdId);
            }

            return createdId;
        }
        finally
        {
            _agentGate.Release();
        }
    }

    private static async Task<string?> TryPickPublicPresenterAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage resp = await http.GetAsync($"{DidBaseUrl}/clips/presenters?limit=10", cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            JsonNode? node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            JsonArray? presenters = node?["presenters"] as JsonArray;
            if (presenters is null)
            {
                return null;
            }

            foreach (JsonNode? p in presenters)
            {
                string? gender = p?["gender"]?.GetValue<string>();
                string? id = p?["presenter_id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id) && string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase))
                {
                    return id;
                }
            }

            return presenters.FirstOrDefault()?["presenter_id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryRecoverExistingClientKeyAsync(
        HttpClient http,
        string agentId,
        List<string> allowedDomains,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage getResp = await http.GetAsync(
            $"{DidBaseUrl}/agents/{Uri.EscapeDataString(agentId)}/client-keys",
            cancellationToken).ConfigureAwait(false);
        if (!getResp.IsSuccessStatusCode)
        {
            return null;
        }

        JsonNode? parsed = JsonNode.Parse(await getResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonNode? first = (parsed as JsonArray)?.FirstOrDefault()
                          ?? (parsed?["client_keys"] as JsonArray)?.FirstOrDefault()
                          ?? parsed;
        string? clientKey = first?["client_key"]?.GetValue<string>() ?? first?["key"]?.GetValue<string>();
        string? keyId = first?["id"]?.GetValue<string>() ?? clientKey;
        if (string.IsNullOrWhiteSpace(clientKey) || string.IsNullOrWhiteSpace(keyId))
        {
            return null;
        }

        string patchPayload = JsonSerializer.Serialize(new { allowed_domains = allowedDomains });
        using HttpRequestMessage patchReq = new(
            HttpMethod.Patch,
            $"{DidBaseUrl}/agents/{Uri.EscapeDataString(agentId)}/client-keys/{Uri.EscapeDataString(keyId)}")
        {
            Content = new StringContent(patchPayload, Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage _ = await http.SendAsync(patchReq, cancellationToken).ConfigureAwait(false);
        return clientKey;
    }

    private List<string> BuildAllowedDomains(string? callerOrigin)
    {
        HashSet<string> origins = new(StringComparer.OrdinalIgnoreCase);

        foreach (string configured in _avatar.AllowedDomains)
        {
            if (TryNormalizeOperatorOrigin(configured, out string? norm))
            {
                origins.Add(norm!);
            }
        }

        if (!string.IsNullOrWhiteSpace(_media.ServiceFqdn))
        {
            origins.Add($"https://{_media.ServiceFqdn.Trim()}");
        }

        origins.Add("https://play.managedapps.cloud.microsoft");
        origins.Add("https://play.preview.managedapps.cloud.microsoft");

        string headerOrigin = Request.Headers.Origin.ToString();
        if (TryNormalizeAllowedOrigin(headerOrigin, out string? normalizedHeader))
        {
            origins.Add(normalizedHeader!);
        }

        if (TryNormalizeAllowedOrigin(callerOrigin, out string? normalizedCaller))
        {
            origins.Add(normalizedCaller!);
        }

        return [.. origins];
    }

    internal static bool TryNormalizeOperatorOrigin(string? raw, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool isLocalhost = uri.Scheme == Uri.UriSchemeHttp &&
                           (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("127.0.0.1", StringComparison.Ordinal));
        if (uri.Scheme != Uri.UriSchemeHttps && !isLocalhost)
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    internal static bool TryNormalizeAllowedOrigin(string? raw, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool isLocalhost = uri.Scheme == Uri.UriSchemeHttp &&
                           (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                            uri.Host.Equals("127.0.0.1", StringComparison.Ordinal));
        bool isTrustedHttps = uri.Scheme == Uri.UriSchemeHttps &&
                              (uri.Host.EndsWith(".island.powerplatformusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                               uri.Host.EndsWith(".managedapps.cloud.microsoft", StringComparison.OrdinalIgnoreCase) ||
                               uri.Host.EndsWith(".cloudapp.azure.com", StringComparison.OrdinalIgnoreCase));

        if (!isLocalhost && !isTrustedHttps)
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public sealed record AvatarSessionRequest(string? CallId, string? Origin);

    public sealed record AvatarConfigureRequest(string ApiKey, string? AgentId);
}
