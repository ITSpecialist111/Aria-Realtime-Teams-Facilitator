namespace RtBot.Configuration;

/// <summary>Root configuration for the bot. Bound from the "RtBot" configuration section.</summary>
public sealed class RtBotOptions
{
    public const string SectionName = "RtBot";

    public AzureAdOptions AzureAd { get; set; } = new();
    public MediaOptions Media { get; set; } = new();
    public VoiceOptions Voice { get; set; } = new();
    public ToolsOptions Tools { get; set; } = new();
    public FacilitatorOptions Facilitator { get; set; } = new();
    public ObservabilityOptions Observability { get; set; } = new();
}

public sealed class AzureAdOptions
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The calling bot's Entra application (client) id. Must match the Azure Bot resource's app id.</summary>
    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// DPAPI-protected file holding the client secret, used when <see cref="AppSecret"/> is empty.
    /// </summary>
    public string AppSecretFile { get; set; } = @"C:\rtbot\state\client.secret";

    /// <summary>Graph endpoint that outbound call creation is POSTed to.</summary>
    public string PlaceCallEndpointUrl { get; set; } = "https://graph.microsoft.com/v1.0";
}

public sealed class MediaOptions
{
    /// <summary>
    /// Public DNS name that resolves to this instance. Must exactly match the CN/SAN of
    /// <see cref="CertificateThumbprint"/> — the media platform validates it during MTLS.
    /// </summary>
    public string ServiceFqdn { get; set; } = string.Empty;

    /// <summary>Thumbprint of a public-CA certificate in LocalMachine\My. Self-signed is rejected.</summary>
    public string CertificateThumbprint { get; set; } = string.Empty;

    /// <summary>Loopback port the signalling listener binds to.</summary>
    public int SignalingInternalPort { get; set; } = 9441;

    /// <summary>Public port Teams reaches the signalling endpoint on.</summary>
    public int SignalingPublicPort { get; set; } = 443;

    /// <summary>Loopback port the native media stack binds to.</summary>
    public int MediaInternalPort { get; set; } = 8445;

    /// <summary>Public port that NATs to <see cref="MediaInternalPort"/> on this instance.</summary>
    public int MediaPublicPort { get; set; } = 8445;

    /// <summary>
    /// Per-instance ordinal, appended to the notification URL path. A media call is pinned to the
    /// VM that created it, so notifications must route back to that exact instance.
    /// </summary>
    public int InstanceOrdinal { get; set; }

    /// <summary>Receive one audio stream per speaker instead of a single mixed stream.</summary>
    public bool ReceiveUnmixedAudio { get; set; }
}

public sealed class VoiceOptions
{
    /// <summary>AI Services / Foundry resource endpoint, e.g. https://foo.cognitiveservices.azure.com.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Optional key. When empty the bot authenticates with its managed identity.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-realtime-2.1";

    /// <summary>Azure neural voice name. Falls back to an OpenAI voice when empty.</summary>
    public string VoiceName { get; set; } = "en-GB-SoniaNeural";

    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// Voice Live expects a canonical model id here, not an Azure deployment name. azure-speech is
    /// the default because it is the only option that accepts a phrase list, which is what makes
    /// the agent's name transcribe reliably.
    /// </summary>
    public string TranscriptionModel { get; set; } = "azure-speech";

    /// <summary>Comma-separated for multi-language auto-detect, e.g. "en-GB,fr-FR".</summary>
    public string Language { get; set; } = "en-GB";

    /// <summary>Speech-probability floor for the semantic VAD. Higher is less trigger-happy in a noisy room.</summary>
    public float VadThreshold { get; set; } = 0.6f;

    public int VadSilenceDurationMs { get; set; } = 600;

    public int VadPrefixPaddingMs { get; set; } = 300;

    /// <summary>
    /// When false the bot transcribes continuously but only answers when addressed by name.
    /// Prevents it interjecting on every pause in a multi-party meeting.
    /// </summary>
    public bool RespondToEveryTurn { get; set; }

    /// <summary>Display name announced in chat and matched as a whole word to wake the agent.</summary>
    public string AgentName { get; set; } = "Aria";

    /// <summary>
    /// Spellings the recogniser produces for the agent's name. Matched as whole words, so a
    /// common word here ("area") would make the agent answer when nobody spoke to it.
    /// </summary>
    public string[] NameAliases { get; set; } = ["aria", "arya", "ariah", "aaria", "ariya"];

    /// <summary>
    /// After the agent replies, the same speaker can follow up without repeating the name for
    /// this long. Without it every follow-up question needs "Aria," in front, which nobody does.
    /// </summary>
    public int FollowUpWindowSeconds { get; set; } = 10;

    /// <summary>Spoken while a slow tool call runs, so the meeting does not hear dead air.</summary>
    public string[] InterimResponses { get; set; } =
    [
        "Let me look that up.",
        "One moment, checking now.",
        "Just pulling that up.",
    ];

