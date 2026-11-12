using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using Microsoft.Skype.Bots.Media;
using RtBot.Chat;
using RtBot.Configuration;
using RtBot.Facilitator;
using RtBot.Tools;
using RtBot.Voice;

namespace RtBot.Teams;

/// <summary>Owns the Graph communications client and the lifetime of every call the bot is in.</summary>
public sealed class TeamsCallingService : IAsyncDisposable
{
    private readonly RtBotOptions _options;
    private readonly IGraphLogger _graphLogger;
    private readonly IServiceProvider _services;
    private readonly ILogger<TeamsCallingService> _logger;
    private readonly ConcurrentDictionary<string, CallHandler> _handlers = new();

    // Keyed by thread id: the call id is not known until after the join request returns, and
    // the handler can be created before then.
    private readonly ConcurrentDictionary<string, string> _joinUrls = new(StringComparer.Ordinal);

    private ICommunicationsClient? _client;

    public TeamsCallingService(
        IOptions<RtBotOptions> options,
        IGraphLogger graphLogger,
        IServiceProvider services,
        ILogger<TeamsCallingService> logger)
    {
        _options = options.Value;
        _graphLogger = graphLogger;
        _services = services;
        _logger = logger;
    }

    public ICommunicationsClient Client =>
        _client ?? throw new InvalidOperationException("The calling service has not been initialised.");

    public bool TryGetHandler(string callId, out CallHandler? handler) =>
        _handlers.TryGetValue(callId, out handler);

    /// <summary>The live call whose meeting chat is <paramref name="threadId"/>, if the agent is in it.</summary>
    public CallHandler? FindByThread(string threadId) =>
        _handlers.Values.FirstOrDefault(h => string.Equals(h.Meeting.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));

    public void Initialize()
    {
        MediaOptions media = _options.Media;

        // A call is pinned to the machine that created it, so the callback must address this
        // instance specifically rather than a shared VIP.
        Uri notificationUrl = new(
            $"https://{media.ServiceFqdn}:{media.SignalingPublicPort}/{media.InstanceOrdinal}/api/calling/notification");

        MediaPlatformSettings mediaSettings = new()
        {
            ApplicationId = _options.AzureAd.AppId,
            MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
            {
                CertificateThumbprint = media.CertificateThumbprint,
                InstanceInternalPort = media.MediaInternalPort,

                // The platform requires an instance-level public IP. Behind a load balancer or
                // NAT rule the instance cannot see that address, and IPAddress.Any tells the
                // platform to advertise the FQDN and public port instead.
                InstancePublicIPAddress = IPAddress.Any,
                InstancePublicPort = media.MediaPublicPort,
                ServiceFqdn = media.ServiceFqdn,
            },
        };

        GraphTokenProvider tokenProvider = new(_options.AzureAd);

        _client = new CommunicationsClientBuilder("RtBot", _options.AzureAd.AppId, _graphLogger)
            .SetAuthentication(_options.AzureAd.AppId, tokenProvider)
            .SetNotificationUrl(notificationUrl)
            .SetMediaPlatformSettings(mediaSettings)
            .SetServiceBaseUrl(new Uri(_options.AzureAd.PlaceCallEndpointUrl))
            .Build();

        _client.Calls().OnIncoming += OnIncomingCall;
        _client.Calls().OnUpdated += OnCallsUpdated;

        _logger.LogInformation(
            "Media platform initialised. fqdn={Fqdn} signalling={SignalPort} media={MediaPort} notify={Url}",
            media.ServiceFqdn,
            media.SignalingPublicPort,
            media.MediaPublicPort,
            notificationUrl);
    }

