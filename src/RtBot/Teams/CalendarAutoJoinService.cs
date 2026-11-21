using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RtBot.Configuration;
using RtBot.Tools;

namespace RtBot.Teams;

/// <summary>
/// Makes the AI teammate behave like a colleague: invite her mailbox to a Teams meeting and she
/// turns up when it starts, with no operator call.
/// </summary>
/// <remarks>
/// Polls the agent user's own calendar (delegated <c>Calendars.ReadWrite</c> as the agent) and
/// joins each Teams meeting once, shortly before its start. Declined, cancelled and free-busy
/// "free" events are skipped, so removing her from the invite or declining keeps her out.
/// </remarks>
public sealed partial class CalendarAutoJoinService : BackgroundService
{
    private const string Scope = "https://graph.microsoft.com/Calendars.ReadWrite";

    private readonly AgentIdentityOptions _agent;
    private readonly IDelegatedTokenProvider _tokens;
    private readonly TeamsCallingService _calling;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CalendarAutoJoinService> _logger;

    // Event id + start: a rescheduled meeting is a new occurrence and should be joined again.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _joined = new(StringComparer.Ordinal);

    public CalendarAutoJoinService(
        AgentIdentityOptions agent,
        IDelegatedTokenProvider tokens,
        TeamsCallingService calling,
        IHttpClientFactory httpFactory,
        ILogger<CalendarAutoJoinService> logger)
    {
        _agent = agent;
        _tokens = tokens;
        _calling = calling;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_agent.Enabled || !_agent.AutoJoinFromCalendar || _tokens is not AgentUserTokenProvider)
        {
            return;
        }

        _logger.LogWarning("Calendar auto-join on for {Upn}, polling every {Seconds}s.", _agent.AgentUserUpn, _agent.CalendarPollSeconds);

        // Let the media platform initialise first.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Max(20, _agent.CalendarPollSeconds)));
        do
        {
            try
            {
                await PollAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Calendar auto-join poll failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        string? token = await _tokens.GetTokenAsync(Scope, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string key, DateTimeOffset end) in _joined)
        {
            if (end < now.AddHours(-1))
            {
                _joined.TryRemove(key, out _);
            }
        }

        // Window: meetings already running (up to their end) and those starting within the lead time.
        string url = "https://graph.microsoft.com/v1.0/me/calendarView" +
            $"?startDateTime={Uri.EscapeDataString(now.AddHours(-8).ToString("o"))}" +
            $"&endDateTime={Uri.EscapeDataString(now.AddMinutes(_agent.JoinMinutesBeforeStart + 1).ToString("o"))}" +
            "&$select=id,subject,start,end,isCancelled,showAs,responseStatus,isOnlineMeeting,onlineMeeting,body&$top=50";

        HttpClient http = _httpFactory.CreateClient(nameof(CalendarAutoJoinService));
        List<JsonNode?> events = [];
        string? next = url;
        for (int page = 0; next is not null && page < 10; page++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\", outlook.body-content-type=\"text\"");

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Calendar poll returned {Status}.", (int)response.StatusCode);
                return;
            }

            JsonNode? page0 = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (page0?["value"] is JsonArray items)
            {
                events.AddRange(items.Select(i => i?.DeepClone()));
            }

            next = page0?["@odata.nextLink"]?.GetValue<string>();
        }

        foreach (JsonNode? e in events)
        {
            if (e is null || e["isCancelled"]?.GetValue<bool>() == true ||
                string.Equals(e["showAs"]?.GetValue<string>(), "free", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e["responseStatus"]?["response"]?.GetValue<string>(), "declined", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryUtc(e["start"]?["dateTime"]?.GetValue<string>(), out DateTimeOffset start) ||
                !TryUtc(e["end"]?["dateTime"]?.GetValue<string>(), out DateTimeOffset end) ||
                end <= now || start > now.AddMinutes(_agent.JoinMinutesBeforeStart))
            {
                continue;
            }

            string key = $"{e["id"]?.GetValue<string>()}|{start:o}";
            if (_joined.ContainsKey(key))
            {
                continue;
            }

            string? joinUrl = FindJoinUrl(e);
            string subject = MeetingContextText(e["subject"]?.GetValue<string>());
            if (joinUrl is null)
            {
                // Remember it, or the same warning repeats every minute until the meeting ends.
                _joined[key] = end;
                _logger.LogWarning("Calendar meeting '{Subject}' has no Teams join link Aria can use; not joining.", subject);
                continue;
            }

            string? thread = ThreadOf(joinUrl);
            if (thread is null)
            {
                // A link that won't parse won't parse next minute either.
                _joined[key] = end;
                _logger.LogWarning("Calendar meeting '{Subject}' has a Teams link Aria can't read; not joining.", subject);
                continue;
            }

            if (_calling.FindByThread(thread) is not null)
            {
                _joined[key] = end;
                continue;
            }

            _joined[key] = end;
            try
            {
                await _calling.JoinMeetingAsync(joinUrl, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Auto-joined '{Subject}' from the calendar (starts {Start:HH:mm} UTC).", subject, start);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try again next poll: the most common cause is joining a moment before the meeting exists.
                _joined.TryRemove(key, out _);
                _logger.LogWarning(ex, "Auto-join of '{Subject}' failed; will retry.", subject);
            }
        }
    }

    /// <summary>The long-form join link the parser understands, from the event or its body.</summary>
    private static string? FindJoinUrl(JsonNode e)
    {
        string? online = e["onlineMeeting"]?["joinUrl"]?.GetValue<string>();
        if (online is not null && online.Contains("/meetup-join/", StringComparison.OrdinalIgnoreCase))
        {
            return online;
        }

        string body = e["body"]?["content"]?.GetValue<string>() ?? string.Empty;
        Match m = LongJoinLink().Match(body);
        if (m.Success)
        {
            return m.Value.TrimEnd('>', ')', ']', '.', ',');
        }

        // Defender Safe Links rewrites links in mail delivered to the agent's mailbox:
        // https://<region>.safelinks.protection.outlook.com/...?url=<encoded>&data=...
        foreach (Match s in SafeLink().Matches(body))
        {
            string inner = WebUtility.UrlDecode(s.Groups["u"].Value);
            if (inner.Contains("/meetup-join/", StringComparison.OrdinalIgnoreCase))
            {
                return inner;
            }
        }

        return null;
    }

    private static string? ThreadOf(string joinUrl)
    {
        try
        {
            return JoinUrlParser.Parse(joinUrl).ChatInfo.ThreadId;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
    }

    private static bool TryUtc(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out result);

    private static string MeetingContextText(string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? "(no subject)" : WebUtility.HtmlDecode(subject).Trim();

    [GeneratedRegex(@"https://teams\.microsoft\.com/l/meetup-join/[^\s""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex LongJoinLink();

    [GeneratedRegex(@"https://[a-z0-9.\-]*safelinks\.protection\.outlook\.com/[^\s""<>]*?[?&]url=(?<u>[^&\s""<>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SafeLink();
}
