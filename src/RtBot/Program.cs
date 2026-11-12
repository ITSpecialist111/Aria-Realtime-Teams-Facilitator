using System.Net;
using System.Security.Cryptography.X509Certificates;
using Azure.AI.VoiceLive;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenTelemetry;
using RtBot.Chat;
using RtBot.Configuration;
using RtBot.Controllers;
using RtBot.Facilitator;
using RtBot.Observability;
using RtBot.Teams;
using RtBot.Tools;
using RtBot.Voice;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Without this the Service Control Manager never sees the host report Running and kills it.
builder.Host.UseWindowsService(o => o.ServiceName = "RtBot");

builder.Services.Configure<RtBotOptions>(builder.Configuration.GetSection(RtBotOptions.SectionName));
RtBotOptions options = builder.Configuration.GetSection(RtBotOptions.SectionName).Get<RtBotOptions>() ?? new();

if (string.IsNullOrWhiteSpace(options.AzureAd.AppSecret))
{
    options.AzureAd.AppSecret =
        ProtectedSecretFile.TryRead(options.AzureAd.AppSecretFile) ?? string.Empty;
}

// IOptions<RtBotOptions> binds from configuration independently, so the secret resolved above
// has to be applied there too or anything reading IOptions sees an empty value.
string resolvedSecret = options.AzureAd.AppSecret;
builder.Services.PostConfigure<RtBotOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.AzureAd.AppSecret))
    {
        o.AzureAd.AppSecret = resolvedSecret;
    }
});

// The observability exporter authenticates as the agent itself; unless it has been given a
// separate blueprint credential it reuses the bot's own registration.
if (string.IsNullOrWhiteSpace(options.Observability.ClientId))
{
    options.Observability.ClientId = options.AzureAd.AppId;
    options.Observability.ClientSecret = resolvedSecret;
}

if (string.IsNullOrWhiteSpace(options.Observability.TenantId))
{
    options.Observability.TenantId = options.AzureAd.TenantId;
}

if (string.IsNullOrWhiteSpace(options.Observability.AgentId))
{
    options.Observability.AgentId = options.AzureAd.AppId;
}

ConfigureTelemetry(builder, options.Observability);

// win-acme renews the certificate every ~60 days and the thumbprint changes each time. Resolve the
// live one once so Kestrel and the media platform agree, instead of crashing after a renewal.
string resolvedThumbprint = ResolveCertificateThumbprint(options.Media);
options.Media.CertificateThumbprint = resolvedThumbprint;
builder.Services.PostConfigure<RtBotOptions>(o => o.Media.CertificateThumbprint = resolvedThumbprint);

ConfigureKestrel(builder, options.Media);

builder.Services.AddSingleton(options.AzureAd);
builder.Services.AddSingleton(options.Voice);
builder.Services.AddSingleton(options.Tools);
builder.Services.AddHttpClient();
builder.Services.AddControllers();

ConfigureAuthentication(builder, options.AzureAd);

builder.Services.AddSingleton<IGraphLogger>(_ => new GraphLogger("RtBot", redirectToTrace: true));

builder.Services.AddSingleton<TokenCredential>(_ =>
{
    // On the media host this resolves to the user-assigned identity; on a developer machine
    // it falls through to the signed-in az CLI account. DefaultAzureCredential is avoided
    // because its IMDS probe stalls for seconds off-Azure and then masks the real failure.
    string? clientId = builder.Configuration["RtBot:ManagedIdentityClientId"];

    ManagedIdentityId identityId = string.IsNullOrWhiteSpace(clientId)
        ? ManagedIdentityId.SystemAssigned
        : ManagedIdentityId.FromUserAssignedClientId(clientId);

    ManagedIdentityCredential managedIdentity = new(new ManagedIdentityCredentialOptions(identityId));

    return new ChainedTokenCredential(managedIdentity, new AzureCliCredential());
});

builder.Services.AddSingleton<ITokenStore>(sp =>
{
    string? vaultUri = builder.Configuration["RtBot:KeyVaultUri"];
    if (!string.IsNullOrWhiteSpace(vaultUri))
    {
        return new KeyVaultTokenStore(
            new Uri(vaultUri),
            sp.GetRequiredService<TokenCredential>(),
            sp.GetRequiredService<ILogger<KeyVaultTokenStore>>());
    }

    string path = options.Tools.TokenCachePath;
    return string.IsNullOrWhiteSpace(path)
        ? new NullTokenStore()
        : new ProtectedFileTokenStore(path, sp.GetRequiredService<ILogger<ProtectedFileTokenStore>>());
});

