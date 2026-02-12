using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload;

/// <summary>
/// Manages upload queue and coordinates with storage providers.
/// </summary>
public sealed class UploadService : IUploadService, IHostedService, IDisposable
{
    private readonly ILogger<UploadService> _logger;
    private readonly Dictionary<string, ICloudStorageProvider> _providers;
    private readonly ConcurrentQueue<UploadJob> _queue = new();
    private readonly ConcurrentDictionary<Guid, UploadJob> _activeJobs = new();
    private readonly List<UploadJob> _allJobs = new();
    private readonly SemaphoreSlim _uploadSemaphore;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task[]? _workerTasks;
    private int _concurrentUploads = 2;

    public IReadOnlyList<UploadJob> Jobs
    {
        get
        {
            lock (_lock)
            {
                return _allJobs.ToList();
            }
        }
    }

    public event EventHandler<UploadJobStatusChangedEventArgs>? JobStatusChanged;
    public event EventHandler<UploadJobProgressEventArgs>? JobProgress;

    public UploadService(
        ILogger<UploadService> logger,
        IEnumerable<ICloudStorageProvider> providers,
        int concurrentUploads = 2)
    {
        _logger = logger;
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        _concurrentUploads = concurrentUploads;
        _uploadSemaphore = new SemaphoreSlim(concurrentUploads);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _workerTasks = Enumerable.Range(0, _concurrentUploads)
            .Select(_ => ProcessUploadsAsync(_cts.Token))
            .ToArray();

        _logger.LogInformation("Upload service started with {WorkerCount} workers", _concurrentUploads);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_workerTasks != null)
        {
            await Task.WhenAll(_workerTasks);
        }

