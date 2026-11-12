using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Graph.Contracts;
using Microsoft.Graph.Models;

namespace RtBot.Teams;

/// <summary>Turns a Teams meeting join URL into the chat and organizer identifiers Graph needs.</summary>
/// <remarks>
/// The parser in the published samples predates <c>@thread.v2</c> links, Safe Links wrapping and
/// the launcher redirect, all of which are routine in a real tenant, so this rebuilds the parse
/// around the <c>/meetup-join/</c> marker rather than a positional regex.
/// </remarks>
public static class JoinUrlParser
{
    public static (ChatInfo ChatInfo, MeetingInfo MeetingInfo, string TenantId) Parse(string joinUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(joinUrl);

        string url = Unwrap(joinUrl.Trim());

        int marker = url.IndexOf("/meetup-join/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            throw new ArgumentException(
                "Not a Teams meeting join URL: no '/meetup-join/' segment found.",
                nameof(joinUrl));
        }

        string remainder = url[(marker + "/meetup-join/".Length)..];

        int queryStart = remainder.IndexOf('?');
        string path = queryStart < 0 ? remainder : remainder[..queryStart];
        string query = queryStart < 0 ? string.Empty : remainder[(queryStart + 1)..];

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("Teams join URL has no thread id.", nameof(joinUrl));
        }

        string threadId = HttpUtility.UrlDecode(segments[0]);
        string messageId = segments.Length > 1 ? HttpUtility.UrlDecode(segments[1]) : "0";

        string? contextJson = HttpUtility.ParseQueryString(query)["context"];
        if (string.IsNullOrWhiteSpace(contextJson))
        {
            throw new ArgumentException(
                "Teams join URL is missing the 'context' parameter that carries the organizer and tenant.",
                nameof(joinUrl));
        }

        MeetingContext context = JsonSerializer.Deserialize<MeetingContext>(contextJson)
            ?? throw new ArgumentException("Teams join URL context could not be parsed.", nameof(joinUrl));

        if (string.IsNullOrWhiteSpace(context.Oid) || string.IsNullOrWhiteSpace(context.Tid))
        {
            throw new ArgumentException(
                "Teams join URL context is missing the organizer (Oid) or tenant (Tid).",
                nameof(joinUrl));
        }

        ChatInfo chatInfo = new()
        {
            ThreadId = threadId,
            MessageId = messageId,
            ReplyChainMessageId = context.MessageId,
        };

        Identity organizer = new() { Id = context.Oid };
        organizer.SetTenantId(context.Tid);

        OrganizerMeetingInfo meetingInfo = new()
        {
            Organizer = new IdentitySet { User = organizer },
        };

        return (chatInfo, meetingInfo, context.Tid);
    }

    /// <summary>Unwraps Safe Links and the Teams launcher redirect, which both nest the real URL.</summary>
    private static string Unwrap(string url)
    {
        for (int i = 0; i < 3; i++)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                break;
            }

            string? inner = HttpUtility.ParseQueryString(uri.Query)["url"];
            if (string.IsNullOrWhiteSpace(inner))
            {
                break;
            }

            url = HttpUtility.UrlDecode(inner);
        }

        return HttpUtility.UrlDecode(url);
    }

    private sealed class MeetingContext
    {
        [JsonPropertyName("Tid")]
        public string? Tid { get; set; }

        [JsonPropertyName("Oid")]
        public string? Oid { get; set; }

        [JsonPropertyName("MessageId")]
        public string? MessageId { get; set; }
    }
}
