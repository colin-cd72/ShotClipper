using Microsoft.Extensions.Logging;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Scheduling;

namespace Screener.Scheduling;

/// <summary>
/// Manages scheduled recordings.
/// </summary>
public sealed class SchedulingService : ISchedulingService, IDisposable
{
    private readonly ILogger<SchedulingService> _logger;
    private readonly List<ScheduledRecording> _schedules = new();
    private readonly Timer _checkTimer;
    private readonly object _lock = new();

    public IReadOnlyList<ScheduledRecording> Schedules
    {
        get
        {
            lock (_lock)
            {
                return _schedules.ToList();
            }
        }
    }

    public event EventHandler<ScheduleEventArgs>? ScheduleStarting;
    public event EventHandler<ScheduleEventArgs>? ScheduleEnded;
    public event EventHandler<ScheduleEventArgs>? ScheduleMissed;

    public SchedulingService(ILogger<SchedulingService> logger)
    {
        _logger = logger;
        _checkTimer = new Timer(CheckSchedules, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public Task<ScheduledRecording> CreateScheduleAsync(ScheduledRecordingRequest request, CancellationToken ct = default)
    {
        var schedule = new ScheduledRecording(
            Guid.NewGuid(),
            request.Name,
            request.StartTime,
            request.Duration,
            request.Preset,
            request.FilenameTemplate,
            request.Recurrence,
            request.AutoUpload,
            request.UploadProviderId,
            request.IsEnabled,
            DateTimeOffset.UtcNow,
            null,
            request.InputDeviceIds);

        lock (_lock)
        {
            _schedules.Add(schedule);
        }

        _logger.LogInformation("Created schedule: {Name} at {StartTime}", schedule.Name, schedule.StartTime);

        return Task.FromResult(schedule);
    }

    public Task UpdateScheduleAsync(Guid scheduleId, ScheduledRecordingRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var idx = _schedules.FindIndex(s => s.Id == scheduleId);
            if (idx >= 0)
            {
                _schedules[idx] = _schedules[idx] with
                {
                    Name = request.Name,
                    StartTime = request.StartTime,
                    Duration = request.Duration,
                    Preset = request.Preset,
                    FilenameTemplate = request.FilenameTemplate,
                    Recurrence = request.Recurrence,
                    AutoUpload = request.AutoUpload,
                    UploadProviderId = request.UploadProviderId,
                    IsEnabled = request.IsEnabled
                };

                _logger.LogInformation("Updated schedule: {ScheduleId}", scheduleId);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteScheduleAsync(Guid scheduleId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _schedules.RemoveAll(s => s.Id == scheduleId);
        }

        _logger.LogInformation("Deleted schedule: {ScheduleId}", scheduleId);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledRecording>> GetUpcomingSchedulesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            var upcoming = _schedules
                .Where(s => s.IsEnabled && s.StartTime >= from && s.StartTime <= to)
                .OrderBy(s => s.StartTime)
                .ToList();

            return Task.FromResult<IReadOnlyList<ScheduledRecording>>(upcoming);
        }
    }

    public Task<IReadOnlyList<ScheduleConflict>> CheckConflictsAsync(
        DateTimeOffset startTime,
        TimeSpan duration,
        Guid? excludeScheduleId = null,
        CancellationToken ct = default)
    {
        var endTime = startTime + duration;
        var conflicts = new List<ScheduleConflict>();

        lock (_lock)
        {
            foreach (var schedule in _schedules.Where(s => s.IsEnabled && s.Id != excludeScheduleId))
            {
                var scheduleEnd = schedule.StartTime + schedule.Duration;

                // Check for overlap
                if (startTime < scheduleEnd && endTime > schedule.StartTime)
                {
                    var overlapStart = startTime > schedule.StartTime ? startTime : schedule.StartTime;
                    var overlapEnd = endTime < scheduleEnd ? endTime : scheduleEnd;

                    conflicts.Add(new ScheduleConflict(schedule, overlapStart, overlapEnd));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ScheduleConflict>>(conflicts);
    }

    private void CheckSchedules(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        var bufferTime = TimeSpan.FromSeconds(30);

        lock (_lock)
        {
            foreach (var schedule in _schedules.Where(s => s.IsEnabled))
            {
                var timeTillStart = schedule.StartTime - now;

                // Check if schedule is about to start
                if (timeTillStart > TimeSpan.Zero && timeTillStart <= bufferTime)
                {
                    _logger.LogInformation("Schedule starting soon: {Name}", schedule.Name);
                    ScheduleStarting?.Invoke(this, new ScheduleEventArgs { Schedule = schedule });
                }
                // Check if schedule was missed
                else if (timeTillStart < TimeSpan.Zero && timeTillStart > -schedule.Duration && schedule.LastRunAt == null)
                {
                    _logger.LogWarning("Schedule missed: {Name}", schedule.Name);
                    ScheduleMissed?.Invoke(this, new ScheduleEventArgs { Schedule = schedule });
                }
            }
        }
    }

    /// <summary>
    /// Mark a schedule as having run.
    /// </summary>
    public void MarkScheduleRun(Guid scheduleId)
    {
        lock (_lock)
        {
            var idx = _schedules.FindIndex(s => s.Id == scheduleId);
            if (idx >= 0)
            {
                _schedules[idx] = _schedules[idx] with { LastRunAt = DateTimeOffset.UtcNow };

                // If recurring, schedule next occurrence
                var schedule = _schedules[idx];
                if (schedule.Recurrence != null)
                {
                    var nextStart = CalculateNextOccurrence(schedule);
                    if (nextStart.HasValue)
                    {
                        _schedules[idx] = schedule with { StartTime = nextStart.Value };
                        _logger.LogInformation("Next occurrence scheduled for {Time}", nextStart.Value);
                    }
                    else
                    {
                        // No more occurrences, disable schedule
                        _schedules[idx] = schedule with { IsEnabled = false };
                    }
                }
            }
        }
    }

    private DateTimeOffset? CalculateNextOccurrence(ScheduledRecording schedule)
    {
        if (schedule.Recurrence == null)
            return null;

        var current = schedule.StartTime;
        var recurrence = schedule.Recurrence;

        var next = recurrence.Type switch
        {
            RecurrenceType.Daily => current.AddDays(recurrence.Interval),
            RecurrenceType.Weekly => CalculateNextWeekly(current, recurrence),
            RecurrenceType.Monthly => current.AddMonths(recurrence.Interval),
            _ => (DateTimeOffset?)null
        };

        // Check end conditions
        if (next.HasValue)
        {
            if (recurrence.EndDate.HasValue && next > recurrence.EndDate)
                return null;

            // Would need to track occurrence count for MaxOccurrences
        }

        return next;
    }

    private DateTimeOffset CalculateNextWeekly(DateTimeOffset current, RecurrencePattern recurrence)
    {
        var daysOfWeek = recurrence.DaysOfWeek ?? new[] { current.DayOfWeek };
        var next = current.AddDays(1);

        for (int i = 0; i < 7 * recurrence.Interval; i++)
        {
            if (daysOfWeek.Contains(next.DayOfWeek))
                return next;

            next = next.AddDays(1);
        }

        return current.AddDays(7 * recurrence.Interval);
    }

    public void Dispose()
    {
        _checkTimer.Dispose();
    }
}
