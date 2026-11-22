using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using RtBot.Tools;

namespace RtBot.Facilitator;

/// <summary>
/// Emails the end-of-meeting report to the attendees, sent from the delegated service account.
/// </summary>
/// <remarks>
/// Only attendees from the agent's own tenant are emailed: the report can contain internal
/// information, and external attendees cannot be resolved to an address in this directory anyway.
/// </remarks>
public sealed class MeetingMailer
{
    private const string MailScope = "https://graph.microsoft.com/Mail.Send";
    private const string UserScope = "https://graph.microsoft.com/User.ReadBasic.All";
    private const int MaxRecipients = 100;

    private readonly IDelegatedTokenProvider _tokens;
    private readonly RtBot.Configuration.AgentIdentityOptions _agentIdentity;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MeetingMailer> _logger;

    public MeetingMailer(
        IDelegatedTokenProvider tokens,
        RtBot.Configuration.AgentIdentityOptions agentIdentity,
        IHttpClientFactory httpFactory,
        ILogger<MeetingMailer> logger)
    {
        _tokens = tokens;
        _agentIdentity = agentIdentity;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<int> SendAsync(MeetingContext meeting, MeetingReport report, string agentName, CancellationToken cancellationToken)
    {
        IEnumerable<string> ids = meeting.Attendees
            .Where(a => !a.IsExternal && !string.IsNullOrWhiteSpace(a.UserId))
            .Select(a => a.UserId!)
            .Append(meeting.OrganizerUserId)
            .OfType<string>()
            .Where(id => !_agentIdentity.Enabled || !string.Equals(id, _agentIdentity.AgentUserId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecipients);

        string? userToken = await _tokens.GetTokenAsync(UserScope, cancellationToken).ConfigureAwait(false);
        string? mailToken = await _tokens.GetTokenAsync(MailScope, cancellationToken).ConfigureAwait(false);
        if (userToken is null || mailToken is null)
        {
            _logger.LogWarning("No delegated Graph token for mail; recap email not sent.");
            return 0;
        }

        HttpClient http = _httpFactory.CreateClient(nameof(MeetingMailer));
        List<(string Name, string Address)> recipients = [];
        foreach (string id in ids)
        {
            (string Name, string Address)? r = await ResolveAsync(http, userToken, id, cancellationToken).ConfigureAwait(false);
            if (r is not null)
            {
                recipients.Add(r.Value);
            }
        }

        if (recipients.Count == 0)
        {
            _logger.LogWarning("No internal attendees could be resolved to an email address; recap email not sent.");
            return 0;
        }

        // Graph caps a sendMail request at about 4 MB, and base64 adds a third. Images beyond the budget
        // are described in text only.
        const int imageBudgetBytes = 2_500_000;
        int used = 0;
        List<int> inlined = [];
        IReadOnlyList<VisualNote> allVisuals = meeting.Visuals;
        for (int i = 0; i < allVisuals.Count; i++)
        {
            if (used + allVisuals[i].Jpeg.Length <= imageBudgetBytes)
            {
                used += allVisuals[i].Jpeg.Length;
                inlined.Add(i);
            }
        }

        string subject = $"Meeting recap: {meeting.Subject ?? $"Teams meeting {meeting.JoinedAt.ToLocalTime():d MMM HH:mm}"}";
        JsonObject message = new()
        {
            ["message"] = new JsonObject
            {
                ["subject"] = subject,
                ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = RenderHtml(meeting, report, agentName, inlined) },
                ["toRecipients"] = new JsonArray([.. recipients.Select(r => (JsonNode)new JsonObject
                {
                    ["emailAddress"] = new JsonObject { ["name"] = r.Name, ["address"] = r.Address },
                })]),
                ["attachments"] = new JsonArray([.. inlined.Select(i => (JsonNode)new JsonObject
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = $"capture-{i + 1}.jpg",
                    ["contentType"] = "image/jpeg",
                    ["contentId"] = $"capture{i + 1}",
                    ["isInline"] = true,
                    ["contentBytes"] = Convert.ToBase64String(allVisuals[i].Jpeg),
                })]),
            },
            // Keeping AI recaps out of Aria's own Sent Items prevents Work IQ indexing her own
            // unverified meeting summaries over authoritative playbooks and policies.
            ["saveToSentItems"] = false,
        };

        using HttpRequestMessage request = new(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail")
        {
            Content = JsonContent.Create(message),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mailToken);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Recap email failed: {Status} {Body}", (int)response.StatusCode, body);
            return 0;
        }

        _logger.LogInformation("Recap emailed to {Count} attendees.", recipients.Count);
        return recipients.Count;
    }

