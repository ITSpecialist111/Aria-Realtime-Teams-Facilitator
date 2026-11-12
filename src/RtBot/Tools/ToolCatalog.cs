using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure.AI.VoiceLive;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RtBot.Configuration;
using RtBot.Observability;

namespace RtBot.Tools;

/// <summary>
/// Everything the agent can do, exposed to the model as Voice Live function tools.
/// </summary>
/// <remarks>
/// Tool calls are dispatched here rather than handed to Voice Live's native remote-MCP support
/// so that every invocation produces an <c>execute_tool</c> span. Agent 365 observability, and
/// therefore the Purview and Defender signal derived from it, depends on those spans existing.
/// </remarks>
public sealed class ToolCatalog : IAsyncDisposable
{
    private readonly List<VoiceLiveToolDefinition> _definitions = [];
    private readonly List<ToolSpec> _specs = [];
    private readonly Dictionary<string, ToolBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<McpClient> _clients = [];
    private readonly List<HttpClient> _httpClients = [];
    private readonly ConcurrentQueue<ToolInvocation> _recent = new();
    private readonly ILogger<ToolCatalog> _logger;

    public ToolCatalog(ILogger<ToolCatalog> logger) => _logger = logger;

    public IReadOnlyList<VoiceLiveToolDefinition> Definitions => _definitions;

    /// <summary>The same tools in a model-agnostic form, for the text model and per-call toolsets.</summary>
    public IReadOnlyList<ToolSpec> Specs => _specs;

    /// <summary>Names of every registered tool, in the form the model sees them.</summary>
    public IReadOnlyCollection<string> ToolNames => _bindings.Keys;

    /// <summary>The most recent tool invocations, newest last. Diagnostics only.</summary>
    public IReadOnlyCollection<ToolInvocation> RecentInvocations => _recent.ToArray();

    /// <summary>Registers a tool implemented in-process.</summary>
    public void AddLocalTool(
        string name,
        string description,
        BinaryData parametersSchema,
        Func<JsonElement, CancellationToken, Task<string>> handler)
    {
        _definitions.Add(new VoiceLiveFunctionDefinition(name)
        {
            Description = description,
            Parameters = parametersSchema,
        });

        _specs.Add(new ToolSpec(name, description, parametersSchema.ToString(), AccessesOrganisationData: false));
        _bindings[name] = new ToolBinding(name, handler);
    }

    /// <summary>Connects to a remote MCP server and registers every tool it advertises.</summary>
    public async Task AddMcpServerAsync(
        McpServerOptions server,
        IDelegatedTokenProvider tokenProvider,
        CancellationToken cancellationToken)
    {
        HttpClient? httpClient = null;

        if (server.RequiresDelegatedAuth)
        {
            // Fail fast with a clear message rather than surfacing a 401 from the first tool call.
            string? token = await tokenProvider
                .GetTokenAsync(server.Scope, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning(
                    "Skipping MCP server {Server}: no delegated token for scope {Scope}.",
                    server.Name,
                    server.Scope);
                return;
            }

            // A handler, not a static header: the token expires long before the process does.
            httpClient = new HttpClient(new DelegatedAuthHandler(tokenProvider, server.Scope));
            _httpClients.Add(httpClient);
        }

        HttpClientTransportOptions transportOptions = new()
        {
            Endpoint = new Uri(server.Url),
            Name = server.Name,
        };

        McpClient client = await McpClient.CreateAsync(
            httpClient is null
                ? new HttpClientTransport(transportOptions)
                : new HttpClientTransport(transportOptions, httpClient),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _clients.Add(client);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        foreach (McpClientTool tool in tools)
        {
            // Several servers can advertise the same verb, so names are namespaced.
            string exposedName = Sanitise($"{server.Name}_{tool.Name}");

            if (_bindings.ContainsKey(exposedName))
            {
                _logger.LogWarning("Duplicate tool name {Name}; skipping.", exposedName);
                continue;
            }

            _definitions.Add(new VoiceLiveFunctionDefinition(exposedName)
            {
                Description = Truncate(tool.Description ?? tool.Name, 1024),
                Parameters = BinaryData.FromObjectAsJson(tool.JsonSchema),
            });

            // Anything from a remote server is treated as reaching organisation data, so it is
            // what gets switched off when an external participant joins.
            _specs.Add(new ToolSpec(
                exposedName,
                Truncate(tool.Description ?? tool.Name, 1024),
                tool.JsonSchema.GetRawText(),
                AccessesOrganisationData: true));

            McpClientTool captured = tool;
            _bindings[exposedName] = new ToolBinding(
                exposedName,
                (args, ct) => CallMcpToolAsync(client, captured, args, ct));
        }

        _logger.LogInformation(
            "MCP server {Server} contributed {Count} tools.",
            server.Name,
            tools.Count);
    }

    public async Task<string> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        if (!_bindings.TryGetValue(name, out ToolBinding? binding))
        {
            return JsonSerializer.Serialize(new { error = $"Unknown tool '{name}'." });
        }

        JsonElement arguments = ParseArguments(argumentsJson);

        using Activity? activity = AgentTelemetry.StartToolSpan(name);
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            string result = await binding.Handler(arguments, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("tool.status", "ok");
            activity?.SetTag("tool.duration_ms", stopwatch.ElapsedMilliseconds);
            Record(name, stopwatch.ElapsedMilliseconds, true, result.Length);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("tool.status", "error");
            Record(name, stopwatch.ElapsedMilliseconds, false, 0);
            throw;
        }
    }

