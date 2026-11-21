using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Skype.Bots.Media;

namespace RtBot.Media;

/// <summary>
/// Grabs a single still from a participant's camera or the active screen share, on request.
/// </summary>
/// <remarks>
/// Subscribes only for the moment a capture is wanted and unsubscribes straight after, so the
/// bot is not decoding everyone's video all meeting. Frames arrive as NV12 and are converted to
/// JPEG for the vision model.
/// </remarks>
public sealed class VisualCapture : IDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(8);

    // The first frames after subscribing are often a low-quality ramp-up; take a later one.
    private const int FramesToSkip = 4;

    private readonly IVideoSocket? _camera;
    private readonly IVideoSocket? _screen;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TaskCompletionSource<byte[]?>? _pending;
    private int _framesSeen;

    public VisualCapture(IVideoSocket? camera, IVideoSocket? screen, ILogger logger)
    {
        _camera = camera;
        _screen = screen;
        _logger = logger;

        if (_camera is not null)
        {
            _camera.VideoMediaReceived += OnFrame;
        }

        if (_screen is not null)
        {
            _screen.VideoMediaReceived += OnFrame;
        }
    }

    public bool HasCamera => _camera is not null;

    public bool HasScreen => _screen is not null;

    /// <summary>One JPEG frame from the given source, or null if nothing arrived in time.</summary>
    public async Task<byte[]?> CaptureAsync(bool screen, uint mediaSourceId, CancellationToken cancellationToken)
    {
        IVideoSocket? socket = screen ? _screen : _camera;
        if (socket is null)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<byte[]?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _framesSeen = 0;
            Volatile.Write(ref _pending, tcs);
            socket.Subscribe(screen ? VideoResolution.HD1080p : VideoResolution.HD720p, mediaSourceId);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FrameTimeout);
            using (timeout.Token.Register(() => tcs.TrySetResult(null)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            try
            {
                socket.Unsubscribe();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unsubscribe after capture failed.");
            }

            _gate.Release();
        }
    }

    private void OnFrame(object? sender, VideoMediaReceivedEventArgs e)
    {
        try
        {
            TaskCompletionSource<byte[]?>? pending = Volatile.Read(ref _pending);
            if (pending is null || pending.Task.IsCompleted || Interlocked.Increment(ref _framesSeen) <= FramesToSkip)
            {
                return;
            }

            VideoMediaBuffer buffer = e.Buffer;
            int width = buffer.VideoFormat.Width;
            int height = buffer.VideoFormat.Height;
            int stride = buffer.Stride > 0 ? (int)buffer.Stride : width;
            if (width <= 0 || height <= 0 || buffer.Data == IntPtr.Zero || buffer.Length < (long)stride * height * 3 / 2)
            {
                return;
            }

            byte[] nv12 = new byte[stride * height * 3 / 2];
            Marshal.Copy(buffer.Data, nv12, 0, nv12.Length);

            // The copy is a memcpy; encoding is not, and media callbacks must return quickly.
            _ = Task.Run(() =>
            {
                try
                {
                    pending.TrySetResult(ToJpeg(nv12, width, height, stride));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not encode a captured frame.");
                    pending.TrySetResult(null);
                }
            });

            // Only the first good frame is wanted; stop later frames from queueing more encodes.
            Interlocked.CompareExchange(ref _pending, null, pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not convert a captured frame.");
        }
        finally
        {
            e.Buffer.Dispose();
        }
    }

    /// <summary>NV12 (BT.601 limited range) to a JPEG, scaled down to at most 1600 px wide.</summary>
    public static unsafe byte[] ToJpeg(byte[] nv12, int width, int height, int stride)
    {
        using Bitmap bmp = new(width, height, PixelFormat.Format24bppRgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            fixed (byte* src = nv12)
            {
                byte* uvPlane = src + (stride * height);
                for (int row = 0; row < height; row++)
                {
                    byte* yRow = src + (row * stride);
                    byte* uvRow = uvPlane + ((row >> 1) * stride);
                    byte* outRow = (byte*)data.Scan0 + (row * data.Stride);

                    for (int col = 0; col < width; col++)
                    {
                        int c = yRow[col] - 16;
                        int d = uvRow[col & ~1] - 128;
                        int e = uvRow[(col & ~1) + 1] - 128;
                        int r = ((298 * c) + (409 * e) + 128) >> 8;
                        int g = ((298 * c) - (100 * d) - (208 * e) + 128) >> 8;
                        int b = ((298 * c) + (516 * d) + 128) >> 8;
                        outRow[(col * 3) + 0] = (byte)Math.Clamp(b, 0, 255);
                        outRow[(col * 3) + 1] = (byte)Math.Clamp(g, 0, 255);
                        outRow[(col * 3) + 2] = (byte)Math.Clamp(r, 0, 255);
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return EncodeJpeg(bmp, 1600);
    }

    public static byte[] EncodeJpeg(Image image, int maxWidth)
    {
        Image source = image;
        Bitmap? scaled = null;
        if (image.Width > maxWidth)
        {
            int h = (int)Math.Round(image.Height * (maxWidth / (double)image.Width));
            scaled = new Bitmap(maxWidth, h);
            using Graphics g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, maxWidth, h);
            source = scaled;
        }

        try
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using EncoderParameters parameters = new(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
            using MemoryStream ms = new();
            source.Save(ms, codec, parameters);
            return ms.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_camera is not null)
        {
            _camera.VideoMediaReceived -= OnFrame;
        }

        if (_screen is not null)
        {
            _screen.VideoMediaReceived -= OnFrame;
        }

        _gate.Dispose();
    }
}
