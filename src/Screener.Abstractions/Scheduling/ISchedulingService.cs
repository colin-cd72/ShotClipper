using Screener.Abstractions.Encoding;

namespace Screener.Abstractions.Scheduling;

/// <summary>
/// Manages scheduled recordings.
/// </summary>
public interface ISchedulingService
{
    /// <summary>
    /// All scheduled recordings.
    /// </summary>
    IReadOnlyList<ScheduledRecording> Schedules { get; }

    /// <summary>
    /// Fired when a schedule is about to start.
    /// </summary>
    event EventHandler<ScheduleEventArgs>? ScheduleStarting;

    /// <summary>
    /// Fired when a schedule has ended.
    /// </summary>
    event EventHandler<ScheduleEventArgs>? ScheduleEnded;

    /// <summary>
    /// Fired when a schedule is missed (system was off, etc.).
    /// </summary>
    event EventHandler<ScheduleEventArgs>? ScheduleMissed;

    /// <summary>
    /// Create a new scheduled recording.
    /// </summary>
    Task<ScheduledRecording> CreateScheduleAsync(ScheduledRecordingRequest request, CancellationToken ct = default);

    /// <summary>
    /// Update an existing schedule.
    /// </summary>
    Task UpdateScheduleAsync(Guid scheduleId, ScheduledRecordingRequest request, CancellationToken ct = default);

    /// <summary>
    /// Delete a schedule.
    /// </summary>
    Task DeleteScheduleAsync(Guid scheduleId, CancellationToken ct = default);

    /// <summary>
    /// Get upcoming schedules within a time range.
    /// </summary>
    Task<IReadOnlyList<ScheduledRecording>> GetUpcomingSchedulesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>
    /// Check for schedule conflicts.
    /// </summary>
    Task<IReadOnlyList<ScheduleConflict>> CheckConflictsAsync(
        DateTimeOffset startTime,
        TimeSpan duration,
        Guid? excludeScheduleId = null,
        CancellationToken ct = default);
}

public record ScheduledRecording(
    Guid Id,
    string Name,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    EncodingPreset Preset,
    string FilenameTemplate,
    RecurrencePattern? Recurrence,
    bool AutoUpload,
    string? UploadProviderId,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt,
    List<string>? InputDeviceIds = null);

public record ScheduledRecordingRequest(
    string Name,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    EncodingPreset Preset,
    string FilenameTemplate,
    RecurrencePattern? Recurrence = null,
    bool AutoUpload = false,
    string? UploadProviderId = null,
    bool IsEnabled = true,
    List<string>? InputDeviceIds = null);

public record RecurrencePattern(
    RecurrenceType Type,
    int Interval,
    DayOfWeek[]? DaysOfWeek = null,
    DateTimeOffset? EndDate = null,
    int? MaxOccurrences = null);

public enum RecurrenceType
{
    None,
    Daily,
    Weekly,
    Monthly,
    Custom
}

public record ScheduleConflict(
    ScheduledRecording ExistingSchedule,
    DateTimeOffset OverlapStart,
    DateTimeOffset OverlapEnd);

public class ScheduleEventArgs : EventArgs
{
    public required ScheduledRecording Schedule { get; init; }
    public DateTimeOffset EventTime { get; init; } = DateTimeOffset.UtcNow;
}
