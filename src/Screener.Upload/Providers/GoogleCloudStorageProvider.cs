using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// Google Cloud Storage provider with resumable upload support.
/// </summary>
public sealed class GoogleCloudStorageProvider : ICloudStorageProvider
{
    private readonly ILogger<GoogleCloudStorageProvider> _logger;
    private StorageClient? _client;
    private string _bucketName = string.Empty;
    private string _prefix = string.Empty;

    public string ProviderId => "google-cloud-storage";
    public string DisplayName => "Google Cloud Storage";
    public bool IsConfigured => _client != null;

    public GoogleCloudStorageProvider(ILogger<GoogleCloudStorageProvider> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        if (!credentials.Values.TryGetValue("BucketName", out var bucketName))
        {
            throw new ArgumentException("Missing GCS bucket name");
        }

        _bucketName = bucketName;

        credentials.Values.TryGetValue("Prefix", out var prefix);
        _prefix = prefix ?? string.Empty;

        if (credentials.Values.TryGetValue("ServiceAccountJson", out var serviceAccountJson))
        {
            // Service account authentication
            var credential = GoogleCredential.FromJson(serviceAccountJson);
            _client = await StorageClient.CreateAsync(credential);
        }
        else
        {
            // Use default credentials (from environment)
            _client = await StorageClient.CreateAsync();
        }

        _logger.LogInformation("GCS provider initialized for bucket: {Bucket}", _bucketName);
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (_client == null)
            return false;

        try
        {
            var bucket = await _client.GetBucketAsync(_bucketName, cancellationToken: ct);
            return bucket != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GCS connection validation failed");
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
        if (_client == null)
            return new UploadResult(false, null, null, "Provider not initialized");

        try
        {
            var objectName = string.IsNullOrEmpty(_prefix)
                ? remotePath
                : $"{_prefix.TrimEnd('/')}/{remotePath.TrimStart('/')}";

            var fileInfo = new FileInfo(localFilePath);
            var startTime = DateTime.UtcNow;

            await using var fileStream = File.OpenRead(localFilePath);

            var contentType = GetContentType(localFilePath);

            // Create object with metadata
            var storageObject = new Google.Apis.Storage.v1.Data.Object
            {
                Bucket = _bucketName,
                Name = objectName,
                ContentType = contentType
            };

            if (metadata != null)
            {
                storageObject.Metadata = new Dictionary<string, string>
                {
                    ["recordedAt"] = metadata.RecordedAt.ToString("O"),
                    ["duration"] = metadata.Duration.ToString()
                };

                if (!string.IsNullOrEmpty(metadata.Timecode))
                    storageObject.Metadata["timecode"] = metadata.Timecode;

                if (!string.IsNullOrEmpty(metadata.ProjectName))
                    storageObject.Metadata["project"] = metadata.ProjectName;
            }

            // Upload with progress tracking
            var uploadProgress = new Progress<Google.Apis.Upload.IUploadProgress>(p =>
            {
                var elapsed = DateTime.UtcNow - startTime;
                progress?.Report(new UploadProgress(
                    p.BytesSent,
                    fileInfo.Length,
                    100.0 * p.BytesSent / fileInfo.Length,
                    elapsed,
                    elapsed.TotalSeconds > 0 ? (long)(p.BytesSent / elapsed.TotalSeconds) : 0));
            });

            var result = await _client.UploadObjectAsync(
                storageObject,
                fileStream,
                new UploadObjectOptions(),
                ct,
                uploadProgress);

            var publicUrl = $"https://storage.googleapis.com/{_bucketName}/{objectName}";

            _logger.LogInformation("Uploaded to GCS: {ObjectName}", objectName);

            return new UploadResult(true, objectName, publicUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GCS upload failed");
            return new UploadResult(false, null, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default)
    {
        if (_client == null)
            return Array.Empty<StorageDestination>();

        try
        {
            var prefix = parentPath ?? _prefix;
            var destinations = new List<StorageDestination>();

            // GCS uses prefixes to simulate folders
            var options = new ListObjectsOptions
            {
                Delimiter = "/"
            };

            await foreach (var obj in _client.ListObjectsAsync(_bucketName, prefix, options).WithCancellation(ct))
            {
                // Objects ending with / are "folders"
                if (obj.Name.EndsWith('/'))
                {
                    var name = obj.Name.TrimEnd('/').Split('/').Last();
                    destinations.Add(new StorageDestination(
                        obj.Name,
                        name,
                        obj.Name,
                        true));
                }
            }

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list GCS destinations");
            return Array.Empty<StorageDestination>();
        }
    }

    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mxf" => "application/mxf",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
