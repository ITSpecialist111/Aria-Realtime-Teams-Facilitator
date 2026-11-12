using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.VoiceLive;
using RtBot.Configuration;
using RtBot.Facilitator;
using RtBot.Media;

namespace RtBot.Voice;

/// <summary>
/// Bridges one Teams meeting's audio to one Azure AI Voice Live session.
/// </summary>
/// <remarks>
/// Voice Live speaks 16 kHz PCM natively in both directions, the format Teams uses, so audio is
/// never resampled; and its server-side echo cancellation stops the agent barging in on itself
/// when the meeting mix is fed back to it.
/// </remarks>
public sealed class VoiceLiveBridge : IAsyncDisposable
{
    private const int MaxReconnectAttempts = 5;
    private static readonly TimeSpan MaxWaitForGap = TimeSpan.FromSeconds(8);

    private readonly VoiceOptions _options;
    private readonly VoiceLiveClient _client;
    private readonly ILogger<VoiceLiveBridge> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (string Speaker, DateTimeOffset Started)> _turnSpeakers = new();
    private readonly Regex _namePattern;

    private MeetingContext? _meeting;
    private MeetingToolset? _tools;
    private Func<string>? _currentSpeaker;
    private VoiceLiveSession? _session;
    private OutboundAudioPacer? _pacer;
    private Task? _receiveLoop;
    private int _pendingToolOutputs;
    private int _disposed;
    private volatile bool _someoneSpeaking;
    private volatile bool _responseActive;
    private DateTimeOffset _responseStartedAt;
    private DateTimeOffset _lastAgentReplyAt = DateTimeOffset.MinValue;
    private string? _lastAddressedBy;