builder.Services.AddSingleton<IDelegatedTokenProvider, DelegatedTokenProvider>();
builder.Services.AddSingleton(options.Facilitator);
builder.Services.AddSingleton<BotChatClient>();
builder.Services.AddSingleton<MeetingDetailsLookup>();
builder.Services.AddSingleton<MeetingMailer>();
builder.Services.AddSingleton(sp => new TextModelClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TextModelClient)),
    sp.GetRequiredService<TokenCredential>(),
    new Uri(options.Voice.Endpoint),
    sp.GetRequiredService<ILogger<TextModelClient>>()));

builder.Services.AddSingleton(sp =>
{
    VoiceOptions voice = options.Voice;
    Uri endpoint = new(voice.Endpoint);

    return string.IsNullOrWhiteSpace(voice.ApiKey)
        ? new VoiceLiveClient(endpoint, sp.GetRequiredService<TokenCredential>())
        : new VoiceLiveClient(endpoint, new Azure.AzureKeyCredential(voice.ApiKey));
});

builder.Services.AddSingleton<ToolCatalog>();

// One bridge per call: each owns its own websocket and playback clock.
builder.Services.AddTransient<VoiceLiveBridge>();

builder.Services.AddSingleton<TeamsCallingService>();
builder.Services.AddHostedService<BotStartupService>();

WebApplication app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Deliberately unauthenticated: load balancer probes need it.
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }))
    .AllowAnonymous();

app.Run();

static void ConfigureAuthentication(WebApplicationBuilder builder, AzureAdOptions azureAd)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = $"https://login.microsoftonline.com/{azureAd.TenantId}/v2.0";
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers =
                [
                    $"https://login.microsoftonline.com/{azureAd.TenantId}/v2.0",
                    $"https://sts.windows.net/{azureAd.TenantId}/",
                ],
                ValidateAudience = true,
                ValidAudiences = [azureAd.AppId, $"api://{azureAd.AppId}"],
                ValidateLifetime = true,
            };
        })
        // Inbound Teams chat activities carry a Bot Connector token, not an Entra one.
        .AddJwtBearer(BotFrameworkAuth.Scheme, options =>
        {
            options.MetadataAddress = "https://login.botframework.com/v1/.well-known/openidconfiguration";
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "https://api.botframework.com",
                ValidateAudience = true,
                ValidAudience = azureAd.AppId,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
            };
        });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(AuthorizationPolicies.Operator, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                HasRole(context.User, AuthorizationPolicies.OperatorRole) ||
                HasScope(context.User, AuthorizationPolicies.OperatorScope)));
}

static bool HasRole(System.Security.Claims.ClaimsPrincipal user, string role) =>
    user.HasClaim("roles", role) ||
    user.HasClaim("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", role);

// The scp claim is a space-delimited list, so a whole-value match would miss it.
static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string scope) =>
    (user.FindFirst("scp")?.Value ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/scope")?.Value)
        ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains(scope, StringComparer.OrdinalIgnoreCase) == true;

static void ConfigureTelemetry(WebApplicationBuilder builder, ObservabilityOptions observability)
{
    builder.UseMicrosoftOpenTelemetry(o =>
    {
        ExportTarget targets = ExportTarget.Console;

        if (observability.EnableAgent365 && !string.IsNullOrWhiteSpace(observability.AgentId))
        {
            targets |= ExportTarget.Agent365;

            // The agent authenticates as itself, not on behalf of a user, so the exporter must
            // post to the S2S path. Without this it uses the OBO path and every export 401s.
            o.Agent365.UseS2SEndpoint = true;
            o.Agent365.TokenResolver = (_, _) => AcquireObservabilityTokenAsync(observability);
        }

        if (!string.IsNullOrWhiteSpace(observability.ApplicationInsightsConnectionString))
        {
            targets |= ExportTarget.AzureMonitor;
            o.AzureMonitor.ConnectionString = observability.ApplicationInsightsConnectionString;
        }

        o.Exporters = targets;

        // The distro suppresses infrastructure instrumentation by default; without these the
        // Graph, Voice Live and MCP calls never appear on a trace.
        o.Instrumentation.EnableAspNetCoreInstrumentation = true;
        o.Instrumentation.EnableHttpClientInstrumentation = true;
        o.Instrumentation.EnableAzureSdkInstrumentation = true;
    });

    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource(AgentTelemetry.SourceName));
}