        _logger.LogInformation("Upload service stopped");
    }

    public Task<UploadJob> EnqueueAsync(UploadRequest request, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(request.LocalFilePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("File not found", request.LocalFilePath);

        var job = new UploadJob(
            Guid.NewGuid(),
            request.LocalFilePath,
            request.ProviderId,
            request.RemotePath,
            request.Metadata,
            UploadJobStatus.Queued,
            request.Priority,
            fileInfo.Length,
            0,
            0,
            null,
            DateTimeOffset.UtcNow,
            null);

        lock (_lock)
        {
            _allJobs.Add(job);
        }

        _queue.Enqueue(job);

        _logger.LogInformation("Upload job enqueued: {JobId} - {FileName}",
            job.Id, Path.GetFileName(request.LocalFilePath));

        return Task.FromResult(job);
    }

    public Task PauseJobAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_activeJobs.TryGetValue(jobId, out var job))
        {
            UpdateJobStatus(jobId, UploadJobStatus.Paused);
        }

        return Task.CompletedTask;
    }

    public Task ResumeJobAsync(Guid jobId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var job = _allJobs.FirstOrDefault(j => j.Id == jobId);
            if (job != null && job.Status == UploadJobStatus.Paused)
            {
                var resumedJob = job with { Status = UploadJobStatus.Queued };
                UpdateJob(resumedJob);
                _queue.Enqueue(resumedJob);
            }
        }

        return Task.CompletedTask;
    }

    public Task CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        UpdateJobStatus(jobId, UploadJobStatus.Cancelled);
        return Task.CompletedTask;
    }

    public async Task RetryJobAsync(Guid jobId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var job = _allJobs.FirstOrDefault(j => j.Id == jobId);
            if (job != null && job.Status == UploadJobStatus.Failed)
            {
                var retriedJob = job with
                {
                    Status = UploadJobStatus.Queued,
                    RetryCount = job.RetryCount + 1,
                    ErrorMessage = null
                };
                UpdateJob(retriedJob);
                _queue.Enqueue(retriedJob);
            }
        }
    }

    public IReadOnlyList<ICloudStorageProvider> GetProviders()
    {
        return _providers.Values.ToList();
    }

    private async Task ProcessUploadsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_queue.TryDequeue(out var job))
                {
                    await ProcessJobAsync(job, ct);
                }
                else
                {
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in upload worker");
            }
        }
    }

    private async Task ProcessJobAsync(UploadJob job, CancellationToken ct)
    {
        if (job.Status is UploadJobStatus.Cancelled or UploadJobStatus.Paused)
            return;

        if (!_providers.TryGetValue(job.ProviderId, out var provider))
        {
            UpdateJobStatus(job.Id, UploadJobStatus.Failed, $"Provider not found: {job.ProviderId}");
            return;
        }

        _activeJobs[job.Id] = job;
        UpdateJobStatus(job.Id, UploadJobStatus.Uploading);

        try
        {
            var progress = new Progress<UploadProgress>(p =>
            {
                JobProgress?.Invoke(this, new UploadJobProgressEventArgs
                {
                    JobId = job.Id,
                    Progress = p
                });

                // Update job with progress
                lock (_lock)
                {
                    var idx = _allJobs.FindIndex(j => j.Id == job.Id);
                    if (idx >= 0)
                    {
                        _allJobs[idx] = _allJobs[idx] with { UploadedBytes = p.BytesTransferred };
                    }
                }
            });

            var result = await provider.UploadFileAsync(
                job.LocalFilePath,
                job.RemotePath,
                job.Metadata,
                progress,
                ct);

            if (result.Success)
            {
                UpdateJobStatus(job.Id, UploadJobStatus.Completed);
                UpdateJobCompleted(job.Id, DateTimeOffset.UtcNow);

                _logger.LogInformation("Upload completed: {JobId} -> {RemoteUrl}",
                    job.Id, result.RemoteUrl);
            }
            else
            {
                throw new Exception(result.ErrorMessage ?? "Upload failed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload failed: {JobId}", job.Id);

            if (job.RetryCount < 3)
            {
                // Re-queue for retry
                var delay = TimeSpan.FromSeconds(Math.Pow(2, job.RetryCount + 1));
                await Task.Delay(delay, ct);

                var retriedJob = job with { RetryCount = job.RetryCount + 1, Status = UploadJobStatus.Queued };
                UpdateJob(retriedJob);
                _queue.Enqueue(retriedJob);

                _logger.LogInformation("Retrying upload: {JobId} (attempt {Attempt})",
                    job.Id, retriedJob.RetryCount);
            }
            else
            {
                UpdateJobStatus(job.Id, UploadJobStatus.Failed, ex.Message);
            }
        }
        finally
        {
            _activeJobs.TryRemove(job.Id, out _);
        }
    }

    private void UpdateJobStatus(Guid jobId, UploadJobStatus status, string? errorMessage = null)
    {
        lock (_lock)
        {
            var idx = _allJobs.FindIndex(j => j.Id == jobId);
            if (idx >= 0)
            {
                var oldStatus = _allJobs[idx].Status;
                _allJobs[idx] = _allJobs[idx] with { Status = status, ErrorMessage = errorMessage };

                JobStatusChanged?.Invoke(this, new UploadJobStatusChangedEventArgs
                {
                    Job = _allJobs[idx],
                    OldStatus = oldStatus
                });
            }
        }
    }

    private void UpdateJob(UploadJob job)
    {
        lock (_lock)
        {
            var idx = _allJobs.FindIndex(j => j.Id == job.Id);
            if (idx >= 0)
            {
                _allJobs[idx] = job;
            }
        }
    }

    private void UpdateJobCompleted(Guid jobId, DateTimeOffset completedAt)
    {
        lock (_lock)
        {
            var idx = _allJobs.FindIndex(j => j.Id == jobId);
            if (idx >= 0)
            {
                _allJobs[idx] = _allJobs[idx] with { CompletedAt = completedAt };
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _uploadSemaphore.Dispose();

        foreach (var provider in _providers.Values)
        {
            provider.DisposeAsync().AsTask().Wait();
        }
    }
}
