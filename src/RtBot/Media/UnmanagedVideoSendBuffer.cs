using System.Runtime.InteropServices;
using Microsoft.Skype.Bots.Media;

namespace RtBot.Media;

/// <summary>A send-side NV12 video frame in unmanaged memory, freed when the platform disposes it.</summary>
internal sealed class UnmanagedVideoSendBuffer : VideoMediaBuffer
{
    private int _disposed;

    private UnmanagedVideoSendBuffer(IntPtr data, int length, int stride, VideoFormat format, long timestamp)
    {
        Data = data;
        Length = length;
        Stride = stride;
        VideoFormat = format;
        Timestamp = timestamp;
    }

    public static UnmanagedVideoSendBuffer Create(byte[] nv12, int length, int width, VideoFormat format, long timestamp)
    {
        IntPtr unmanaged = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(nv12, 0, unmanaged, length);
        }
        catch
        {
            Marshal.FreeHGlobal(unmanaged);
            throw;
        }

        return new UnmanagedVideoSendBuffer(unmanaged, length, width, format, timestamp);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        if (Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
        }
    }
}
