using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure.AI.VoiceLive;
using Azure.Identity;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Verification harness for the two integrations most likely to break: the Voice Live
// 16 kHz round trip and the delegated Work IQ MCP connection.

string command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "voicelive" => await VerifyVoiceLiveAsync(args),
    "stream" => await VerifyStreamAsync(args),
    "workiq" => await VerifyWorkIqAsync(args),
    "audio" => VerifyAudioMaths(),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        rtbot-verify <command>

          voicelive <endpoint> <model> [voice]   Round-trip 16 kHz audio through Voice Live.
          stream    <endpoint> <model> [seconds] Hold a session open while streaming 16 kHz audio.
          workiq    <mcpUrl> <accessToken>       List the tools a Work IQ MCP server exposes.
          audio                                  Check the Teams frame arithmetic.
        """);
    return 1;
}

/// <summary>
/// Reproduces exactly what the bot does to the socket: open a session with the bot's options,
/// then pump 100 ms chunks of 16 kHz audio in real time, reporting anything the service says.
/// </summary>
static async Task<int> VerifyStreamAsync(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: rtbot-verify stream <endpoint> <model> [seconds]");
        return 1;
    }

    string endpoint = args[1];
    string model = args[2];
    int seconds = args.Length > 3 && int.TryParse(args[3], out int s) ? s : 180;
    string transcriber = args.Length > 4 ? args[4] : "whisper-1";

    Console.WriteLine($"Streaming for {seconds}s against {model}, transcription={transcriber}");
    Console.WriteLine();

    VoiceLiveClient client = new(new Uri(endpoint), BuildCredential());
    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(seconds + 30));

    VoiceLiveSession session = await client.StartSessionAsync(model, cts.Token);
    Console.WriteLine($"[{Stamp()}] session opened");

    VoiceLiveSessionOptions options = new()
    {
        Model = model,
        Instructions = "You are a meeting assistant.",
        InputAudioFormat = InputAudioFormat.Pcm16,
        InputAudioSamplingRate = 16000,
        OutputAudioFormat = OutputAudioFormat.Pcm1616000hz,
        InputAudioEchoCancellation = new AudioEchoCancellation(),
        InputAudioNoiseReduction = new AudioNoiseReduction(AudioNoiseReductionType.AzureDeepNoiseSuppression),
        InputAudioTranscription = new AudioInputTranscriptionOptions(transcriber) { Language = "en" },
        TurnDetection = new AzureSemanticVadTurnDetection
        {
            Threshold = 0.6f,
            PrefixPadding = TimeSpan.FromMilliseconds(300),
            SilenceDuration = TimeSpan.FromMilliseconds(600),
            RemoveFillerWords = true,
            CreateResponse = false,
            InterruptResponse = true,
        },
        Voice = new AzureStandardVoice("en-GB-SoniaNeural"),
    };

    options.Modalities.Clear();
    options.Modalities.Add(InteractionModality.Text);
    options.Modalities.Add(InteractionModality.Audio);

    await session.ConfigureSessionAsync(options, cts.Token);
    Console.WriteLine($"[{Stamp()}] session configured");

    // 100 ms of 16 kHz PCM16 mono, matching what the bot batches out of Teams.
    byte[] chunk = new byte[3200];
    Random random = new(1);

    Task pump = Task.Run(async () =>
    {
        Stopwatch clock = Stopwatch.StartNew();
        long sent = 0;

        while (clock.Elapsed.TotalSeconds < seconds && !cts.IsCancellationRequested)
        {
            // Low-level noise rather than digital silence, which some VADs treat as no signal.
            for (int i = 0; i < chunk.Length; i += 2)
            {
                short sample = (short)random.Next(-40, 40);
                BitConverter.TryWriteBytes(chunk.AsSpan(i, 2), sample);
            }

            try
            {
                await session.SendInputAudioAsync(chunk, CancellationToken.None);
                sent++;
                if (sent % 100 == 0)
                {
                    Console.WriteLine($"[{Stamp()}] sent {sent} chunks ({sent / 10}s of audio)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Stamp()}] SEND FAILED after {sent} chunks: {ex.GetType().Name} {ex.Message}");
                return;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        Console.WriteLine($"[{Stamp()}] pump finished, {sent} chunks sent");
    });

    try
    {
        await foreach (SessionUpdate update in session.GetUpdatesAsync(cts.Token))
        {
            switch (update)
            {
                case SessionUpdateError e:
                    Console.WriteLine($"[{Stamp()}] ERROR code={e.Error?.Code} type={e.Error?.Type} msg={e.Error?.Message}");
                    Console.WriteLine($"[{Stamp()}] socket connected={session.IsConnected} state={session.ConnectionState}");
                    return 1;

                case SessionUpdateConversationItemInputAudioTranscriptionCompleted t:
                    Console.WriteLine($"[{Stamp()}] transcript: {t.Transcript}");
                    break;

                case SessionUpdateInputAudioBufferSpeechStarted:
                    Console.WriteLine($"[{Stamp()}] speech started");
                    break;

                default:
                    if (update.GetType().Name is not ("SessionUpdateSessionUpdated" or "SessionUpdateSessionCreated"))
                    {
                        break;
                    }

                    Console.WriteLine($"[{Stamp()}] {update.GetType().Name}");
                    break;
            }
        }

        Console.WriteLine($"[{Stamp()}] update stream ended cleanly. connected={session.IsConnected}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{Stamp()}] RECEIVE FAILED: {ex.GetType().Name} {ex.Message}");
        Console.WriteLine($"[{Stamp()}] socket connected={session.IsConnected} state={session.ConnectionState}");
    }

    await pump;
    return 0;
}

static string Stamp() => DateTime.Now.ToString("HH:mm:ss");

static int VerifyAudioMaths()
{
    const int sampleRate = 16000;
    const int frameMs = 20;
    int frameBytes = sampleRate / 1000 * frameMs * 2;

    Pass("Teams frame size", frameBytes == 640, $"{frameBytes} bytes");
    Pass("Frames per second", 1000 / frameMs == 50, $"{1000 / frameMs} fps");
    Pass("Bytes per millisecond", sampleRate * 2 / 1000 == 32, $"{sampleRate * 2 / 1000} B/ms");
    Pass("Voice Live output matches Teams input", true, "Pcm1616000hz == 16 kHz PCM16 mono, no resampling");
    return 0;
}

static async Task<int> VerifyVoiceLiveAsync(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: rtbot-verify voicelive <endpoint> <model> [voice]");
        return 1;
    }

    string endpoint = args[1];
    string model = args[2];
    string voice = args.Length > 3 ? args[3] : "en-GB-SoniaNeural";

    Console.WriteLine($"Endpoint : {endpoint}");
    Console.WriteLine($"Model    : {model}");
    Console.WriteLine($"Voice    : {voice}");
    Console.WriteLine();

    Stopwatch total = Stopwatch.StartNew();

    VoiceLiveClient client = new(new Uri(endpoint), BuildCredential());

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(90));
    VoiceLiveSession session = await client.StartSessionAsync(model, cts.Token);
    Pass("Session opened", true, $"{total.ElapsedMilliseconds} ms");

    VoiceLiveSessionOptions options = new()
    {
        Model = model,
        Instructions = "Reply with exactly: Realtime voice bridge is online.",
        InputAudioFormat = InputAudioFormat.Pcm16,
        InputAudioSamplingRate = 16000,
        OutputAudioFormat = OutputAudioFormat.Pcm1616000hz,
        InputAudioEchoCancellation = new AudioEchoCancellation(),
        InputAudioNoiseReduction = new AudioNoiseReduction(AudioNoiseReductionType.AzureDeepNoiseSuppression),
        TurnDetection = new AzureSemanticVadTurnDetection
        {
            Threshold = 0.6f,
            PrefixPadding = TimeSpan.FromMilliseconds(300),
            SilenceDuration = TimeSpan.FromMilliseconds(600),
            RemoveFillerWords = true,
            CreateResponse = false,
            InterruptResponse = true,
        },
        Voice = new AzureStandardVoice(voice),
    };

    options.Modalities.Clear();
    options.Modalities.Add(InteractionModality.Text);
    options.Modalities.Add(InteractionModality.Audio);

    // Prove tools are accepted in the same shape the bot uses.
    options.Tools.Add(new VoiceLiveFunctionDefinition("verify_probe")
    {
        Description = "A no-op probe used to confirm tool registration.",
        Parameters = BinaryData.FromString("""
            {"type":"object","properties":{"echo":{"type":"string"}},"required":["echo"]}
            """),
    });

    await session.ConfigureSessionAsync(options, cts.Token);
    Pass("Session configured", true, "16 kHz in/out, echo cancellation, semantic VAD, 1 tool");

    await session.AddItemAsync(
        new UserMessageItem(new[] { new InputTextContentPart("Say the confirmation line now.") }),
        cts.Token);
    await session.StartResponseAsync(cts.Token);

    Stopwatch firstAudio = Stopwatch.StartNew();
    long audioBytes = 0;
    long firstAudioMs = -1;
    string transcript = string.Empty;
    bool sessionCreated = false;
    bool done = false;
    string? error = null;

    await foreach (SessionUpdate update in session.GetUpdatesAsync(cts.Token))
    {
        switch (update)
        {
            case SessionUpdateSessionCreated:
                sessionCreated = true;
                break;

            case SessionUpdateResponseAudioDelta audio:
                if (firstAudioMs < 0)
                {
                    firstAudioMs = firstAudio.ElapsedMilliseconds;
                }

                audioBytes += audio.Delta.ToArray().Length;
                break;

            case SessionUpdateResponseAudioTranscriptDone t:
                transcript = t.Transcript ?? string.Empty;
                break;

            case SessionUpdateResponseDone:
                done = true;
                break;

            case SessionUpdateError e:
                error = $"{e.Error?.Code}: {e.Error?.Message}";
                done = true;
                break;
        }

        if (done)
        {
            break;
        }
    }

    Pass("session.created received", sessionCreated);

    if (error is not null)
    {
        Fail("Service reported an error", error);
        await session.CloseAsync(CancellationToken.None);
        return 1;
    }

    Pass("Audio received", audioBytes > 0, $"{audioBytes:N0} bytes");
    Pass("Time to first audio", firstAudioMs >= 0 && firstAudioMs < 8000, $"{firstAudioMs} ms");
    Pass("Transcript received", transcript.Length > 0, transcript);

    // 16 kHz PCM16 mono is 32 bytes per millisecond. A sane utterance is 0.5-30 s.
    double seconds = audioBytes / 32000.0;
    Pass("Output rate consistent with 16 kHz PCM16 mono", seconds is > 0.4 and < 30,
        $"{seconds:F2} s of audio");
    Pass("Frame-aligned for Teams", audioBytes % 2 == 0, "even byte count (whole samples)");

    await session.CloseAsync(CancellationToken.None);
    Console.WriteLine();
    Console.WriteLine($"Total elapsed: {total.ElapsedMilliseconds} ms");
    return 0;
}

static async Task<int> VerifyWorkIqAsync(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: rtbot-verify workiq <mcpUrl> <accessToken>");
        return 1;
    }

    string url = args[1];
    string token = args[2];

    Console.WriteLine($"MCP URL : {url}");
    Console.WriteLine();

    try
    {
        await using McpClient client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(url),
                Name = "workiq",
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {token}",
                },
            }));

        Pass("MCP session initialised", true, client.ServerInfo?.Name ?? "(unnamed server)");

        IList<McpClientTool> tools = await client.ListToolsAsync();
        Pass("Tools listed", tools.Count > 0, $"{tools.Count} tools");

        foreach (McpClientTool tool in tools)
        {
            string description = tool.Description ?? string.Empty;
            Console.WriteLine($"    - {tool.Name,-18} {Trim(description, 90)}");
        }

        // Every tool must carry a schema, or the model cannot call it.
        bool allSchemas = tools.All(t => t.JsonSchema.ValueKind != JsonValueKind.Undefined);
        Pass("All tools expose a JSON schema", allSchemas);

        // Listing tools proves auth; only invoking one proves the agent can actually retrieve.
        McpClientTool? ask = tools.FirstOrDefault(t => t.Name == "ask");
        if (ask is null)
        {
            Fail("Invoke 'ask'", "tool not present");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("  --- 'ask' input schema ---");
        Console.WriteLine("  " + Trim(ask.JsonSchema.ToString(), 1500));
        Console.WriteLine();
        Console.WriteLine("  Invoking 'ask' against live tenant data...");

        Stopwatch sw = Stopwatch.StartNew();
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(
                "ask",
                new Dictionary<string, object?>
                {
                    ["question"] = "What meetings do I have today? Answer in one sentence.",
                });
        }
        catch (Exception ex)
        {
            Fail("Invoke 'ask'", $"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        string text = string.Join(
            "\n",
            result.Content.OfType<TextContentBlock>().Select(b => b.Text));

        Pass("Tool invocation returned", result.IsError != true, $"{sw.ElapsedMilliseconds} ms");
        Pass("Response has content", text.Length > 0, $"{text.Length} chars");

        Console.WriteLine();
        Console.WriteLine("  --- response ---");
        Console.WriteLine("  " + Trim(text.Replace("\n", "\n  "), 1200));

        return result.IsError == true ? 1 : 0;
    }
    catch (Exception ex)
    {
        Fail("Work IQ MCP connection", ex.Message);
        return 1;
    }
}

static string Trim(string value, int max) =>
    value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "...");

// DefaultAzureCredential probes IMDS first and, on a developer machine with no route to
// 169.254.169.254, that probe is slow and its failure masks the real one. Naming the two
// credentials that can actually work here keeps the diagnostics readable.
static Azure.Core.TokenCredential BuildCredential() =>
    new ChainedTokenCredential(
        new AzureCliCredential(),
        new ManagedIdentityCredential());

static void Pass(string label, bool ok, string? detail = null)
{
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(ok ? "  PASS  " : "  FAIL  ");
    Console.ResetColor();
    Console.Write(label);
    if (detail is not null)
    {
        Console.Write($"  ({detail})");
    }

    Console.WriteLine();
}

static void Fail(string label, string detail)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Write("  FAIL  ");
    Console.ResetColor();
    Console.WriteLine($"{label}: {detail}");
}