/// <summary>Client-credentials token for the Agent 365 observability endpoint.</summary>
static async Task<string?> AcquireObservabilityTokenAsync(ObservabilityOptions observability)
{
    const string ObservabilityResource = "9b975845-388f-4429-889e-eab1ef63949c";

    IConfidentialClientApplication app = ConfidentialClientApplicationBuilder
        .Create(observability.ClientId)
        .WithClientSecret(observability.ClientSecret)
        .WithAuthority($"https://login.microsoftonline.com/{observability.TenantId}")
        .Build();

    AuthenticationResult result = await app
        .AcquireTokenForClient([$"{ObservabilityResource}/.default"])
        .ExecuteAsync()
        .ConfigureAwait(false);

    return result.AccessToken;
}

static void ConfigureKestrel(WebApplicationBuilder builder, MediaOptions media)
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.Listen(IPAddress.Any, media.SignalingInternalPort, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;

            if (!string.IsNullOrWhiteSpace(media.CertificateThumbprint))
            {
                listen.UseHttps(FindCertificate(media.CertificateThumbprint));
            }
        });
    });
}

static string ResolveCertificateThumbprint(MediaOptions media)
{
    using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadOnly);

    if (!string.IsNullOrWhiteSpace(media.CertificateThumbprint) &&
        store.Certificates.Find(X509FindType.FindByThumbprint, media.CertificateThumbprint, validOnly: false).Count > 0)
    {
        return media.CertificateThumbprint;
    }

    if (string.IsNullOrWhiteSpace(media.ServiceFqdn))
    {
        return media.CertificateThumbprint;
    }

    // Newest unexpired certificate for this host that has a private key.
    X509Certificate2? current = store.Certificates
        .Find(X509FindType.FindBySubjectName, media.ServiceFqdn, validOnly: false)
        .Where(c => c.HasPrivateKey && c.NotAfter > DateTime.Now && c.NotBefore <= DateTime.Now)
        .OrderByDescending(c => c.NotAfter)
        .FirstOrDefault();

    return current?.Thumbprint ?? media.CertificateThumbprint;
}

static X509Certificate2 FindCertificate(string thumbprint)
{
    using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadOnly);

    X509Certificate2Collection matches = store.Certificates.Find(
        X509FindType.FindByThumbprint,
        thumbprint,
        validOnly: false);

    return matches.Count > 0
        ? matches[0]
        : throw new InvalidOperationException(
            $"No certificate with thumbprint '{thumbprint}' in LocalMachine\\My. The media platform " +
            "requires a public-CA certificate whose subject matches Media:ServiceFqdn.");
}

/// <summary>Initialises the media platform and connects the tool servers once at start-up.</summary>
internal sealed class BotStartupService : IHostedService
{
    private readonly TeamsCallingService _calling;
    private readonly ToolCatalog _tools;
    private readonly IDelegatedTokenProvider _tokenProvider;
    private readonly RtBotOptions _options;
    private readonly ILogger<BotStartupService> _logger;

    public BotStartupService(
        TeamsCallingService calling,
        ToolCatalog tools,
        IDelegatedTokenProvider tokenProvider,
        IOptions<RtBotOptions> options,
        ILogger<BotStartupService> logger)
    {
        _calling = calling;
        _tools = tools;
        _tokenProvider = tokenProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _calling.Initialize();
        await RegisterToolsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RegisterToolsAsync(CancellationToken cancellationToken)
    {
        ToolsOptions tools = _options.Tools;

        if (tools.EnableWorkIq)
        {
            await TryAddServerAsync(
                new McpServerOptions
                {
                    Name = "workiq",
                    Url = tools.WorkIqMcpUrl,
                    Scope = tools.WorkIqScope,
                    RequiresDelegatedAuth = true,
                },
                cancellationToken).ConfigureAwait(false);
        }

        foreach (McpServerOptions server in tools.AdditionalServers)
        {
            await TryAddServerAsync(server, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Tool catalog ready with {Count} tools.", _tools.Definitions.Count);
    }

    private async Task TryAddServerAsync(McpServerOptions server, CancellationToken cancellationToken)
    {
        try
        {
            await _tools.AddMcpServerAsync(server, _tokenProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A tool server being unreachable degrades the agent; it must not stop it starting.
            _logger.LogError(ex, "Could not register MCP server {Server}.", server.Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