    public VoiceLiveBridge(VoiceOptions options, VoiceLiveClient client, ILogger<VoiceLiveBridge> logger)
    {
        _options = options;
        _client = client;
        _logger = logger;

        // Whole words only: substring matching would wake on "Maria" or "malaria".
        IEnumerable<string> names = _options.NameAliases.Append(_options.AgentName).Select(Regex.Escape);
        _namePattern = new Regex(
            $@"\b({string.Join('|', names.Distinct(StringComparer.OrdinalIgnoreCase))})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>Raised with the agent's own words, for the meeting chat and notes.</summary>
    public event Func<string, Task>? AgentSpoke;

    /// <summary>Raised once when the voice session cannot be re-established.</summary>
    public event Func<Task>? SessionLost;

    /// <summary>True while the agent has audio queued or playing.</summary>
    public bool IsAgentSpeaking => _pacer?.IsSpeaking == true;

    // A lost response.done (dropped socket, server error) must not silence the agent for the rest of the call.
    private bool IsResponding => _responseActive && DateTimeOffset.UtcNow - _responseStartedAt < TimeSpan.FromSeconds(45);

    /// <summary>Connects the bridge to the meeting it serves. Must be called before <see cref="StartAsync"/>.</summary>
    public void Attach(MeetingContext meeting, MeetingToolset tools, Func<string> currentSpeaker)
    {
        _meeting = meeting;
        _tools = tools;
        _currentSpeaker = currentSpeaker;
    }

    public async Task StartAsync(OutboundAudioPacer pacer, CancellationToken cancellationToken)
    {
        _pacer = pacer;
        await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => RunSessionLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>Re-sends the session configuration, e.g. after the restriction state or attendee list changes.</summary>
    public async Task RefreshSessionAsync(CancellationToken cancellationToken)
    {
        VoiceLiveSession? session = _session;
        if (session is null)
        {
            return;
        }

        await session.ConfigureSessionAsync(BuildSessionOptions(), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Session reconfigured. Restricted={Restricted} Tools={Count}",
            _meeting?.IsRestricted,
            _tools?.AvailableSpecs().Count);
    }

    /// <summary>Feeds inbound meeting audio (16 kHz PCM16 mono) to the model.</summary>
    public async Task SendAudioAsync(byte[] pcm16k, CancellationToken cancellationToken)
    {
        VoiceLiveSession? session = _session;
        if (session is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            // Not cancellable: cancelling an in-flight WebSocket send aborts the whole connection.
            await session.SendInputAudioAsync(pcm16k, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Dropped an input audio frame.");
        }
    }

    /// <summary>Operator-injected prompt, answered as if a participant had said it.</summary>
    public async Task SayAsync(string text, CancellationToken cancellationToken)
    {
        VoiceLiveSession? session = _session;
        if (session is null)
        {
            return;
        }

        await session.AddItemAsync(
            new UserMessageItem(new[] { new InputTextContentPart(text) }),
            cancellationToken).ConfigureAwait(false);
        await RespondAsync(session, "An operator sent this prompt.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Speaks unprompted (time checks, timers, notices), waiting briefly for a pause so the agent
    /// does not talk over a participant.
    /// </summary>
    public async Task AnnounceAsync(string instruction, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + MaxWaitForGap;
        while ((_someoneSpeaking || IsResponding || IsAgentSpeaking) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        VoiceLiveSession? session = _session;
        if (session is null)
        {
            return;
        }

        string extra = $"""
            You are speaking up unprompted, not answering anyone. Ignore the last thing that was said.
            Say only this, in one short natural sentence, without calling any tools: {instruction}
            """;

        await RespondAsync(session, extra, cancellationToken).ConfigureAwait(false);
    }

    private async Task OpenSessionAsync(CancellationToken cancellationToken)
    {
        VoiceLiveSession session = await _client
            .StartSessionAsync(_options.Model, cancellationToken)
            .ConfigureAwait(false);

        await session.ConfigureSessionAsync(BuildSessionOptions(), cancellationToken).ConfigureAwait(false);

        // After a reconnect the new session has heard nothing; seed it with what the meeting has
        // covered so far rather than letting the agent start from a blank slate mid-meeting.
        if (_meeting is not null && _meeting.RecentTurns(1).Count > 0)
        {
            await session.AddItemAsync(
                new SystemMessageItem("Notes from earlier in this meeting:\n" + _meeting.RenderBriefing()),
                cancellationToken).ConfigureAwait(false);
        }

        _session = session;
        _responseActive = false;
        _logger.LogInformation(
            "Voice Live session started. Model={Model} Transcription={Transcription} Tools={Count}",
            _options.Model,
            _options.TranscriptionModel,
            _tools?.AvailableSpecs().Count ?? 0);
    }

    /// <summary>Keeps a live session for as long as the call lasts, rebuilding it when it drops or expires.</summary>
    private async Task RunSessionLoopAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _logger.LogWarning(ex, "Voice Live session ended unexpectedly.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (consecutiveFailures > MaxReconnectAttempts)
            {
                _logger.LogError("Giving up on the voice session after {Count} consecutive failures.", consecutiveFailures);
                if (SessionLost is not null)
                {
                    await SessionLost().ConfigureAwait(false);
                }
                return;
            }

            await CloseSessionAsync().ConfigureAwait(false);

            TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(8, 1 << consecutiveFailures));
            _logger.LogInformation("Reconnecting the voice session in {Delay}.", backoff);

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not reopen the voice session.");
            }
        }
    }

    private async Task CloseSessionAsync()
    {
        VoiceLiveSession? session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        try
        {
            await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Voice Live session close failed.");
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    private VoiceLiveSessionOptions BuildSessionOptions()
    {
        AudioInputTranscriptionOptions transcription = new(_options.TranscriptionModel)
        {
            Language = _options.Language,
        };

        // Biasing the recogniser towards the agent's name and the attendees' names is what makes
        // "Aria, ask Priya..." transcribe correctly instead of as "area" and "prior".
        transcription.PhraseList.Add(_options.AgentName);
        foreach (MeetingParticipant p in _meeting?.Participants ?? [])
        {
            transcription.PhraseList.Add(p.Name);
        }

        VoiceLiveSessionOptions options = new()
        {
            Model = _options.Model,
            Instructions = string.IsNullOrWhiteSpace(_options.Instructions) ? DefaultInstructions() : _options.Instructions,

            InputAudioFormat = InputAudioFormat.Pcm16,
            InputAudioSamplingRate = AudioConstants.SampleRate,
            OutputAudioFormat = OutputAudioFormat.Pcm1616000hz,

            InputAudioEchoCancellation = new AudioEchoCancellation(),
            InputAudioNoiseReduction = new AudioNoiseReduction(AudioNoiseReductionType.AzureDeepNoiseSuppression),
            InputAudioTranscription = transcription,

            TurnDetection = new AzureSemanticVadTurnDetection
            {
                Threshold = _options.VadThreshold,
                PrefixPadding = TimeSpan.FromMilliseconds(_options.VadPrefixPaddingMs),
                SilenceDuration = TimeSpan.FromMilliseconds(_options.VadSilenceDurationMs),
                RemoveFillerWords = true,

                // An always-on responder talks over everyone in a group call. Turns are still
                // segmented and transcribed; replying is gated on being addressed.
                CreateResponse = _options.RespondToEveryTurn,
                InterruptResponse = true,
            },

            Temperature = 0.7f,
        };

        // Work IQ lookups take 10-15 s. Silence that long reads as a crash, so the agent
        // acknowledges the request while the tool runs.
        StaticInterimResponseConfig interim = new()
        {
            LatencyThreshold = TimeSpan.FromMilliseconds(_options.InterimResponseThresholdMs),
        };
        interim.Triggers.Add(InterimResponseTrigger.Tool);
        interim.Triggers.Add(InterimResponseTrigger.Latency);
        foreach (string filler in _options.InterimResponses)
        {
            interim.Texts.Add(filler);
        }

        options.InterimResponse = ModelReaderWriter.Write(interim);

        options.Modalities.Clear();
        options.Modalities.Add(InteractionModality.Text);
        options.Modalities.Add(InteractionModality.Audio);

        options.Voice = string.IsNullOrWhiteSpace(_options.VoiceName)
            ? new OpenAIVoice(OAIVoice.Marin)
            : new AzureStandardVoice(_options.VoiceName);

        foreach (VoiceLiveToolDefinition tool in _tools?.VoiceDefinitions() ?? [])
        {
            options.Tools.Add(tool);
        }

        return options;
    }

    private string DefaultInstructions() =>
        $"""
         You are {_options.AgentName}, an AI facilitator taking part by voice in a Microsoft Teams meeting.
         Everyone in the meeting hears you. You were addressed by name or are following up with someone who just did.

         How to speak:
         - Like a sharp colleague in a meeting: short, direct, one idea per turn. Usually one to three sentences.
         - Never read out more than three items. Summarise aloud and put the detail in the meeting chat with post_to_meeting_chat.
         - Use people's names. Do not narrate what you are about to do; do it and report the result.

         What you do as facilitator:
         - Track the meeting: when someone takes on a task, call record_action_item and confirm it in a few words.
           When the group agrees something, call record_decision.
         - Questions about the meeting itself (who said what, what was agreed, time left) are answered from get_meeting_status.
         - Questions needing organisation data (mail, calendar, files, chats, people, documents) use the workiq tools.
           Answer from tool results, never from memory. If a tool returns nothing useful, say so plainly.
         - When someone refers to something on screen, use look_at_shared_screen.
         - When asked for a plan, timeline or draft, write it into the meeting chat and summarise it aloud in one sentence.

         Never invent names, dates, figures or document contents. If you are unsure, say so.
         Do not repeat sensitive detail unnecessarily, and never read out credentials.
         """;

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        VoiceLiveSession? session = _session;
        if (session is null)
        {
            return;
        }

        await foreach (SessionUpdate update in session.GetUpdatesAsync(cancellationToken).ConfigureAwait(false))
        {
            // A dead socket surfaces as an endless stream of errors rather than an exception.
            if (!session.IsConnected)
            {
                throw new InvalidOperationException("The Voice Live socket is no longer connected.");
            }

            try
            {
                await HandleUpdateAsync(session, update, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed handling Voice Live update {Type}.", update.GetType().Name);
            }
        }
    }

    private async Task HandleUpdateAsync(VoiceLiveSession session, SessionUpdate update, CancellationToken cancellationToken)
    {
        switch (update)
        {
            case SessionUpdateResponseAudioDelta audio:
                _pacer?.Enqueue(audio.ItemId, audio.Delta.ToArray());
                break;

            case SessionUpdateInputAudioBufferSpeechStarted started:
                _someoneSpeaking = true;
                RememberSpeaker(started.ItemId);
                await HandleBargeInAsync(session, cancellationToken).ConfigureAwait(false);
                break;

            case SessionUpdateInputAudioBufferSpeechStopped:
                _someoneSpeaking = false;
                break;

            case SessionUpdateConversationItemInputAudioTranscriptionCompleted transcript:
                await OnParticipantTurnAsync(session, transcript.ItemId, transcript.Transcript, cancellationToken).ConfigureAwait(false);
                break;

            case SessionUpdateResponseCreated:
                _responseActive = true;
                _responseStartedAt = DateTimeOffset.UtcNow;
                break;

            case SessionUpdateResponseAudioTranscriptDone said when !string.IsNullOrWhiteSpace(said.Transcript):
                _lastAgentReplyAt = DateTimeOffset.UtcNow;
                _meeting?.AddTurn(new TranscriptTurn(DateTimeOffset.UtcNow, _options.AgentName, said.Transcript, IsAgent: true));
                if (AgentSpoke is not null)
                {
                    await AgentSpoke(said.Transcript).ConfigureAwait(false);
                }

                break;

            case SessionUpdateResponseFunctionCallArgumentsDone call:
                await DispatchToolCallAsync(session, call, cancellationToken).ConfigureAwait(false);
                break;

            case SessionUpdateResponseDone:
                _responseActive = false;
                await FlushToolOutputsAsync(session, cancellationToken).ConfigureAwait(false);
                break;

            case SessionUpdateError error:
                if (error.Error?.Code == "invalid_session_update_message")
                {
                    _logger.LogCritical("Voice Live rejected the session configuration: {Message}", error.Error.Message);
                }
                else
                {
                    _logger.LogError("Voice Live error: {Code} {Message}", error.Error?.Code, error.Error?.Message);
                }

                break;
        }
    }

    /// <summary>Records who held the floor when this utterance began, so the transcript can be attributed.</summary>
    private void RememberSpeaker(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return;
        }

        string speaker = _currentSpeaker?.Invoke() ?? "Someone";
        lock (_turnSpeakers)
        {
            _turnSpeakers[itemId] = (speaker, DateTimeOffset.UtcNow);

            if (_turnSpeakers.Count > 200)
            {
                foreach (string stale in _turnSpeakers.OrderBy(kv => kv.Value.Started).Take(100).Select(kv => kv.Key).ToList())
                {
                    _turnSpeakers.Remove(stale);
                }
            }
        }
    }

    private async Task OnParticipantTurnAsync(
        VoiceLiveSession session,
        string? itemId,
        string? text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string speaker = "Someone";
        DateTimeOffset started = DateTimeOffset.UtcNow;
        lock (_turnSpeakers)
        {
            if (itemId is not null && _turnSpeakers.Remove(itemId, out var known))
            {
                (speaker, started) = known;
            }
        }

        _meeting?.AddTurn(new TranscriptTurn(started, speaker, text.Trim(), IsAgent: false));
        _meeting?.AddTalkTime(speaker, DateTimeOffset.UtcNow - started);

        if (_options.RespondToEveryTurn)
        {
            return;
        }

        bool named = _namePattern.IsMatch(text);
        bool followUp = !named &&
                        speaker == _lastAddressedBy &&
                        DateTimeOffset.UtcNow - _lastAgentReplyAt < TimeSpan.FromSeconds(_options.FollowUpWindowSeconds);

        if (!named && !followUp)
        {
            return;
        }

        _lastAddressedBy = speaker;
        _logger.LogInformation("Addressed by {Speaker} ({How}).", speaker, named ? "by name" : "follow-up");

        await RespondAsync(session, $"The person talking to you is {speaker}.", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a reply with a fresh briefing attached, so the model always knows the clock and the notes.</summary>
    private async Task RespondAsync(VoiceLiveSession session, string situation, CancellationToken cancellationToken)
    {
        if (IsResponding)
        {
            _logger.LogDebug("A response is already in progress; not starting another.");
            return;
        }

        string briefing = _meeting?.RenderBriefing() ?? string.Empty;
        string restricted = _meeting?.IsRestricted == true
            ? "\nSomeone from outside the organisation is present: do not share internal information; organisation data tools are unavailable."
            : string.Empty;

        try
        {
            await session.StartResponseAsync(
                $"{situation}\n\nCurrent meeting state:\n{briefing}{restricted}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start a response.");
        }
    }

    private async Task HandleBargeInAsync(VoiceLiveSession session, CancellationToken cancellationToken)
    {
        if (_pacer is null || !_pacer.IsSpeaking)
        {
            return;
        }

        BargeInResult cut = _pacer.Flush();

        try
        {
            await session.CancelResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CancelResponse on barge-in was rejected.");
        }

        if (cut.ItemId is not null && cut.PlayedDuration > TimeSpan.Zero)
        {
            try
            {
                // Without this the model believes the meeting heard everything it generated.
                await session.TruncateConversationAsync(cut.ItemId, 0, cut.PlayedDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Truncate after barge-in failed for item {ItemId}.", cut.ItemId);
            }
        }

        _logger.LogInformation("Barge-in after {Played}ms of playback.", (int)cut.PlayedDuration.TotalMilliseconds);
    }

    private async Task DispatchToolCallAsync(
        VoiceLiveSession session,
        SessionUpdateResponseFunctionCallArgumentsDone call,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tool call {Name}", call.Name);

        string output;
        try
        {
            output = _tools is null
                ? JsonSerializer.Serialize(new { error = "No tools are available." })
                : await _tools.InvokeAsync(call.Name, call.Arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Name} threw.", call.Name);
            output = JsonSerializer.Serialize(new { error = ex.Message });
        }

        await session.AddItemAsync(new FunctionCallOutputItem(call.CallId, output), cancellationToken).ConfigureAwait(false);

        // Arrives mid-response; a new response is requested once on response.done.
        Interlocked.Increment(ref _pendingToolOutputs);
    }

    private async Task FlushToolOutputsAsync(VoiceLiveSession session, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _pendingToolOutputs, 0) == 0)
        {
            return;
        }

        try
        {
            await session.StartResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not request a response after tool execution.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The DI container also disposes this, so teardown would otherwise run twice.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        await CloseSessionAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
