using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RtBot.Tools;

namespace RtBot.Facilitator;

/// <summary>
/// Reads the agenda from the meeting invite in the agent account's own calendar, so the agent
/// arrives knowing the topics and timeboxes instead of waiting to be told.
/// </summary>
/// <remarks>
/// Works when the agent account organised or was invited to the meeting. Uses delegated
/// <c>Calendars.Read</c>, so it never sees a calendar the account couldn't open itself.
/// </remarks>
public sealed partial class MeetingAgendaLoader
{
    private const string Scope = "https://graph.microsoft.com/Calendars.Read";

    private const string ExtractPrompt = """
        You read a Teams meeting invite and extract its agenda. Return JSON:
        {"agenda":[{"title":"short item title","minutes":number or null}]}

        Rules:
        - Only items the invite actually lists as agenda topics, in order. Keep titles under 8 words.
        - minutes only when the invite states a duration or time range for that item; otherwise null.
        - If the invite has no agenda, return {"agenda":[]}. Never invent items from the subject alone.
        """;

    private readonly IDelegatedTokenProvider _tokens;
    private readonly TextModelClient _model;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MeetingAgendaLoader> _logger;

    public MeetingAgendaLoader(
        IDelegatedTokenProvider tokens,
        TextModelClient model,
        IHttpClientFactory httpFactory,
        ILogger<MeetingAgendaLoader> logger)
    {
        _tokens = tokens;
        _model = model;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>The invite's agenda, or an empty list when there is no invite or no agenda in it.</summary>
    public async Task<IReadOnlyList<AgendaItem>> LoadAsync(
        string threadId,
        string? joinUrl,
        string modelDeployment,
        CancellationToken cancellationToken)
    {
        try
        {
            string? token = await _tokens.GetTokenAsync(Scope, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return [];
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string url = "https://graph.microsoft.com/v1.0/me/calendarView" +
                $"?startDateTime={Uri.EscapeDataString(now.AddHours(-12).ToString("o"))}" +
                $"&endDateTime={Uri.EscapeDataString(now.AddHours(12).ToString("o"))}" +
                "&$select=subject,body,onlineMeeting&$top=100";

            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");

            using HttpResponseMessage response = await _httpFactory.CreateClient(nameof(MeetingAgendaLoader))
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Calendar lookup for the agenda failed: {Status}", (int)response.StatusCode);
                return [];
            }

            string decodedThread = WebUtility.UrlDecode(threadId);

            // Match on the event's Teams link, or the link pasted in the body when the calendar
            // has no Teams provider (third-party schedulers, forwarded invites).
            JsonNode? invite = (JsonNode.Parse(json)?["value"] as JsonArray)?.FirstOrDefault(e =>
            {
                string join = WebUtility.UrlDecode(e?["onlineMeeting"]?["joinUrl"]?.GetValue<string>() ?? string.Empty);
                string text = WebUtility.UrlDecode(e?["body"]?["content"]?.GetValue<string>() ?? string.Empty);
                return (join.Length > 0 &&
                        (join.Contains(decodedThread, StringComparison.OrdinalIgnoreCase) ||
                         (joinUrl is not null && join.Equals(WebUtility.UrlDecode(joinUrl), StringComparison.OrdinalIgnoreCase))))
                       || text.Contains(decodedThread, StringComparison.OrdinalIgnoreCase);
            });

            string body = invite?["body"]?["content"]?.GetValue<string>() ?? string.Empty;

            // Teams appends its own join boilerplate after a line of underscores.
            int boiler = body.IndexOf("________________", StringComparison.Ordinal);
            if (boiler >= 0)
            {
                body = body[..boiler];
            }

            body = Whitespace().Replace(body, " ").Trim();
            if (body.Length < 10)
            {
                return [];
            }

            JsonNode? result = await _model.CompleteJsonAsync(
                modelDeployment,
                ExtractPrompt,
                $"SUBJECT: {invite?["subject"]?.GetValue<string>()}\nINVITE TEXT:\n{(body.Length > 6000 ? body[..6000] : body)}",
                cancellationToken).ConfigureAwait(false);

            List<AgendaItem> items = [];
            if (result?["agenda"] is JsonArray array)
            {
                foreach (JsonNode? node in array)
                {
                    string? title = node?["title"]?.GetValue<string>()?.Trim();
                    double? minutes = node?["minutes"] is JsonValue v && v.TryGetValue(out double m) && m > 0 ? m : null;
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        items.Add(new AgendaItem(title, minutes));
                    }
                }
            }

            _logger.LogWarning("Agenda from invite: {Count} items.", items.Count);
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the agenda from the invite.");
            return [];
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
