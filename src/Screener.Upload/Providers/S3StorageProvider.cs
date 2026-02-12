using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// AWS S3 cloud storage provider with multipart upload support.
/// </summary>
public sealed class S3StorageProvider : ICloudStorageProvider
{
    private readonly ILogger<S3StorageProvider> _logger;
    private IAmazonS3? _s3Client;
    private S3Configuration? _config;

    public string ProviderId => "aws-s3";
    public string DisplayName => "Amazon S3";
    public bool IsConfigured => _s3Client != null;

    public S3StorageProvider(ILogger<S3StorageProvider> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        if (!credentials.Values.TryGetValue("AccessKeyId", out var accessKeyId) ||
            !credentials.Values.TryGetValue("SecretAccessKey", out var secretAccessKey) ||
            !credentials.Values.TryGetValue("BucketName", out var bucketName))
        {
            throw new ArgumentException("Missing required S3 credentials");
        }

        credentials.Values.TryGetValue("Region", out var region);
        credentials.Values.TryGetValue("Prefix", out var prefix);

        _config = new S3Configuration
        {
            AccessKeyId = accessKeyId,
            SecretAccessKey = secretAccessKey,
            BucketName = bucketName,
            Region = region ?? "us-east-1",
            Prefix = prefix ?? ""
        };

        var awsRegion = RegionEndpoint.GetBySystemName(_config.Region);
        _s3Client = new AmazonS3Client(accessKeyId, secretAccessKey, awsRegion);

        _logger.LogInformation("S3 provider initialized for bucket {Bucket}", bucketName);

        return Task.CompletedTask;
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (_s3Client == null || _config == null)
            return false;

        try
        {
            await _s3Client.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request
            {
                BucketName = _config.BucketName,
                MaxKeys = 1
            }, ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "S3 connection validation failed");
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
        if (_s3Client == null || _config == null)
            return new UploadResult(false, null, null, "Provider not initialized");

        try
        {
            var key = string.IsNullOrEmpty(_config.Prefix)
                ? remotePath
                : $"{_config.Prefix.TrimEnd('/')}/{remotePath.TrimStart('/')}";

            var fileInfo = new FileInfo(localFilePath);
            var startTime = DateTime.UtcNow;
            long bytesTransferred = 0;

            using var transferUtility = new TransferUtility(_s3Client);

            var request = new TransferUtilityUploadRequest
            {
                FilePath = localFilePath,
                BucketName = _config.BucketName,
                Key = key,
                ContentType = GetContentType(localFilePath),
                StorageClass = S3StorageClass.Standard
            };

            // Add metadata
            if (metadata != null)
            {
                request.Metadata.Add("x-amz-meta-recorded-at", metadata.RecordedAt.ToString("O"));
                request.Metadata.Add("x-amz-meta-duration", metadata.Duration.ToString());

                if (!string.IsNullOrEmpty(metadata.Timecode))
                    request.Metadata.Add("x-amz-meta-timecode", metadata.Timecode);

                if (!string.IsNullOrEmpty(metadata.ProjectName))
                    request.Metadata.Add("x-amz-meta-project", metadata.ProjectName);
            }

            request.UploadProgressEvent += (sender, e) =>
            {
                bytesTransferred = e.TransferredBytes;
                var elapsed = DateTime.UtcNow - startTime;

                progress?.Report(new UploadProgress(
                    bytesTransferred,
                    e.TotalBytes,
                    e.PercentDone,
                    elapsed,
                    elapsed.TotalSeconds > 0 ? (long)(bytesTransferred / elapsed.TotalSeconds) : 0));
            };

            await transferUtility.UploadAsync(request, ct);

            var remoteUrl = $"https://{_config.BucketName}.s3.{_config.Region}.amazonaws.com/{key}";

            _logger.LogInformation("Uploaded to S3: {Key}", key);

            return new UploadResult(true, key, remoteUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "S3 upload failed");
            return new UploadResult(false, null, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default)
    {
        if (_s3Client == null || _config == null)
            return Array.Empty<StorageDestination>();

        try
        {
            var prefix = parentPath ?? _config.Prefix;

            var response = await _s3Client.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request
            {
                BucketName = _config.BucketName,
                Prefix = prefix,
                Delimiter = "/",
                MaxKeys = 100
            }, ct);

            var destinations = new List<StorageDestination>();

            // Add common prefixes (folders)
            foreach (var commonPrefix in response.CommonPrefixes)
            {
                var name = commonPrefix.TrimEnd('/').Split('/').Last();
                destinations.Add(new StorageDestination(commonPrefix, name, commonPrefix, true));
            }

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list S3 destinations");
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
            ".json" => "application/json",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
    }

    public ValueTask DisposeAsync()
    {
        _s3Client?.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal class S3Configuration
{
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string Prefix { get; set; } = string.Empty;
}
