namespace RtBot.Media;

/// <summary>Fixed constants of the Teams audio pipeline.</summary>
public static class AudioConstants
{
    /// <summary>Teams delivers and accepts 16 kHz PCM16 mono.</summary>
    public const int SampleRate = 16000;

    public const int BytesPerSample = 2;

    /// <summary>Every frame carries exactly 20 ms of content.</summary>
    public const int FrameDurationMs = 20;

    /// <summary>320 samples * 2 bytes.</summary>
    public const int FrameSizeBytes = SampleRate / 1000 * FrameDurationMs * BytesPerSample;

    /// <summary>50 frames per second.</summary>
    public const int FramesPerSecond = 1000 / FrameDurationMs;

    public const int BytesPerMillisecond = SampleRate * BytesPerSample / 1000;

    /// <summary>100-nanosecond ticks in one frame, the unit the media platform timestamps in.</summary>
    public const long FrameDurationTicks = FrameDurationMs * TimeSpan.TicksPerMillisecond;

    public static TimeSpan DurationOf(long byteCount) =>
        TimeSpan.FromMilliseconds((double)byteCount / BytesPerMillisecond);
}
