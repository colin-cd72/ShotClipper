using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// Azure Blob Storage provider with block upload support.
/// </summary>
public sealed class AzureBlobProvider : ICloudStorageProvider
{
    private readonly ILogger<AzureBlobProvider> _logger;
    private BlobContainerClient? _containerClient;
    private string _prefix = string.Empty;

    public string ProviderId => "azure-blob";
    public string DisplayName => "Azure Blob Storage";
    public bool IsConfigured => _containerClient != null;

    public AzureBlobProvider(ILogger<AzureBlobProvider> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        if (!credentials.Values.TryGetValue("ConnectionString", out var connectionString) ||
            !credentials.Values.TryGetValue("ContainerName", out var containerName))
        {
            throw new ArgumentException("Missing required Azure Blob credentials");
        }

        credentials.Values.TryGetValue("Prefix", out var prefix);
        _prefix = prefix ?? string.Empty;

        var blobServiceClient = new BlobServiceClient(connectionString);
        _containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        _logger.LogInformation("Azure Blob provider initialized for container {Container}", containerName);

        return Task.CompletedTask;
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (_containerClient == null)
            return false;

        try
        {
            await _containerClient.ExistsAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Blob connection validation failed");
            return false;
        }
    }

    public async Task<UploadResult> UploadFileAsync(
        string localFilePath,
        string remotePath,
        UploadMetadata? metadata = null,
        IProgress<UploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_containerClient == null)
            return new UploadResult(false, null, null, "Provider not initialized");

        try
        {
            var blobName = string.IsNullOrEmpty(_prefix)
                ? remotePath
                : $"{_prefix.TrimEnd('/')}/{remotePath.TrimStart('/')}";

            var blobClient = _containerClient.GetBlobClient(blobName);
            var fileInfo = new FileInfo(localFilePath);
            var startTime = DateTime.UtcNow;

            var options = new BlobUploadOptions
            {
                TransferOptions = new Azure.Storage.StorageTransferOptions
                {
                    MaximumConcurrency = 4,
                    MaximumTransferSize = 50 * 1024 * 1024 // 50 MB chunks
                },
                ProgressHandler = new Progress<long>(bytesTransferred =>
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    progress?.Report(new UploadProgress(
                        bytesTransferred,
                        fileInfo.Length,
                        100.0 * bytesTransferred / fileInfo.Length,
                        elapsed,
                        elapsed.TotalSeconds > 0 ? (long)(bytesTransferred / elapsed.TotalSeconds) : 0));
                })
            };

            // Add metadata
            if (metadata != null)
            {
                options.Metadata = new Dictionary<string, string>
                {
                    ["recordedAt"] = metadata.RecordedAt.ToString("O"),
                    ["duration"] = metadata.Duration.ToString()
                };

                if (!string.IsNullOrEmpty(metadata.Timecode))
                    options.Metadata["timecode"] = metadata.Timecode;

                if (!string.IsNullOrEmpty(metadata.ProjectName))
                    options.Metadata["project"] = metadata.ProjectName;
            }

            await using var fileStream = File.OpenRead(localFilePath);
            await blobClient.UploadAsync(fileStream, options, ct);

            var remoteUrl = blobClient.Uri.ToString();

            _logger.LogInformation("Uploaded to Azure Blob: {BlobName}", blobName);

            return new UploadResult(true, blobName, remoteUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Blob upload failed");
            return new UploadResult(false, null, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default)
    {
        if (_containerClient == null)
            return Array.Empty<StorageDestination>();

        try
        {
            var prefix = parentPath ?? _prefix;
            var destinations = new List<StorageDestination>();

            await foreach (var item in _containerClient.GetBlobsByHierarchyAsync(
                delimiter: "/",
                prefix: prefix,
                cancellationToken: ct))
            {
                if (item.IsPrefix)
                {
                    var name = item.Prefix.TrimEnd('/').Split('/').Last();
                    destinations.Add(new StorageDestination(item.Prefix, name, item.Prefix, true));
                }
            }

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Azure Blob destinations");
            return Array.Empty<StorageDestination>();
        }
    }

    public ValueTask DisposeAsync()
    {
        // BlobContainerClient doesn't need disposal
        return ValueTask.CompletedTask;
    }
}
