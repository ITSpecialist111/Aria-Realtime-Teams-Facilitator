using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace RtBot.Facilitator;

/// <summary>A tool the text model may call: name, description and JSON-schema parameters.</summary>
public sealed record TextTool(string Name, string Description, JsonNode Parameters);

/// <summary>
/// Minimal client for the AI Services <c>/openai/v1/chat/completions</c> endpoint.
/// </summary>
/// <remarks>
/// Used for everything the realtime voice model is wrong for: maintaining structured notes,
/// answering in the meeting chat, and looking at images of the shared screen. Plain HTTP keeps it
/// on the same managed identity as Voice Live without another SDK's auth model to reconcile.
/// </remarks>
public sealed class TextModelClient
{
    private const string Scope = "https://cognitiveservices.azure.com/.default";
    private const int MaxToolRounds = 6;

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly Uri _endpoint;
    private readonly ILogger<TextModelClient> _logger;

    private AccessToken _token;

    public TextModelClient(
        HttpClient http,
        TokenCredential credential,
        Uri resourceEndpoint,
        ILogger<TextModelClient> logger)
    {
        _http = http;
        _credential = credential;
        _endpoint = new Uri(resourceEndpoint, "openai/v1/chat/completions");
        _logger = logger;
    }

    /// <summary>Single completion that must return a JSON object.</summary>
    public async Task<JsonNode?> CompleteJsonAsync(
        string model,
        string system,
        string user,
        CancellationToken cancellationToken)
    {
        JsonObject body = new()
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            },
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
        };

        JsonNode? response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
        string? content = response?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Scribe returned malformed JSON.");
            return null;
        }
    }

    /// <summary>Describes or answers a question about an image.</summary>
    public async Task<string> DescribeImageAsync(
        string model,
        string question,
        byte[] jpeg,
        CancellationToken cancellationToken)
    {
        string dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);

        JsonObject body = new()
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "You are looking at a screen shared in a business meeting. Be concrete and brief: " +
                                  "your answer will be read aloud. Quote figures and headings exactly. If text is too " +
                                  "small to read, say so rather than guessing.",
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = question },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl, ["detail"] = "high" },
                        },
                    },
                },
            },
        };

        JsonNode? response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
        return response?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "I couldn't make out the screen.";
    }

    /// <summary>
    /// Runs a tool-calling conversation to completion and returns the final text answer.
    /// </summary>
    public async Task<string> RunWithToolsAsync(
        string model,
        string system,
        string user,
        IReadOnlyList<TextTool> tools,
        Func<string, string, CancellationToken, Task<string>> invokeTool,
        CancellationToken cancellationToken)
    {
        JsonArray messages =
        [
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = user },
        ];

        JsonArray toolDefs = [];
        foreach (TextTool tool in tools)
        {
            toolDefs.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.Parameters.DeepClone(),
                },
            });
        }

        for (int round = 0; round < MaxToolRounds; round++)
        {
            JsonObject body = new()
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
            };

            if (toolDefs.Count > 0)
            {
                body["tools"] = toolDefs.DeepClone();
            }

            JsonNode? response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
            JsonNode? message = response?["choices"]?[0]?["message"];
            if (message is null)
            {
                return "Sorry, I couldn't produce an answer.";
            }

            JsonArray? calls = message["tool_calls"] as JsonArray;
            if (calls is null || calls.Count == 0)
            {
                return message["content"]?.GetValue<string>() ?? string.Empty;
            }

            messages.Add(message.DeepClone());

            foreach (JsonNode? call in calls)
            {
                string id = call?["id"]?.GetValue<string>() ?? string.Empty;
                string name = call?["function"]?["name"]?.GetValue<string>() ?? string.Empty;
                string args = call?["function"]?["arguments"]?.GetValue<string>() ?? "{}";

                string output;
                try
                {
                    output = await invokeTool(name, args, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    output = JsonSerializer.Serialize(new { error = ex.Message });
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = id,
                    ["content"] = output,
                });
            }
        }

        return "That needed more steps than I allow for a single answer. Could you narrow the question?";
    }

    private async Task<JsonNode?> PostAsync(JsonObject body, CancellationToken cancellationToken)
    {
        if (_token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
        {
            _token = await _credential
                .GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken)
                .ConfigureAwait(false);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Token);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Text model call failed: {Status} {Body}", (int)response.StatusCode, Truncate(text, 400));
            return null;
        }

        return JsonNode.Parse(text);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "...");
}