    private void Record(string name, long durationMs, bool succeeded, int responseLength)
    {
        _recent.Enqueue(new ToolInvocation(DateTimeOffset.UtcNow, name, durationMs, succeeded, responseLength));

        while (_recent.Count > 20 && _recent.TryDequeue(out _))
        {
            // Keep only a short rolling window.
        }
    }

    private static JsonElement ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }

        try
        {
            return JsonDocument.Parse(argumentsJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    private async Task<string> CallMcpToolAsync(
        McpClient client,
        McpClientTool tool,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> args = [];

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in arguments.EnumerateObject())
            {
                args[property.Name] = property.Value.Clone();
            }
        }

        CallToolResult result = await client
            .CallToolAsync(tool.Name, args, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return Flatten(result);
    }

    /// <summary>
    /// Renders an MCP result as text for the model.
    /// </summary>
    /// <remarks>
    /// Work IQ does not always return JSON — some tools reply with plain text blocks — so the
    /// content blocks are concatenated rather than deserialised into an assumed shape.
    /// </remarks>
    private static string Flatten(CallToolResult result)
    {
        StringBuilder builder = new();

        foreach (ContentBlock block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    builder.AppendLine(text.Text);
                    break;
                case EmbeddedResourceBlock resource:
                    builder.AppendLine(JsonSerializer.Serialize(resource.Resource));
                    break;
            }
        }

        if (result.StructuredContent is not null)
        {
            builder.AppendLine(result.StructuredContent.Value.ToString());
        }

        string output = builder.ToString().Trim();

        if (result.IsError == true)
        {
            return JsonSerializer.Serialize(new { error = output.Length == 0 ? "Tool reported an error." : output });
        }

        return output.Length == 0
            ? JsonSerializer.Serialize(new { result = "The tool returned no content." })
            : Truncate(output, 24_000);
    }

    private static string Sanitise(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int written = 0;

        foreach (char c in name)
        {
            buffer[written++] = char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_';
        }

        return new string(buffer[..written]);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "...");

    public async ValueTask DisposeAsync()
    {
        foreach (McpClient client in _clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _clients.Clear();

        foreach (HttpClient httpClient in _httpClients)
        {
            httpClient.Dispose();
        }

        _httpClients.Clear();
    }

    private sealed record ToolBinding(string Name, Func<JsonElement, CancellationToken, Task<string>> Handler);
}

/// <summary>One tool, independent of which model will call it.</summary>
public sealed record ToolSpec(string Name, string Description, string ParametersJson, bool AccessesOrganisationData);

/// <summary>One tool call, for the diagnostics endpoint. Deliberately carries no arguments or results.</summary>
public sealed record ToolInvocation(
    DateTimeOffset At,
    string Tool,
    long DurationMs,
    bool Succeeded,
    int ResponseLength);
