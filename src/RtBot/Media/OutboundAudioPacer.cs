using System.Diagnostics;
using Microsoft.Skype.Bots.Media;

namespace RtBot.Media;

/// <summary>
/// Paces assistant audio into the Teams meeting at exactly one 20 ms frame every 20 ms.
/// </summary>
/// <remarks>
/// <para>
/// The model emits audio far faster than real time, so the raw deltas cannot be handed to the
/// media socket as they arrive. This class owns the playback clock, which makes it the only
/// component that knows how much of a response the meeting actually heard — the number that
/// <c>conversation.item.truncate</c> needs after a barge-in.
/// </para>
/// <para>
/// Lookahead is deliberately capped at a few frames. Anything already handed to the platform
/// still plays after a flush, so a large lookahead directly becomes barge-in lag.
/// </para>
/// </remarks>
public sealed class OutboundAudioPacer : IAsyncDisposable
{
    private const int MaxLookaheadFrames = 3;

    private readonly IAudioSocket _audioSocket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();

    private byte[]? _partial;
    private int _partialOffset;
    private long _queuedBytes;
    private long _playedBytes;
    private long _enqueuedBytes;
    private string? _currentItemId;
    private string? _suppressedItemId;
    private short _lastSample;
    private volatile bool _sendActive;
    private long _nextTimestampTicks;
    private Task? _pump;

    public OutboundAudioPacer(IAudioSocket audioSocket, ILogger logger)
    {
        _audioSocket = audioSocket;
        _logger = logger;
        _audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;
    }

    /// <summary>Raised when the queue drains, i.e. the assistant has finished speaking.</summary>
    public event Action? PlaybackDrained;

