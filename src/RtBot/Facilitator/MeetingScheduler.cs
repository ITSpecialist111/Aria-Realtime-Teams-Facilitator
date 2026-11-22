using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RtBot.Configuration;
using RtBot.Tools;

namespace RtBot.Facilitator;

/// <summary>Books a real follow-up Teams meeting and sends calendar invites from the agent's mailbox.</summary>
public sealed partial class MeetingScheduler
{
    private const string CalendarScope = "https://graph.microsoft.com/Calendars.ReadWrite";
    private const string OnlineMeetingScope = "https://graph.microsoft.com/OnlineMeetings.ReadWrite";
    private const string UserScope = "https://graph.microsoft.com/User.ReadBasic.All";

    private readonly IDelegatedTokenProvider _tokens;
    private readonly DelegatedTokenProvider _serviceAccountTokens;
    private readonly AgentIdentityOptions _agentIdentity;
    private readonly VoiceOptions _voice;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MeetingScheduler> _logger;

    public MeetingScheduler(
        IDelegatedTokenProvider tokens,
        DelegatedTokenProvider serviceAccountTokens,
        AgentIdentityOptions agentIdentity,
        VoiceOptions voice,
        IHttpClientFactory httpFactory,
        ILogger<MeetingScheduler> logger)
    {
        _tokens = tokens;
        _serviceAccountTokens = serviceAccountTokens;
        _agentIdentity = agentIdentity;
        _voice = voice;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<ScheduledFollowUp> ScheduleAsync(
        MeetingContext meeting,
        string? subject,
        double? startInMinutes,
        string? startAtIso,
        double durationMinutes,
        IReadOnlyList<string> additionalAttendees,
        IReadOnlyList<string> agendaItems,
        CancellationToken cancellationToken)
    {
        string? calToken = await _tokens.GetTokenAsync(CalendarScope, cancellationToken).ConfigureAwait(false);
        string? userToken = await _tokens.GetTokenAsync(UserScope, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(calToken))
        {
            return new ScheduledFollowUp(false, $"Calendar access is not configured for {_voice.AgentName}.", null, null, []);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset startUtc;
        if (!string.IsNullOrWhiteSpace(startAtIso))
        {
            if (!DateTimeOffset.TryParse(startAtIso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                return new ScheduledFollowUp(false, $"Could not parse start_at_utc '{startAtIso}'. Pass an ISO-8601 UTC timestamp or use start_in_minutes.", null, null, []);
            }

            DateTimeOffset utc = parsed.ToUniversalTime();
            if (utc <= now.AddMinutes(-2) || utc > now.AddDays(90))
            {
                if (startInMinutes is { } fallbackMins)
                {
                    startUtc = now.AddMinutes(Math.Clamp(fallbackMins, 2, 10_080));
                }
                else
                {
                    return new ScheduledFollowUp(false, $"Requested start time {utc:yyyy-MM-dd HH:mm} UTC must be between now and 90 days ahead.", null, null, []);
                }
            }
            else
            {
                startUtc = utc;
            }
        }
        else
        {
            startUtc = now.AddMinutes(Math.Clamp(startInMinutes ?? 60, 2, 10_080));
        }

        double mins = Math.Clamp(durationMinutes > 0 ? durationMinutes : 15, 5, 240);
        DateTimeOffset endUtc = startUtc.AddMinutes(mins);
        string title = !string.IsNullOrWhiteSpace(subject)
            ? MeetingContext.OneLine(subject, 120)
            : $"{MeetingContext.OneLine(meeting.Subject ?? "Follow-up", 90)} - Checkpoint";

        HttpClient http = _httpFactory.CreateClient(nameof(MeetingScheduler));
        Dictionary<string, string> recipients = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(userToken))
        {
            IEnumerable<string> internalIds = meeting.Attendees
                .Where(a => !a.IsExternal && !string.IsNullOrWhiteSpace(a.UserId))
                .Select(a => a.UserId!)
                .Append(meeting.OrganizerUserId)
                .OfType<string>()
                .Where(id => !_agentIdentity.Enabled || !string.Equals(id, _agentIdentity.AgentUserId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40);

            foreach (string id in internalIds)
            {
                (string Name, string Address)? resolved = await ResolveUserAsync(http, userToken, id, cancellationToken).ConfigureAwait(false);
                if (resolved is { } r)
                {
                    recipients[r.Address] = r.Name;
                }
            }
        }

        foreach (string raw in additionalAttendees.Take(20))
        {
            if (TryParseSpokenOrFormattedEmail(raw, out MailAddress? addr) && addr is not null)
            {
                if (userToken is not null &&
                    await ResolveInternalMemberByEmailAsync(http, userToken, addr.Address, cancellationToken).ConfigureAwait(false) is { } verified)
                {
                    recipients.TryAdd(verified.Address, verified.Name);
                }
                else
                {
                    _logger.LogWarning("Skipped unverified or external follow-up recipient {Address}.", addr.Address);
                }
            }
        }

        if (recipients.Count == 0)
        {
            return new ScheduledFollowUp(false, "No valid attendee email addresses could be resolved for the invite.", null, null, []);
        }

        string? joinUrl = await CreateOnlineMeetingAsync(http, title, startUtc, endUtc, cancellationToken).ConfigureAwait(false);
        string htmlBody = BuildInviteHtml(meeting, _voice.AgentName, agendaItems, joinUrl);

        JsonObject ev = new()
        {
            ["subject"] = title,
            ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = htmlBody },
            ["start"] = new JsonObject { ["dateTime"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = endUtc.ToString("yyyy-MM-ddTHH:mm:ss"), ["timeZone"] = "UTC" },
            ["attendees"] = new JsonArray([.. recipients.Select(kv => (JsonNode)new JsonObject
            {
                ["emailAddress"] = new JsonObject { ["address"] = kv.Key, ["name"] = kv.Value },
                ["type"] = "required",
            })]),
        };

        using HttpRequestMessage req = new(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/events")
        {
            Content = JsonContent.Create(ev),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", calToken);

        using HttpResponseMessage resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Follow-up calendar invite failed: {Status} {Body}", (int)resp.StatusCode, err.Length > 300 ? err[..300] : err);
            return new ScheduledFollowUp(false, $"Calendar service returned {(int)resp.StatusCode}.", null, null, []);
        }

        _logger.LogWarning(
            "Scheduled follow-up '{Title}' at {Start:HH:mm} UTC ({Mins}m) for {Count} attendees; hasTeamsLink={HasLink}.",
            title,
            startUtc,
            (int)mins,
            recipients.Count,
            joinUrl is not null);

        string linkNote = joinUrl is null ? " (calendar invite sent without a Teams link)" : string.Empty;
        string summary = $"Booked '{title}' for {startUtc:ddd d MMM HH:mm} UTC ({(int)mins} min) with {recipients.Count} invitee{(recipients.Count == 1 ? string.Empty : "s")}{linkNote}.";
        return new ScheduledFollowUp(true, summary, joinUrl, startUtc, [.. recipients.Keys]);
    }

    private async Task<string?> CreateOnlineMeetingAsync(
        HttpClient http,
        string subject,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        string? primaryToken = await _tokens.GetTokenAsync(OnlineMeetingScope, cancellationToken).ConfigureAwait(false);
        string? join = await TryPostOnlineMeetingAsync(http, primaryToken, subject, startUtc, endUtc, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(join))
        {
            return join;
        }

        // Only hit the service-account fallback if primary minting failed and fallback is allowed.
        if (!_agentIdentity.Enabled || _agentIdentity.FallbackToServiceAccount)
        {
            string? fallbackToken = await _serviceAccountTokens.GetTokenAsync(OnlineMeetingScope, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fallbackToken) && !string.Equals(fallbackToken, primaryToken, StringComparison.Ordinal))
            {
                return await TryPostOnlineMeetingAsync(http, fallbackToken, subject, startUtc, endUtc, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<string?> TryPostOnlineMeetingAsync(
        HttpClient http,
        string? token,
        string subject,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        JsonObject payload = new()
        {
            ["subject"] = subject,
            ["startDateTime"] = startUtc.ToString("o"),
            ["endDateTime"] = endUtc.ToString("o"),
            ["allowedPresenters"] = "organization",
            ["lobbyBypassSettings"] = new JsonObject { ["scope"] = "organization", ["isDialInBypassEnabled"] = false },
        };

        using HttpRequestMessage req = new(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/onlineMeetings")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogDebug("POST /me/onlineMeetings returned {Status}.", (int)resp.StatusCode);
            return null;
        }

        JsonNode? node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return node?["joinWebUrl"]?.GetValue<string>();
    }

    private async Task<(string Name, string Address)?> ResolveInternalMemberByEmailAsync(
        HttpClient http,
        string token,
        string email,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage req = new(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(email)}?$select=id,displayName,mail,userPrincipalName,userType");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        JsonNode? user = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        string? id = user?["id"]?.GetValue<string>();
        string? userType = user?["userType"]?.GetValue<string>();
        if (!string.Equals(userType, "Member", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_agentIdentity.Enabled && !string.IsNullOrWhiteSpace(_agentIdentity.AgentUserId) &&
            string.Equals(id, _agentIdentity.AgentUserId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? mail = user?["mail"]?.GetValue<string>() ?? user?["userPrincipalName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(mail) || !MailAddress.TryCreate(mail, out MailAddress? parsedMail) || !IsValidSmtpAddress(parsedMail))
        {
            return null;
        }

        string name = user?["displayName"]?.GetValue<string>() ?? parsedMail.Address;
        return (name, parsedMail.Address);
    }

    private static async Task<(string Name, string Address)?> ResolveUserAsync(
        HttpClient http,
        string token,
        string userId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage req = new(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}?$select=displayName,mail");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage resp = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        JsonNode? u = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        string? mail = u?["mail"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(mail)
            ? null
            : (u?["displayName"]?.GetValue<string>() ?? mail, mail);
    }

    private static string BuildInviteHtml(MeetingContext meeting, string agentName, IReadOnlyList<string> agendaItems, string? joinUrl)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        MeetingNotes notes = meeting.SnapshotNotes();
        StringBuilder sb = new();
        sb.Append("<p>Follow-up checkpoint scheduled by ")
            .Append(E(agentName))
            .Append(" during <b>")
            .Append(E(meeting.Subject ?? "Teams meeting"))
            .Append("</b>.</p>");

        List<string> items = agendaItems.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => MeetingContext.OneLine(a, 120)).ToList();
        if (items.Count == 0 && notes.Actions.Count > 0)
        {
            items.AddRange(notes.Actions.TakeLast(4).Select(a => $"Check action ({a.Owner}): {a.Task}"));
        }

        if (items.Count > 0)
        {
            sb.Append("<p><b>Agenda</b></p><ol>");
            foreach (string item in items.Take(8))
            {
                sb.Append("<li>").Append(E(item)).Append(" (5 min)</li>");
            }

            sb.Append("</ol>");
        }

        if (!string.IsNullOrWhiteSpace(joinUrl))
        {
            sb.Append("<p>________________________________________________________________________________</p>");
            sb.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(joinUrl)).Append("\">Join the meeting now</a></p>");
        }

        return sb.ToString();
    }

    /// <summary>Parses formatted or spoken addresses like "alex at contoso dot com" while rejecting dotless hosts.</summary>
    public static bool TryParseSpokenOrFormattedEmail(string raw, out MailAddress? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim().Trim(',', ';');
        if (trimmed.Contains('@') && MailAddress.TryCreate(trimmed, out MailAddress? direct) && IsValidSmtpAddress(direct))
        {
            result = direct;
            return true;
        }

        string s = trimmed.Trim('<', '>', '.', '"', '\'');
        if (!s.Contains('@'))
        {
            s = SpokenAt().Replace(s, "@", 1);
        }

        if (!s.Contains('@'))
        {
            return false;
        }

        s = SpokenDot().Replace(s, ".");
        s = SpaceAroundSeparator().Replace(s, "$1");
        if (s.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (MailAddress.TryCreate(s, out MailAddress? parsed) && IsValidSmtpAddress(parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    private static bool IsValidSmtpAddress(MailAddress addr) =>
        !addr.Address.Any(char.IsWhiteSpace) &&
        addr.Host.Contains('.') &&
        !addr.Host.StartsWith('.') &&
        !addr.Host.EndsWith('.') &&
        !addr.Host.Contains("..", StringComparison.Ordinal);

    [GeneratedRegex(@"\s+at\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpokenAt();

    [GeneratedRegex(@"\s+dot\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpokenDot();

    [GeneratedRegex(@"\s*([@.])\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceAroundSeparator();
}

public sealed record ScheduledFollowUp(
    bool Succeeded,
    string Summary,
    string? JoinUrl,
    DateTimeOffset? StartUtc,
    IReadOnlyList<string> Attendees);
