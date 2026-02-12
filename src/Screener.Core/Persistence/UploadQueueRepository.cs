using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Core.Persistence;

/// <summary>
/// Repository for upload queue persistence.
/// </summary>
public sealed class UploadQueueRepository
{
    private readonly ILogger<UploadQueueRepository> _logger;
    private readonly DatabaseContext _db;

    public UploadQueueRepository(ILogger<UploadQueueRepository> logger, DatabaseContext db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// Get all queued uploads.
    /// </summary>
    public async Task<IReadOnlyList<QueuedUpload>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM upload_queue ORDER BY priority DESC, created_at";

        var rows = await _db.QueryAsync<UploadRow>(sql);
        return rows.Select(MapToUpload).ToList();
    }

    /// <summary>
    /// Get uploads by status.
    /// </summary>
    public async Task<IReadOnlyList<QueuedUpload>> GetByStatusAsync(
        UploadStatus status,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM upload_queue
            WHERE status = @Status
            ORDER BY priority DESC, created_at
            """;

        var rows = await _db.QueryAsync<UploadRow>(sql, new { Status = status.ToString() });
        return rows.Select(MapToUpload).ToList();
    }

    /// <summary>
    /// Get pending uploads ready to process.
    /// </summary>
    public async Task<IReadOnlyList<QueuedUpload>> GetPendingAsync(
        int limit = 10,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT * FROM upload_queue
            WHERE status IN ('Pending', 'Paused')
            ORDER BY priority DESC, created_at
            LIMIT @Limit
            """;

        var rows = await _db.QueryAsync<UploadRow>(sql, new { Limit = limit });
        return rows.Select(MapToUpload).ToList();
    }

    /// <summary>
    /// Get an upload by ID.
    /// </summary>
    public async Task<QueuedUpload?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM upload_queue WHERE id = @Id";

        var row = await _db.QuerySingleOrDefaultAsync<UploadRow>(sql, new { Id = id.ToString() });
        return row != null ? MapToUpload(row) : null;
    }

    /// <summary>
    /// Insert a new upload.
    /// </summary>
    public async Task InsertAsync(QueuedUpload upload, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO upload_queue (
                id, local_file_path, remote_path, provider_id, status, priority,
                bytes_uploaded, total_bytes, error_message, retry_count,
                created_at, started_at, completed_at, metadata_json
            ) VALUES (
                @Id, @LocalFilePath, @RemotePath, @ProviderId, @Status, @Priority,
                @BytesUploaded, @TotalBytes, @ErrorMessage, @RetryCount,
                @CreatedAt, @StartedAt, @CompletedAt, @MetadataJson
            )
            """;

        await _db.ExecuteAsync(sql, MapToRow(upload));

        _logger.LogDebug("Inserted upload: {Id}", upload.Id);
    }

    /// <summary>
    /// Update an existing upload.
    /// </summary>
    public async Task UpdateAsync(QueuedUpload upload, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE upload_queue SET
                status = @Status,
                bytes_uploaded = @BytesUploaded,
                error_message = @ErrorMessage,
                retry_count = @RetryCount,
                started_at = @StartedAt,
                completed_at = @CompletedAt
            WHERE id = @Id
            """;

        await _db.ExecuteAsync(sql, new
        {
            Id = upload.Id.ToString(),
            Status = upload.Status.ToString(),
            upload.BytesUploaded,
            upload.ErrorMessage,
            upload.RetryCount,
            StartedAt = upload.StartedAt?.ToString("O"),
            CompletedAt = upload.CompletedAt?.ToString("O")
        });

        _logger.LogDebug("Updated upload: {Id}, Status: {Status}", upload.Id, upload.Status);
    }

    /// <summary>
    /// Update upload progress.
    /// </summary>
    public async Task UpdateProgressAsync(Guid id, long bytesUploaded, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE upload_queue SET bytes_uploaded = @BytesUploaded
            WHERE id = @Id
            """;

        await _db.ExecuteAsync(sql, new { Id = id.ToString(), BytesUploaded = bytesUploaded });
    }

    /// <summary>
    /// Delete an upload.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM upload_queue WHERE id = @Id";

        await _db.ExecuteAsync(sql, new { Id = id.ToString() });

        _logger.LogDebug("Deleted upload: {Id}", id);
    }

    /// <summary>
    /// Delete completed uploads older than specified age.
    /// </summary>
    public async Task DeleteOldCompletedAsync(TimeSpan age, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - age;

        const string sql = """
            DELETE FROM upload_queue
            WHERE status = 'Completed'
              AND completed_at < @Cutoff
            """;

        var deleted = await _db.ExecuteAsync(sql, new { Cutoff = cutoff.ToString("O") });

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} old completed uploads", deleted);
        }
    }

    private static QueuedUpload MapToUpload(UploadRow row)
    {
        UploadMetadata? metadata = null;
        if (!string.IsNullOrEmpty(row.MetadataJson))
        {
            metadata = JsonSerializer.Deserialize<UploadMetadata>(row.MetadataJson);
        }

        return new QueuedUpload
        {
            Id = Guid.Parse(row.Id),
            LocalFilePath = row.LocalFilePath,
            RemotePath = row.RemotePath,
            ProviderId = row.ProviderId,
            Status = Enum.Parse<UploadStatus>(row.Status),
            Priority = row.Priority,
            BytesUploaded = row.BytesUploaded,
            TotalBytes = row.TotalBytes,
            ErrorMessage = row.ErrorMessage,
            RetryCount = row.RetryCount,
            CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
            StartedAt = string.IsNullOrEmpty(row.StartedAt) ? null : DateTimeOffset.Parse(row.StartedAt),
            CompletedAt = string.IsNullOrEmpty(row.CompletedAt) ? null : DateTimeOffset.Parse(row.CompletedAt),
            Metadata = metadata
        };
    }

    private static object MapToRow(QueuedUpload upload)
    {
        return new
        {
            Id = upload.Id.ToString(),
            upload.LocalFilePath,
            upload.RemotePath,
            upload.ProviderId,
            Status = upload.Status.ToString(),
            upload.Priority,
            upload.BytesUploaded,
            upload.TotalBytes,
            upload.ErrorMessage,
            upload.RetryCount,
            CreatedAt = upload.CreatedAt.ToString("O"),
            StartedAt = upload.StartedAt?.ToString("O"),
            CompletedAt = upload.CompletedAt?.ToString("O"),
            MetadataJson = upload.Metadata != null
                ? JsonSerializer.Serialize(upload.Metadata)
                : null
        };
    }

    private class UploadRow
    {
        public string Id { get; set; } = string.Empty;
        public string LocalFilePath { get; set; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
        public string ProviderId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Priority { get; set; }
        public long BytesUploaded { get; set; }
        public long TotalBytes { get; set; }
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? StartedAt { get; set; }
        public string? CompletedAt { get; set; }
        public string? MetadataJson { get; set; }
    }
}

/// <summary>
/// Upload status enumeration.
/// </summary>
public enum UploadStatus
{
    Pending,
    Uploading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Queued upload entity.
/// </summary>
public class QueuedUpload
{
    public Guid Id { get; set; }
    public string LocalFilePath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public UploadStatus Status { get; set; }
    public int Priority { get; set; }
    public long BytesUploaded { get; set; }
    public long TotalBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public UploadMetadata? Metadata { get; set; }

    public double ProgressPercent => TotalBytes > 0 ? 100.0 * BytesUploaded / TotalBytes : 0;
}
