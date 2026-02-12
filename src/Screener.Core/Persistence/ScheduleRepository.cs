using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Scheduling;

namespace Screener.Core.Persistence;

/// <summary>
/// Repository for scheduled recording persistence.
/// </summary>
public sealed class ScheduleRepository
{
    private readonly ILogger<ScheduleRepository> _logger;
    private readonly DatabaseContext _db;

    public ScheduleRepository(ILogger<ScheduleRepository> logger, DatabaseContext db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// Get all schedules.
    /// </summary>
    public async Task<IReadOnlyList<ScheduledRecording>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM schedules ORDER BY start_time";

        var rows = await _db.QueryAsync<ScheduleRow>(sql);
        return rows.Select(MapToSchedule).ToList();
    }

    /// <summary>
    /// Get a schedule by ID.
    /// </summary>
    public async Task<ScheduledRecording?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM schedules WHERE id = @Id";

        var row = await _db.QuerySingleOrDefaultAsync<ScheduleRow>(sql, new { Id = id.ToString() });
        return row != null ? MapToSchedule(row) : null;
    }

    /// <summary>
    /// Get enabled schedules within a time range.
    /// </summary>
    public async Task<IReadOnlyList<ScheduledRecording>> GetUpcomingAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM schedules
            WHERE is_enabled = 1
              AND start_time >= @From
              AND start_time <= @To
            ORDER BY start_time
            """;

        var rows = await _db.QueryAsync<ScheduleRow>(sql, new
        {
            From = from.ToString("O"),
            To = to.ToString("O")
        });

        return rows.Select(MapToSchedule).ToList();
    }

    /// <summary>
    /// Insert a new schedule.
    /// </summary>
    public async Task InsertAsync(ScheduledRecording schedule, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO schedules (
                id, name, start_time, duration_ticks, preset, filename_template,
                recurrence_json, auto_upload, upload_provider_id, is_enabled,
                created_at, last_run_at
            ) VALUES (
                @Id, @Name, @StartTime, @DurationTicks, @Preset, @FilenameTemplate,
                @RecurrenceJson, @AutoUpload, @UploadProviderId, @IsEnabled,
                @CreatedAt, @LastRunAt
            )
            """;

        await _db.ExecuteAsync(sql, MapToRow(schedule));

        _logger.LogDebug("Inserted schedule: {Id}", schedule.Id);
    }

    /// <summary>
    /// Update an existing schedule.
    /// </summary>
    public async Task UpdateAsync(ScheduledRecording schedule, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE schedules SET
                name = @Name,
                start_time = @StartTime,
                duration_ticks = @DurationTicks,
                preset = @Preset,
                filename_template = @FilenameTemplate,
                recurrence_json = @RecurrenceJson,
                auto_upload = @AutoUpload,
                upload_provider_id = @UploadProviderId,
                is_enabled = @IsEnabled,
                last_run_at = @LastRunAt
            WHERE id = @Id
            """;

        await _db.ExecuteAsync(sql, MapToRow(schedule));

        _logger.LogDebug("Updated schedule: {Id}", schedule.Id);
    }

    /// <summary>
    /// Delete a schedule.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM schedules WHERE id = @Id";

        await _db.ExecuteAsync(sql, new { Id = id.ToString() });

        _logger.LogDebug("Deleted schedule: {Id}", id);
    }

    private static ScheduledRecording MapToSchedule(ScheduleRow row)
    {
        RecurrencePattern? recurrence = null;
        if (!string.IsNullOrEmpty(row.RecurrenceJson))
        {
            recurrence = JsonSerializer.Deserialize<RecurrencePattern>(row.RecurrenceJson);
        }

        return new ScheduledRecording(
            Guid.Parse(row.Id),
            row.Name,
            DateTimeOffset.Parse(row.StartTime),
            TimeSpan.FromTicks(row.DurationTicks),
            EncodingPreset.AllPresets.First(p => p.Name == row.Preset),
            row.FilenameTemplate,
            recurrence,
            row.AutoUpload,
            row.UploadProviderId,
            row.IsEnabled,
            DateTimeOffset.Parse(row.CreatedAt),
            string.IsNullOrEmpty(row.LastRunAt) ? null : DateTimeOffset.Parse(row.LastRunAt));
    }

    private static object MapToRow(ScheduledRecording schedule)
    {
        return new
        {
            Id = schedule.Id.ToString(),
            schedule.Name,
            StartTime = schedule.StartTime.ToString("O"),
            DurationTicks = schedule.Duration.Ticks,
            Preset = schedule.Preset.ToString(),
            schedule.FilenameTemplate,
            RecurrenceJson = schedule.Recurrence != null
                ? JsonSerializer.Serialize(schedule.Recurrence)
                : null,
            AutoUpload = schedule.AutoUpload ? 1 : 0,
            schedule.UploadProviderId,
            IsEnabled = schedule.IsEnabled ? 1 : 0,
            CreatedAt = schedule.CreatedAt.ToString("O"),
            LastRunAt = schedule.LastRunAt?.ToString("O")
        };
    }

    private class ScheduleRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string StartTime { get; set; } = string.Empty;
        public long DurationTicks { get; set; }
        public string Preset { get; set; } = string.Empty;
        public string? FilenameTemplate { get; set; }
        public string? RecurrenceJson { get; set; }
        public bool AutoUpload { get; set; }
        public string? UploadProviderId { get; set; }
        public bool IsEnabled { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? LastRunAt { get; set; }
    }
}