    /// <summary>Joins the meeting identified by <paramref name="joinUrl"/> and starts the voice session.</summary>
    public async Task<ICall> JoinMeetingAsync(string joinUrl, CancellationToken cancellationToken)
    {
        (ChatInfo chatInfo, MeetingInfo meetingInfo, string tenantId) = JoinUrlParser.Parse(joinUrl);

        ILocalMediaSession mediaSession = CreateMediaSession();
        _joinUrls[chatInfo.ThreadId!] = joinUrl;

        JoinMeetingParameters parameters = new(chatInfo, meetingInfo, mediaSession)
        {
            TenantId = tenantId,
        };

        // Deliberately no GuestIdentity: joining as a guest puts the bot in the lobby and
        // gives up per-participant audio streams.
        ICall call = await Client.Calls()
            .AddAsync(parameters, Guid.NewGuid(), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Joined meeting. callId={CallId} thread={Thread}", call.Id, chatInfo.ThreadId);
        return call;
    }

    private ILocalMediaSession CreateMediaSession(Guid mediaSessionId = default) =>
        Client.CreateMediaSession(
            new AudioSocketSettings
            {
                StreamDirections = StreamDirection.Sendrecv,

                // Pcm16K is the only format available when unmixed audio is requested,
                // and it is what Voice Live is configured to consume, so it is used either way.
                SupportedAudioFormat = AudioFormat.Pcm16K,
                ReceiveUnmixedMeetingAudio = _options.Media.ReceiveUnmixedAudio,
            },
            new VideoSocketSettings
            {
                StreamDirections = StreamDirection.Inactive,
            },
            mediaSessionId: mediaSessionId);

    /// <summary>
    /// Answers calls placed to the agent's resource account.
    /// </summary>
    /// <remarks>
    /// This is the path taken when someone picks Aria out of the address list and adds her to a
    /// call: Teams rings the application instance, and the platform delivers it here.
    /// </remarks>
    private void OnIncomingCall(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        foreach (ICall call in args.AddedResources)
        {
            _logger.LogInformation(
                "Incoming call {CallId} from {Caller}; answering.",
                call.Id,
                call.Resource?.Source?.Identity?.User?.DisplayName ?? "unknown");

            ILocalMediaSession session = Guid.TryParse(call.Id, out Guid callId)
                ? CreateMediaSession(callId)
                : CreateMediaSession();

            _ = call.AnswerAsync(session).ContinueWith(
                t => _logger.LogError(t.Exception, "Failed to answer call {CallId}.", call.Id),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private void OnCallsUpdated(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        foreach (ICall call in args.AddedResources)
        {
            _ = AttachHandlerAsync(call);
        }

        foreach (ICall call in args.RemovedResources)
        {
            if (_handlers.TryRemove(call.Id, out CallHandler? handler))
            {
                _logger.LogInformation("Call {CallId} ended; releasing resources.", call.Id);
                handler.Dispose();
            }
        }
    }

    private async Task AttachHandlerAsync(ICall call)
    {
        try
        {
            string? threadId = call.Resource?.ChatInfo?.ThreadId;
            string? joinUrl = threadId is not null && _joinUrls.TryRemove(threadId, out string? url) ? url : null;

            CallHandler handler = new(
                call,
                _services.GetRequiredService<VoiceLiveBridge>(),
                _services.GetRequiredService<ToolCatalog>(),
                _services.GetRequiredService<TextModelClient>(),
                _services.GetRequiredService<BotChatClient>(),
                _services.GetRequiredService<MeetingDetailsLookup>(),
                _services.GetRequiredService<MeetingMailer>(),
                _options,
                joinUrl,
                _services.GetRequiredService<ILoggerFactory>().CreateLogger($"Call:{call.Id}"));

            if (!_handlers.TryAdd(call.Id, handler))
            {
                handler.Dispose();
                return;
            }

            await handler.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach a handler to call {CallId}.", call.Id);

            if (_handlers.TryRemove(call.Id, out CallHandler? failed))
            {
                failed.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (CallHandler handler in _handlers.Values)
        {
            handler.Dispose();
        }

        _handlers.Clear();

        if (_client is not null)
        {
            _client.Calls().OnIncoming -= OnIncomingCall;
            _client.Calls().OnUpdated -= OnCallsUpdated;
            await _client.TerminateAsync().ConfigureAwait(false);
            _client.Dispose();
        }
    }
}
