using Microsoft.Skype.Bots.Media;

namespace RtBot.Media;

/// <summary>
/// Streams the facilitator tile into the call's video socket at 15 fps while Teams wants it.
/// </summary>
/// <remarks>
/// The platform reports through <c>VideoSendStatusChanged</c> when anyone is viewing the bot's
/// video and which format it prefers. Rendering only while active keeps an unwatched bot idle.
/// </remarks>
public sealed class VideoTilePump : IAsyncDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 15);

    private readonly IVideoSocket _socket;
    private readonly Func<TileState> _state;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    private Task? _loop;
    private volatile bool _active;
    private VideoFormat _format = VideoFormat.NV12_1280x720_15Fps;
    private FacilitatorTileRenderer? _renderer;
    private long _framesSent;
    private byte[]? _lastPng;
    private DateTimeOffset _lastFailureLog = DateTimeOffset.MinValue;
    private byte[]? _lastFrame;
    private long _renders;
    private double _renderMsTotal;
    private double _renderMsMax;

    public VideoTilePump(IVideoSocket socket, Func<TileState> state, ILogger logger)
    {
        _socket = socket;
        _state = state;
        _logger = logger;
    }

    public static VideoFormat[] SupportedFormats { get; } =
    [
        VideoFormat.NV12_1280x720_15Fps,
        VideoFormat.NV12_640x360_15Fps,
    ];

    public bool IsActive => _active;

    public long FramesSent => Interlocked.Read(ref _framesSent);

    public string Format => $"{_format.Width}x{_format.Height}";

    /// <summary>Mean GDI+ render plus NV12 conversion time per rendered frame.</summary>
    public double AverageRenderMs => _renders == 0 ? 0 : Math.Round(_renderMsTotal / _renders, 1);

    public double MaxRenderMs => Math.Round(_renderMsMax, 1);

    public void Start()
    {
        _socket.VideoSendStatusChanged += OnSendStatusChanged;
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>PNG of a freshly rendered frame, for the diagnostics endpoint.</summary>
    public byte[]? Snapshot()
    {
        if (_lastPng is not null && _active)
        {
            return _lastPng;
        }

        using FacilitatorTileRenderer renderer = new(1280, 720);
        renderer.Render(_state(), (DateTimeOffset.UtcNow - _started).TotalSeconds);
        return renderer.SnapshotPng();
    }

    private void OnSendStatusChanged(object? sender, VideoSendStatusChangedEventArgs e)
    {
        _logger.LogWarning(
            "Video send status {Status}, preferred {Format}.",
            e.MediaSendStatus,
            e.PreferredVideoSourceFormat is { } f ? $"{f.VideoColorFormat} {f.Width}x{f.Height}@{f.FrameRate}" : "none");

        if (e.MediaSendStatus == MediaSendStatus.Active && e.PreferredVideoSourceFormat is { } preferred)
        {
            VideoFormat? match = SupportedFormats.FirstOrDefault(f => f.Width == preferred.Width && f.Height == preferred.Height);
            if (match is not null)
            {
                _format = match;
            }
        }

        _active = e.MediaSendStatus == MediaSendStatus.Active;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(FrameInterval);
        long tick = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_active)
                {
                    continue;
                }

                try
                {
                    VideoFormat format = _format;
                    if (_renderer is null || _renderer.Width != format.Width || _renderer.Height != format.Height)
                    {
                        _renderer?.Dispose();
                        _renderer = new FacilitatorTileRenderer(format.Width, format.Height);
                        _lastFrame = null;
                    }

                    // The content changes slowly and only the orb animates, so render at 7.5 fps
                    // and resend the previous frame in between: half the CPU, same 15 fps stream.
                    if (_lastFrame is null || (tick++ & 1) == 0)
                    {
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        _lastFrame = _renderer.Render(_state(), (DateTimeOffset.UtcNow - _started).TotalSeconds);
                        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        _renders++;
                        _renderMsTotal += ms;
                        _renderMsMax = Math.Max(_renderMsMax, ms);

                        if (_renders % 40 == 0)
                        {
                            _lastPng = _renderer.SnapshotPng();
                        }
                    }

                    UnmanagedVideoSendBuffer buffer = UnmanagedVideoSendBuffer.Create(
                        _lastFrame, format.Width * format.Height * 3 / 2, format.Width, format, MediaPlatform.GetCurrentTimestamp());

                    try
                    {
                        _socket.Send(buffer);
                    }
                    catch
                    {
                        buffer.Dispose();
                        throw;
                    }

                    Interlocked.Increment(ref _framesSent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 15 failures a second would bury every other log line.
                    if (DateTimeOffset.UtcNow - _lastFailureLog > TimeSpan.FromMinutes(1))
                    {
                        _lastFailureLog = DateTimeOffset.UtcNow;
                        _logger.LogWarning(ex, "Video frame failed.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _socket.VideoSendStatusChanged -= OnSendStatusChanged;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _renderer?.Dispose();
        _cts.Dispose();
    }
}