    /// <summary>How long to wait before filling the silence.</summary>
    public int InterimResponseThresholdMs { get; set; } = 1500;
}

public sealed class ToolsOptions
{
    public bool EnableWorkIq { get; set; } = true;

    public string WorkIqMcpUrl { get; set; } = "https://workiq.svc.cloud.microsoft/mcp";

    public string WorkIqScope { get; set; } = "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask";

    /// <summary>
    /// Work IQ MCP is delegated-only. A meeting bot has no interactive user, so a refresh token
    /// for a dedicated service account is cached here by <c>scripts/Connect-WorkIq.ps1</c>.
    /// </summary>
    public string TokenCachePath { get; set; } = string.Empty;

    /// <summary>Additional MCP servers to expose as tools.</summary>
    public McpServerOptions[] AdditionalServers { get; set; } = [];

    /// <summary>Post the agent's greeting, answers and recap into the meeting chat.</summary>
    public bool EnableMeetingChat { get; set; } = true;

    /// <summary>Teams app catalogue id, installed into each meeting chat so the bot can post and be @mentioned.</summary>
    public string TeamsAppCatalogId { get; set; } = string.Empty;

    /// <summary>Must equal the RSC permissions in the published app manifest, or the chat install is rejected.</summary>
    public string[] TeamsAppRscPermissions { get; set; } =
    [
        "Calls.JoinGroupCalls.Chat",
        "Calls.AccessMedia.Chat",
        "OnlineMeetingParticipant.Read.Chat",
        "ChatMessage.Send.Chat",
        "ChatMessage.Read.Chat",
    ];

    /// <summary>Bot Connector base URL for proactive messages to a meeting chat.</summary>
    public string BotServiceUrl { get; set; } = "https://smba.trafficmanager.net/teams/";
}

public sealed class McpServerOptions
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public bool RequiresDelegatedAuth { get; set; } = true;
}

/// <summary>What the agent does when someone from outside the organisation joins.</summary>
public enum ExternalParticipantPolicy
{
    /// <summary>Carry on as normal. Only appropriate for deliberately cross-tenant meetings.</summary>
    Allow,

    /// <summary>Announce it, stop reaching internal data, and keep facilitating with meeting content only.</summary>
    Restrict,

    /// <summary>Announce it and leave the call.</summary>
    Leave,
}

public sealed class FacilitatorOptions
{
    /// <summary>Speak a short introduction on joining, so nobody is surprised by an AI in the room.</summary>
    public bool AnnounceOnJoin { get; set; } = true;

    /// <summary>
    /// Sets the call's recording status, which shows every participant the recording indicator.
    /// Graph returns 403 unless the bot is registered under a compliance-recording policy.
    /// </summary>
    public bool SetRecordingStatus { get; set; }

    public ExternalParticipantPolicy ExternalParticipants { get; set; } = ExternalParticipantPolicy.Restrict;

    /// <summary>Deployment name of the text model that keeps live notes and reads the shared screen.</summary>
    public string ScribeModel { get; set; } = "gpt-5.4-mini";

    /// <summary>How often the scribe folds new conversation into the running notes.</summary>
    public int ScribeIntervalSeconds { get; set; } = 45;

    /// <summary>Speak time checks at the halfway point and shortly before the scheduled end.</summary>
    public bool ProactiveTimeChecks { get; set; } = true;

    public int WrapUpWarningMinutes { get; set; } = 5;

    /// <summary>Post notes, decisions and action items to the meeting chat when the call ends.</summary>
    public bool PostRecapOnEnd { get; set; } = true;

    /// <summary>Email summary, insights and actions to internal attendees when the call ends, from the Work IQ service account.</summary>
    public bool EmailRecapOnEnd { get; set; } = true;

    /// <summary>Participant utterances needed before a recap email is worth sending.</summary>
    public int MinTurnsForEmail { get; set; } = 3;

    /// <summary>Leave once no humans have been present for this long.</summary>
    public int LeaveWhenAloneSeconds { get; set; } = 60;

    /// <summary>Read the shared screen when asked. Uses the scribe model's vision.</summary>
    public bool EnableScreenVision { get; set; } = true;
}

public sealed class ObservabilityOptions
{
    /// <summary>Export spans to the Agent 365 backend (M365 admin center, Purview, Defender).</summary>
    public bool EnableAgent365 { get; set; } = true;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Entra Agent ID, or the agent's app id when no blueprint has been minted.</summary>
    public string AgentId { get; set; } = string.Empty;

    public string BlueprintId { get; set; } = string.Empty;

    /// <summary>Client id used to acquire the observability exporter token.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string ApplicationInsightsConnectionString { get; set; } = string.Empty;
}
