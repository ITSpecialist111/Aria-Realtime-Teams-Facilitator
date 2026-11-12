using System.Runtime.InteropServices;
using Microsoft.Skype.Bots.Media;

namespace RtBot.Media;

/// <summary>
/// A send-side audio frame backed by unmanaged memory.
/// </summary>
/// <remarks>
/// The media platform keeps reading the buffer after <c>Send</c> returns and calls
/// <see cref="Dispose(bool)"/> when it is finished with it. Freeing the memory any earlier
/// is a use-after-free — this is the single most common bug in the published samples.
/// </remarks>
internal sealed class UnmanagedAudioSendBuffer : AudioMediaBuffer
{
    private int _disposed;

    private UnmanagedAudioSendBuffer(IntPtr data, long length, AudioFormat format, long timestamp)
    {
        Data = data;
        Length = length;
        AudioFormat = format;
        Timestamp = timestamp;
    }

    public static UnmanagedAudioSendBuffer Create(byte[] pcm, AudioFormat format, long timestamp)
    {
        IntPtr unmanaged = Marshal.AllocHGlobal(pcm.Length);
        try
        {
            Marshal.Copy(pcm, 0, unmanaged, pcm.Length);
        }
        catch
        {
            Marshal.FreeHGlobal(unmanaged);
            throw;
        }

        return new UnmanagedAudioSendBuffer(unmanaged, pcm.Length, format, timestamp);
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
