using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RtBot.Chat;
using RtBot.Configuration;
using RtBot.Facilitator;
using RtBot.Teams;

namespace RtBot.Controllers;

/// <summary>
/// Bot Framework messaging endpoint: answers @mentions in a meeting chat (and direct messages).
/// </summary>
/// <remarks>
/// The answer is produced by the text model with the same meeting tools as the voice path, so
/// a typed question sees the same notes, restrictions and Work IQ access as a spoken one.
/// </remarks>
[ApiController]
[Route("api/messages")]
[Authorize(AuthenticationSchemes = BotFrameworkAuth.Scheme)]
public sealed partial class MessagesController : ControllerBase
{
    private readonly TeamsCallingService _calling;
    private readonly TextModelClient _textModel;
    private readonly BotChatClient _chat;
    private readonly RtBotOptions _options;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(
        TeamsCallingService calling,
        TextModelClient textModel,
        BotChatClient chat,
        Microsoft.Extensions.Options.IOptions<RtBotOptions> options,
        ILogger<MessagesController> logger)
    {
        _calling = calling;
        _textModel = textModel;
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost]
    public IActionResult Post([FromBody] JsonElement activity)
    {
        if (Str(activity, "type") != "message")
        {
            return Ok();
        }

        string? serviceUrl = Str(activity, "serviceUrl");
        string? conversationId = activity.TryGetProperty("conversation", out JsonElement c) ? Str(c, "id") : null;
        string? conversationType = activity.TryGetProperty("conversation", out c) ? Str(c, "conversationType") : null;
        string? activityId = Str(activity, "id");
        string sender = activity.TryGetProperty("from", out JsonElement f) ? Str(f, "name") ?? "Someone" : "Someone";
        string? tenant = activity.TryGetProperty("channelData", out JsonElement cd) && cd.TryGetProperty("tenant", out JsonElement t) ? Str(t, "id") : null;
        string text = StripMentions(Str(activity, "text") ?? string.Empty);

        if (serviceUrl is null || conversationId is null || string.IsNullOrWhiteSpace(text))
        {
            return Ok();
        }

        // Answering can take 15 s with a Work IQ call; Teams retries if the webhook is slow, so
        // acknowledge now and reply proactively.
        _ = Task.Run(() => AnswerAsync(serviceUrl, conversationId, conversationType, activityId, sender, tenant, text));
        return Ok();
    }

    private async Task AnswerAsync(
        string serviceUrl,
        string conversationId,
        string? conversationType,
        string? activityId,
        string sender,
        string? tenant,
        string question)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
            string threadId = conversationId.Split(';')[0];
            CallHandler? live = _calling.FindByThread(threadId);

            string reply;
            if (live is not null)
            {
                live.Meeting.AddTurn(new TranscriptTurn(DateTimeOffset.UtcNow, sender, $"(in chat) {question}", IsAgent: false));

                reply = await _textModel.RunWithToolsAsync(
                    _options.Facilitator.ScribeModel,
                    ChatSystemPrompt(live.Meeting),
                    $"{sender} asks in the meeting chat: {question}",
                    live.Tools.TextTools(),
                    live.Tools.InvokeAsync,
                    timeout.Token).ConfigureAwait(false);
            }
            else if (conversationType == "personal" && string.Equals(tenant, _options.AzureAd.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                reply = "I answer in meetings I've joined. Add me to a Teams meeting or call and ask me there, by voice or by @mentioning me in the chat.";
            }
            else
            {
                reply = "I'm not in this meeting's call right now. Add me to the call and I'll pick it up.";
            }

            await _chat.ReplyAsync(serviceUrl, conversationId, activityId, reply, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Answering a chat message in {Conversation} failed.", conversationId);
        }
    }

    private string ChatSystemPrompt(MeetingContext meeting) =>
        $"""
         You are {_options.Voice.AgentName}, the AI facilitator in a Microsoft Teams meeting, replying in the meeting chat.
         Reply in concise Teams markdown. Use tools for anything about organisation data; never invent facts.
         Use record_action_item / record_decision when the message assigns work or states an agreement.
         {(meeting.IsRestricted ? "Someone external is present: do not reveal internal information." : string.Empty)}

         Current meeting state:
         {meeting.RenderBriefing()}
         """;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string StripMentions(string text) =>
        Whitespace().Replace(Tags().Replace(MentionTags().Replace(text, string.Empty), string.Empty), " ").Trim();

    [GeneratedRegex("<at>.*?</at>", RegexOptions.IgnoreCase)]
    private static partial Regex MentionTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public static class BotFrameworkAuth
{
    public const string Scheme = "BotFramework";
}