    private async Task<(string Name, string Address)?> ResolveAsync(HttpClient http, string token, string userId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(userId)}?$select=displayName,mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Could not resolve attendee {UserId}: {Status}", userId, (int)response.StatusCode);
            return null;
        }

        JsonNode? user = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        string? address = user?["mail"]?.GetValue<string>();

        // Accounts without a mailbox (resource accounts, unlicensed users) have no mail attribute.
        return string.IsNullOrWhiteSpace(address)
            ? null
            : (user?["displayName"]?.GetValue<string>() ?? address, address);
    }

    private static string RenderHtml(MeetingContext meeting, MeetingReport report, string agentName, IReadOnlyList<int> inlinedImages)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

        StringBuilder h = new();
        h.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#242424;max-width:720px\">");
        h.Append("<h2 style=\"margin:0 0 4px\">").Append(E(meeting.Subject ?? "Meeting recap")).Append("</h2>");
        h.Append("<div style=\"color:#616161;margin-bottom:16px\">")
            .Append(E(meeting.JoinedAt.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm")))
            .Append(" &middot; ").Append((int)meeting.Elapsed.TotalMinutes).Append(" minutes &middot; ")
            .Append(E(string.Join(", ", meeting.Attendees.Select(a => a.IsExternal ? $"{a.Name} (external)" : a.Name))))
            .Append("</div>");

        Section(h, "Summary", report.Summary);
        List(h, "Key insights", report.Insights);
        List(h, "Decisions", report.Decisions);

        if (report.Actions.Count > 0)
        {
            h.Append("<h3 style=\"margin:20px 0 6px\">Actions</h3><table style=\"border-collapse:collapse;width:100%\">");
            h.Append("<tr style=\"background:#f3f2f1;text-align:left\"><th style=\"padding:6px\">Owner</th><th style=\"padding:6px\">Action</th><th style=\"padding:6px\">Due</th></tr>");
            foreach (ActionItem a in report.Actions)
            {
                h.Append("<tr style=\"border-top:1px solid #e1dfdd\"><td style=\"padding:6px;white-space:nowrap\"><b>").Append(E(a.Owner))
                    .Append("</b></td><td style=\"padding:6px\">").Append(E(a.Task))
                    .Append("</td><td style=\"padding:6px;white-space:nowrap\">").Append(E(a.Due ?? "-")).Append("</td></tr>");
            }

            h.Append("</table>");
        }

        List(h, "Open questions", report.OpenQuestions);
        List(h, "Parking lot", meeting.ParkingLot);
        foreach ((string category, IReadOnlyList<string> items) in meeting.Tracked)
        {
            List(h, category, items);
        }

        List(h, "Not covered this time", meeting.UncoveredChecklist);
        List(h, "Surfaced live during the meeting", [.. meeting.Insights.Select(i =>
            $"{i.Title}: {i.Text}{(i.Citation is null ? string.Empty : $" ({i.Citation})")}")]);

        IReadOnlyList<VisualNote> visuals = meeting.Visuals;
        if (visuals.Count > 0)
        {
            h.Append("<h3 style=\"margin:20px 0 6px\">Captured in the meeting</h3>");
            for (int i = 0; i < visuals.Count; i++)
            {
                VisualNote v = visuals[i];
                h.Append("<div style=\"margin:0 0 18px\"><b>").Append(E(v.Title)).Append("</b> <span style=\"color:#616161\">(")
                    .Append(E(v.Source)).Append(v.CapturedFrom is null ? string.Empty : ", " + E(v.CapturedFrom)).Append(", ")
                    .Append(E(v.At.ToLocalTime().ToString("HH:mm"))).Append(")</span><br>");
                if (inlinedImages.Contains(i))
                {
                    h.Append("<img src=\"cid:capture").Append(i + 1).Append("\" alt=\"").Append(E(v.Title))
                        .Append("\" style=\"max-width:560px;width:100%;border:1px solid #e1dfdd;border-radius:6px;margin:6px 0\"><br>");
                }

                h.Append(E(v.Summary));
                if (v.KeyPoints.Count > 0)
                {
                    h.Append("<ul style=\"margin:4px 0 0;padding-left:20px\">");
                    foreach (string p in v.KeyPoints)
                    {
                        h.Append("<li>").Append(E(p)).Append("</li>");
                    }

                    h.Append("</ul>");
                }

                h.Append("</div>");
            }
        }
        Section(h, "Next steps", report.NextSteps);

        if (report.TalkTime.Count > 1)
        {
            double total = report.TalkTime.Values.Sum(t => t.TotalSeconds);
            if (total > 0)
            {
                List(h, "Share of speaking time", [.. report.TalkTime.Select(kv => $"{kv.Key}: {kv.Value.TotalSeconds / total:P0}")]);
            }
        }

        h.Append("<p style=\"color:#616161;font-size:12px;margin-top:24px\">Prepared by ").Append(E(agentName))
            .Append(", the AI meeting facilitator, from the meeting conversation. AI-generated: check before acting on it.</p></div>");
        return h.ToString();

        void Section(StringBuilder sb, string title, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append("<h3 style=\"margin:20px 0 6px\">").Append(E(title)).Append("</h3><p style=\"margin:0\">").Append(E(text)).Append("</p>");
            }
        }

        void List(StringBuilder sb, string title, IReadOnlyList<string> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            sb.Append("<h3 style=\"margin:20px 0 6px\">").Append(E(title)).Append("</h3><ul style=\"margin:0;padding-left:20px\">");
            foreach (string item in items)
            {
                sb.Append("<li>").Append(E(item)).Append("</li>");
            }

            sb.Append("</ul>");
        }
    }
}
