namespace RtBot.Facilitator;

/// <summary>What the facilitator tools can make happen in the live call.</summary>
public interface IMeetingActions
{
    /// <summary>Speaks a short line, waiting for a natural gap rather than talking over someone.</summary>
    Task SpeakAsync(string instruction, CancellationToken cancellationToken);

    Task<bool> PostToChatAsync(string markdown, CancellationToken cancellationToken);

    /// <summary>JPEG of the active screen share, or of a participant's camera (the current speaker's first), with who it came from, or why nothing was captured.</summary>
    Task<(byte[]? Jpeg, string? From, string? Failure)> CaptureVisualAsync(bool screen, CancellationToken cancellationToken);

    Task LeaveAsync(CancellationToken cancellationToken);

    /// <summary>Re-applies instructions after the active skills change.</summary>
    Task SkillsChangedAsync(CancellationToken cancellationToken);

    /// <summary>Books a real Teams follow-up meeting and sends calendar invites from Aria's mailbox.</summary>
    Task<ScheduledFollowUp> ScheduleFollowUpAsync(
        string? subject,
        double? startInMinutes,
        string? startAtIso,
        double durationMinutes,
        IReadOnlyList<string> additionalAttendees,
        IReadOnlyList<string> agendaItems,
        CancellationToken cancellationToken);
}
