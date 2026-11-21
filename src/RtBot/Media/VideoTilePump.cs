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
        int pngEvery = 0;

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
                    }

                    byte[] nv12 = _renderer.Render(_state(), (DateTimeOffset.UtcNow - _started).TotalSeconds);
                    UnmanagedVideoSendBuffer buffer = UnmanagedVideoSendBuffer.Create(
                        nv12, format.Width * format.Height * 3 / 2, format.Width, format, MediaPlatform.GetCurrentTimestamp());

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

                    if (++pngEvery % 75 == 0)
                    {
                        _lastPng = _renderer.SnapshotPng();
                    }
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