    /// <summary>True while there is assistant audio queued or in flight.</summary>
    public bool IsSpeaking
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes > 0 || _partial is not null;
            }
        }
    }

    public void Start()
    {
        _pump ??= Task.Factory.StartNew(
            PumpAsync,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>Queues 16 kHz PCM16 mono audio belonging to <paramref name="itemId"/>.</summary>
    public void Enqueue(string itemId, ReadOnlySpan<byte> pcm16k)
    {
        if (pcm16k.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            // The service keeps streaming deltas for a short window after response.cancel.
            // Without this they would look like a new item and the meeting would hear a
            // fragment of the response it just interrupted.
            if (_suppressedItemId == itemId)
            {
                return;
            }

            if (_currentItemId != itemId)
            {
                _currentItemId = itemId;
                _suppressedItemId = null;
                _playedBytes = 0;
                _enqueuedBytes = 0;
            }

            _pending.Enqueue(pcm16k.ToArray());
            _queuedBytes += pcm16k.Length;
            _enqueuedBytes += pcm16k.Length;
        }
    }

    /// <summary>
    /// Discards everything not yet played and returns how much of the current item the meeting heard.
    /// </summary>
    public BargeInResult Flush()
    {
        lock (_gate)
        {
            _pending.Clear();
            _partial = null;
            _partialOffset = 0;
            _queuedBytes = 0;

            // Truncating past what the service actually produced is an error, so the played
            // figure is clamped to the audio received for this item.
            long played = Math.Min(_playedBytes, _enqueuedBytes);
            BargeInResult result = new(_currentItemId, AudioConstants.DurationOf(played));

            // A hard cut mid-waveform clicks. Ramp the last sample to silence over one frame.
            // This frame is not part of the model's output, so it is not counted as played.
            if (_lastSample != 0)
            {
                _pending.Enqueue(BuildFadeOutFrame(_lastSample));
                _queuedBytes += AudioConstants.FrameSizeBytes;
            }

            _suppressedItemId = _currentItemId;
            _currentItemId = null;
            _playedBytes = 0;
            _enqueuedBytes = 0;
            _nextTimestampTicks = 0;
            return result;
        }
    }

    private async Task PumpAsync()
    {
        Stopwatch clock = Stopwatch.StartNew();
        long framesEmitted = 0;
        bool wasSpeaking = false;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                long framesDue = clock.ElapsedMilliseconds / AudioConstants.FrameDurationMs;
                long deficit = framesDue - framesEmitted;

                if (deficit > MaxLookaheadFrames)
                {
                    // Woke up late. Skip ahead rather than emitting a burst, which would
                    // desynchronise the playback clock from what the meeting actually hears.
                    framesEmitted = framesDue - MaxLookaheadFrames;
                    deficit = MaxLookaheadFrames;
                }

                for (long i = 0; i < deficit; i++)
                {
                    if (!TryEmitFrame())
                    {
                        break;
                    }

                    framesEmitted++;
                }

                bool speaking = IsSpeaking;
                if (wasSpeaking && !speaking)
                {
                    PlaybackDrained?.Invoke();
                }

                wasSpeaking = speaking;

                if (!speaking)
                {
                    // Nothing to send: keep the frame counter aligned with wall clock so the
                    // next utterance does not start with a burst, and drop the presentation
                    // clock so the next frame is stamped now rather than where silence began.
                    framesEmitted = clock.ElapsedMilliseconds / AudioConstants.FrameDurationMs;
                    _nextTimestampTicks = 0;
                }

                await Task.Delay(5, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbound audio pump iteration failed.");
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private bool TryEmitFrame()
    {
        if (!_sendActive)
        {
            return false;
        }

        byte[] frame = new byte[AudioConstants.FrameSizeBytes];
        int filled;

        lock (_gate)
        {
            filled = FillFrame(frame);
            if (filled == 0)
            {
                return false;
            }

            // Only the bytes that came from the model count towards playback position;
            // the rest of the frame is silence padding this class invented.
            _playedBytes += filled;
            _lastSample = filled >= 2
                ? BitConverter.ToInt16(frame, filled - 2)
                : (short)0;
        }

        long timestamp = _nextTimestampTicks == 0
            ? MediaPlatform.GetCurrentTimestamp()
            : _nextTimestampTicks;
        _nextTimestampTicks = timestamp + AudioConstants.FrameDurationTicks;

        UnmanagedAudioSendBuffer buffer = UnmanagedAudioSendBuffer.Create(frame, AudioFormat.Pcm16K, timestamp);

        try
        {
            _audioSocket.Send(buffer);
        }
        catch (Exception ex)
        {
            // The platform never took ownership, so this is the only chance to free it.
            buffer.Dispose();
            _logger.LogWarning(ex, "audioSocket.Send failed; dropping frame.");
        }

        return true;
    }

    /// <summary>
    /// Copies up to one frame out of the pending queue and returns the number of real bytes
    /// written. Caller holds <see cref="_gate"/>.
    /// </summary>
    private int FillFrame(byte[] frame)
    {
        int written = 0;

        while (written < frame.Length)
        {
            if (_partial is null)
            {
                if (_pending.Count == 0)
                {
                    break;
                }

                _partial = _pending.Dequeue();
                _partialOffset = 0;
            }

            int available = _partial.Length - _partialOffset;
            int take = Math.Min(available, frame.Length - written);
            Array.Copy(_partial, _partialOffset, frame, written, take);
            written += take;
            _partialOffset += take;
            _queuedBytes -= take;

            if (_partialOffset >= _partial.Length)
            {
                _partial = null;
                _partialOffset = 0;
            }
        }

        // A short tail goes out padded with silence rather than being held back, so the last
        // words of a response are not clipped waiting for a frame that will never arrive.
        return written;
    }

    private static byte[] BuildFadeOutFrame(short startSample)
    {
        byte[] frame = new byte[AudioConstants.FrameSizeBytes];
        int samples = frame.Length / 2;

        for (int i = 0; i < samples; i++)
        {
            double gain = 1.0 - ((double)i / samples);
            short value = (short)(startSample * gain);
            BitConverter.TryWriteBytes(frame.AsSpan(i * 2, 2), value);
        }

        return frame;
    }

    private void OnAudioSendStatusChanged(object? sender, AudioSendStatusChangedEventArgs e)
    {
        _sendActive = e.MediaSendStatus == MediaSendStatus.Active;
        _logger.LogInformation("Audio send status: {Status}", e.MediaSendStatus);
    }

    public async ValueTask DisposeAsync()
    {
        _audioSocket.AudioSendStatusChanged -= OnAudioSendStatusChanged;
        await _cts.CancelAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts.Dispose();
    }
}

/// <summary>How much of an assistant item was actually heard before it was cut off.</summary>
public readonly record struct BargeInResult(string? ItemId, TimeSpan PlayedDuration);
