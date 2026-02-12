namespace Screener.Abstractions.Upload;

/// <summary>
/// Manages cloud storage uploads.
/// </summary>
public interface IUploadService
{
    /// <summary>
    /// Get all upload jobs.
    /// </summary>
    IReadOnlyList<UploadJob> Jobs { get; }

    /// <summary>
    /// Fired when a job status changes.
    /// </summary>
    event EventHandler<UploadJobStatusChangedEventArgs>? JobStatusChanged;

    /// <summary>
    /// Fired when upload progress updates.
    /// </summary>
    event EventHandler<UploadJobProgressEventArgs>? JobProgress;

    /// <summary>
    /// Enqueue a new upload job.
    /// </summary>
    Task<UploadJob> EnqueueAsync(UploadRequest request, CancellationToken ct = default);

    /// <summary>
    /// Pause a job.
    /// </summary>
    Task PauseJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Resume a paused job.
    /// </summary>
    Task ResumeJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Cancel a job.
    /// </summary>
    Task CancelJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Retry a failed job.
    /// </summary>
    Task RetryJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Get all registered cloud storage providers.
    /// </summary>
    IReadOnlyList<ICloudStorageProvider> GetProviders();
}

/// <summary>
/// A cloud storage provider for uploading files.
/// </summary>
public interface ICloudStorageProvider : IAsyncDisposable
{
    /// <summary>
    /// Unique provider ID (e.g., "aws-s3", "dropbox").
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether the provider is configured and ready.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Initialize with credentials.
    /// </summary>
    Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default);

    /// <summary>
    /// Validate the connection and credentials.
    /// </summary>
    Task<bool> ValidateConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Upload a file.
    /// </summary>
    Task<UploadResult> UploadFileAsync(
        string localFilePath,
        string remotePath,
        UploadMetadata? metadata = null,
        IProgress<UploadProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// List available destinations.
    /// </summary>
    Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default);
}

public record UploadRequest(
    string LocalFilePath,
    string ProviderId,
    string RemotePath,
    UploadMetadata? Metadata = null,
    UploadPriority Priority = UploadPriority.Normal);

public record UploadMetadata(
    string? Timecode,
    string? ProjectName,
    string? Description,
    DateTimeOffset RecordedAt,
    TimeSpan Duration,
    Dictionary<string, string>? CustomFields = null);

public record UploadJob(
    Guid Id,
    string LocalFilePath,
    string ProviderId,
    string RemotePath,
    UploadMetadata? Metadata,
    UploadJobStatus Status,
    UploadPriority Priority,
    long TotalBytes,
    long UploadedBytes,
    int RetryCount,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public enum UploadJobStatus
{
    Queued,
    Uploading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public enum UploadPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public record UploadProgress(
    long BytesTransferred,
    long TotalBytes,
    double PercentComplete,
    TimeSpan Elapsed,
    long BytesPerSecond);

public record UploadResult(
    bool Success,
    string? RemoteId,
    string? RemoteUrl,
    string? ErrorMessage = null);

public record ProviderCredentials(
    string ProviderId,
    Dictionary<string, string> Values);

public record StorageDestination(
    string Id,
    string Name,
    string Path,
    bool IsContainer);

public class UploadJobStatusChangedEventArgs : EventArgs
{
    public required UploadJob Job { get; init; }
    public required UploadJobStatus OldStatus { get; init; }
}

public class UploadJobProgressEventArgs : EventArgs
{
    public required Guid JobId { get; init; }
    public required UploadProgress Progress { get; init; }
}
