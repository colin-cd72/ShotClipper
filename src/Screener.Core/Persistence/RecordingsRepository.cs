using Dapper;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Encoding;

namespace Screener.Core.Persistence;

/// <summary>
/// Repository for recordings history persistence.
/// </summary>
public sealed class RecordingsRepository
{
    private readonly ILogger<RecordingsRepository> _logger;
    private readonly DatabaseContext _db;

    public RecordingsRepository(ILogger<RecordingsRepository> logger, DatabaseContext db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// Get all recordings.
    /// </summary>
    public async Task<IReadOnlyList<RecordingEntry>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM recordings ORDER BY start_time DESC";

        var rows = await _db.QueryAsync<RecordingRow>(sql);
        return rows.Select(MapToEntry).ToList();
    }

    /// <summary>
    /// Get recent recordings.
    /// </summary>
    public async Task<IReadOnlyList<RecordingEntry>> GetRecentAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM recordings
            ORDER BY start_time DESC
            LIMIT @Limit
            """;

        var rows = await _db.QueryAsync<RecordingRow>(sql, new { Limit = limit });
        return rows.Select(MapToEntry).ToList();
    }

    /// <summary>
    /// Get recordings within a date range.
    /// </summary>
    public async Task<IReadOnlyList<RecordingEntry>> GetByDateRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM recordings
            WHERE start_time >= @From AND start_time <= @To
            ORDER BY start_time DESC
            """;

        var rows = await _db.QueryAsync<RecordingRow>(sql, new
        {
            From = from.ToString("O"),
            To = to.ToString("O")
        });

        return rows.Select(MapToEntry).ToList();
    }

    /// <summary>
    /// Get a recording by ID.
    /// </summary>
    public async Task<RecordingEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM recordings WHERE id = @Id";

        var row = await _db.QuerySingleOrDefaultAsync<RecordingRow>(sql, new { Id = id.ToString() });
        return row != null ? MapToEntry(row) : null;
    }

    /// <summary>
    /// Insert a new recording.
    /// </summary>
    public async Task InsertAsync(RecordingEntry entry, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO recordings (
                id, file_path, start_time, end_time, duration_ticks,
                preset, file_size, schedule_id, status, created_at
            ) VALUES (
                @Id, @FilePath, @StartTime, @EndTime, @DurationTicks,
                @Preset, @FileSize, @ScheduleId, @Status, @CreatedAt
            )
            """;

        await _db.ExecuteAsync(sql, MapToRow(entry));

        _logger.LogDebug("Inserted recording: {Id}", entry.Id);
    }

    /// <summary>
    /// Update a recording.
    /// </summary>
    public async Task UpdateAsync(RecordingEntry entry, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE recordings SET
                end_time = @EndTime,
                duration_ticks = @DurationTicks,
                file_size = @FileSize,
                status = @Status
            WHERE id = @Id
            """;

        await _db.ExecuteAsync(sql, new
        {
            Id = entry.Id.ToString(),
            EndTime = entry.EndTime?.ToString("O"),
            DurationTicks = entry.Duration?.Ticks,
            entry.FileSize,
            Status = entry.Status.ToString()
        });

        _logger.LogDebug("Updated recording: {Id}", entry.Id);
    }

    /// <summary>
    /// Delete a recording.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM recordings WHERE id = @Id";

        await _db.ExecuteAsync(sql, new { Id = id.ToString() });

        _logger.LogDebug("Deleted recording: {Id}", id);
    }

    /// <summary>
    /// Get total recording time for a date range.
    /// </summary>
    public async Task<TimeSpan> GetTotalDurationAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(SUM(duration_ticks), 0) FROM recordings
            WHERE start_time >= @From AND start_time <= @To
              AND status = 'Completed'
            """;

        var ticks = await _db.QuerySingleOrDefaultAsync<long>(sql, new
        {
            From = from.ToString("O"),
            To = to.ToString("O")
        });

        return TimeSpan.FromTicks(ticks);
    }

    private static RecordingEntry MapToEntry(RecordingRow row)
    {
        return new RecordingEntry
        {
            Id = Guid.Parse(row.Id),
            FilePath = row.FilePath,
            StartTime = DateTimeOffset.Parse(row.StartTime),
            EndTime = string.IsNullOrEmpty(row.EndTime) ? null : DateTimeOffset.Parse(row.EndTime),
            Duration = row.DurationTicks.HasValue ? TimeSpan.FromTicks(row.DurationTicks.Value) : null,
            Preset = EncodingPreset.AllPresets.First(p => p.Name == row.Preset),
            FileSize = row.FileSize,
            ScheduleId = string.IsNullOrEmpty(row.ScheduleId) ? null : Guid.Parse(row.ScheduleId),
            Status = Enum.Parse<RecordingStatus>(row.Status),
            CreatedAt = DateTimeOffset.Parse(row.CreatedAt)
        };
    }

    private static object MapToRow(RecordingEntry entry)
    {
        return new
        {
            Id = entry.Id.ToString(),
            entry.FilePath,
            StartTime = entry.StartTime.ToString("O"),
            EndTime = entry.EndTime?.ToString("O"),
            DurationTicks = entry.Duration?.Ticks,
            Preset = entry.Preset.ToString(),
            entry.FileSize,
            ScheduleId = entry.ScheduleId?.ToString(),
            Status = entry.Status.ToString(),
            CreatedAt = entry.CreatedAt.ToString("O")
        };
    }

    private class RecordingRow
    {
        public string Id { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string StartTime { get; set; } = string.Empty;
        public string? EndTime { get; set; }
        public long? DurationTicks { get; set; }
        public string Preset { get; set; } = string.Empty;
        public long? FileSize { get; set; }
        public string? ScheduleId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }
}

/// <summary>
/// Recording entry status.
/// </summary>
public enum RecordingStatus
{
    Recording,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Recording history entry.
/// </summary>
public class RecordingEntry
{
    public Guid Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public TimeSpan? Duration { get; set; }
    public EncodingPreset Preset { get; set; }
    public long? FileSize { get; set; }
    public Guid? ScheduleId { get; set; }
    public RecordingStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public string DurationFormatted => Duration.HasValue
        ? $"{(int)Duration.Value.TotalHours:D2}:{Duration.Value.Minutes:D2}:{Duration.Value.Seconds:D2}"
        : "--:--:--";

    public string FileSizeFormatted => FileSize.HasValue
        ? FormatFileSize(FileSize.Value)
        : "--";

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double size = bytes;

        while (size >= 1024 && i < suffixes.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return $"{size:F1} {suffixes[i]}";
    }
}
