using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// S3-compatible storage provider for MinIO, Backblaze B2, DigitalOcean Spaces, etc.
/// </summary>
public sealed class S3CompatibleProvider : ICloudStorageProvider
{
    private readonly ILogger<S3CompatibleProvider> _logger;
    private IMinioClient? _client;
    private string _bucketName = string.Empty;
    private string _prefix = string.Empty;
    private string _endpoint = string.Empty;

    public string ProviderId => "s3-compatible";
    public string DisplayName => "S3-Compatible Storage";
    public bool IsConfigured => _client != null;

    public S3CompatibleProvider(ILogger<S3CompatibleProvider> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        if (!credentials.Values.TryGetValue("Endpoint", out var endpoint))
        {
            throw new ArgumentException("Missing endpoint URL");
        }

        if (!credentials.Values.TryGetValue("AccessKey", out var accessKey))
        {
            throw new ArgumentException("Missing access key");
        }

        if (!credentials.Values.TryGetValue("SecretKey", out var secretKey))
        {
            throw new ArgumentException("Missing secret key");
        }

        if (!credentials.Values.TryGetValue("BucketName", out var bucketName))
        {
            throw new ArgumentException("Missing bucket name");
        }

        credentials.Values.TryGetValue("Prefix", out var prefix);
        _prefix = prefix ?? string.Empty;

        credentials.Values.TryGetValue("Region", out var region);

        // Parse endpoint to determine if SSL should be used
        var uri = new Uri(endpoint);
        _endpoint = uri.Host + (uri.Port != 80 && uri.Port != 443 ? $":{uri.Port}" : "");
        var useSSL = uri.Scheme == "https";

        _bucketName = bucketName;

        var clientBuilder = new MinioClient()
            .WithEndpoint(_endpoint)
            .WithCredentials(accessKey, secretKey);

        if (useSSL)
            clientBuilder = clientBuilder.WithSSL();

        if (!string.IsNullOrEmpty(region))
            clientBuilder = clientBuilder.WithRegion(region);

        _client = clientBuilder.Build();

        _logger.LogInformation("S3-compatible provider initialized for endpoint: {Endpoint}, bucket: {Bucket}",
            endpoint, bucketName);

        return Task.CompletedTask;
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (_client == null)
            return false;

        try
        {
            var args = new BucketExistsArgs().WithBucket(_bucketName);
            return await _client.BucketExistsAsync(args, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "S3-compatible connection validation failed");
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
            var contentType = GetContentType(localFilePath);

            // Build metadata dictionary
            var objectMetadata = new Dictionary<string, string>();
            if (metadata != null)
            {
                objectMetadata["x-amz-meta-recorded-at"] = metadata.RecordedAt.ToString("O");
                objectMetadata["x-amz-meta-duration"] = metadata.Duration.ToString();

                if (!string.IsNullOrEmpty(metadata.Timecode))
                    objectMetadata["x-amz-meta-timecode"] = metadata.Timecode;

                if (!string.IsNullOrEmpty(metadata.ProjectName))
                    objectMetadata["x-amz-meta-project"] = metadata.ProjectName;
            }

            await using var fileStream = File.OpenRead(localFilePath);

            var args = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithStreamData(fileStream)
                .WithObjectSize(fileInfo.Length)
                .WithContentType(contentType)
                .WithHeaders(objectMetadata)
                .WithProgress(new MinioProgress(progress, fileInfo.Length, startTime));

            await _client.PutObjectAsync(args, ct);

            _logger.LogInformation("Uploaded to S3-compatible storage: {ObjectName}", objectName);

            return new UploadResult(true, objectName, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "S3-compatible upload failed");
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
            if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith('/'))
                prefix += "/";

            var destinations = new List<StorageDestination>();
            var seenPrefixes = new HashSet<string>();

            var args = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithPrefix(prefix)
                .WithRecursive(false);

            var tcs = new TaskCompletionSource<bool>();
            using var ctr = ct.Register(() => tcs.TrySetCanceled());

            _client.ListObjectsAsync(args, ct).Subscribe(
                item =>
                {
                    // Objects ending with / are "folders"
                    if (item.Key.EndsWith('/') || item.IsDir)
                    {
                        var key = item.Key.TrimEnd('/');
                        var name = key.Split('/').Last();

                        if (!string.IsNullOrEmpty(name) && seenPrefixes.Add(key))
                        {
                            destinations.Add(new StorageDestination(
                                key + "/",
                                name,
                                key,
                                true));
                        }
                    }
                },
                ex => tcs.TrySetException(ex),
                () => tcs.TrySetResult(true));

            await tcs.Task;

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list S3-compatible destinations");
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
        _client = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Progress handler for MinIO uploads.
    /// </summary>
    private class MinioProgress : IProgress<Minio.DataModel.ProgressReport>
    {
        private readonly IProgress<UploadProgress>? _progress;
        private readonly long _totalBytes;
        private readonly DateTime _startTime;

        public MinioProgress(IProgress<UploadProgress>? progress, long totalBytes, DateTime startTime)
        {
            _progress = progress;
            _totalBytes = totalBytes;
            _startTime = startTime;
        }

        public void Report(Minio.DataModel.ProgressReport value)
        {
            if (_progress == null) return;

            var elapsed = DateTime.UtcNow - _startTime;
            _progress.Report(new UploadProgress(
                value.TotalBytesTransferred,
                _totalBytes,
                value.Percentage,
                elapsed,
                elapsed.TotalSeconds > 0 ? (long)(value.TotalBytesTransferred / elapsed.TotalSeconds) : 0));
        }
    }
}
