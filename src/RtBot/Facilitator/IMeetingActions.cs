namespace RtBot.Facilitator;

/// <summary>What the facilitator tools can make happen in the live call.</summary>
public interface IMeetingActions
{
    /// <summary>Speaks a short line, waiting for a natural gap rather than talking over someone.</summary>
    Task SpeakAsync(string instruction, CancellationToken cancellationToken);

    Task<bool> PostToChatAsync(string markdown, CancellationToken cancellationToken);

    /// <summary>JPEG of the current screen share, or null when nobody is sharing.</summary>
    Task<byte[]?> CaptureSharedScreenAsync(CancellationToken cancellationToken);

    Task LeaveAsync(CancellationToken cancellationToken);
}
