using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// Frame.io storage provider with chunked upload support.
/// </summary>
public sealed class FrameIoProvider : ICloudStorageProvider
{
    private readonly ILogger<FrameIoProvider> _logger;
    private readonly HttpClient _httpClient;

    private string _accessToken = string.Empty;
    private string _rootAssetId = string.Empty;

    private const string ApiBaseUrl = "https://api.frame.io/v2";
    private const int ChunkSize = 25 * 1024 * 1024; // 25MB chunks (Frame.io recommended)

    public string ProviderId => "frame-io";
    public string DisplayName => "Frame.io";
    public bool IsConfigured => !string.IsNullOrEmpty(_accessToken);

    public FrameIoProvider(ILogger<FrameIoProvider> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        if (!credentials.Values.TryGetValue("AccessToken", out var accessToken))
        {
            throw new ArgumentException("Missing Frame.io access token");
        }

        if (!credentials.Values.TryGetValue("RootAssetId", out var rootAssetId))
        {
            throw new ArgumentException("Missing Frame.io root asset/folder ID");
        }

        _accessToken = accessToken;
        _rootAssetId = rootAssetId;

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        _logger.LogInformation("Frame.io provider initialized");

        return Task.CompletedTask;
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
            return false;

        try
        {
            var response = await _httpClient.GetAsync($"{ApiBaseUrl}/me", ct);
            response.EnsureSuccessStatusCode();

            var user = await response.Content.ReadFromJsonAsync<FrameIoUser>(cancellationToken: ct);
            _logger.LogDebug("Frame.io connected as: {Email}", user?.Email);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Frame.io connection validation failed");
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
        if (string.IsNullOrEmpty(_accessToken))
            return new UploadResult(false, null, null, "Provider not initialized");

        try
        {
            var fileInfo = new FileInfo(localFilePath);
            var fileName = Path.GetFileName(remotePath);
            var startTime = DateTime.UtcNow;

            // Create asset (file placeholder)
            var createRequest = new
            {
                name = fileName,
                type = "file",
                filetype = GetMimeType(localFilePath),
                filesize = fileInfo.Length
            };

            var createResponse = await _httpClient.PostAsJsonAsync(
                $"{ApiBaseUrl}/assets/{_rootAssetId}/children",
                createRequest,
                ct);

            createResponse.EnsureSuccessStatusCode();

            var asset = await createResponse.Content.ReadFromJsonAsync<FrameIoAsset>(cancellationToken: ct);

            if (asset?.UploadUrls == null || asset.UploadUrls.Length == 0)
            {
                throw new Exception("No upload URLs provided by Frame.io");
            }

            _logger.LogDebug("Created Frame.io asset: {AssetId}", asset.Id);

            // Upload file in chunks
            await using var fileStream = File.OpenRead(localFilePath);
            long uploadedBytes = 0;
            var chunkIndex = 0;

            foreach (var uploadUrl in asset.UploadUrls)
            {
                ct.ThrowIfCancellationRequested();

                var bytesToRead = (int)Math.Min(ChunkSize, fileInfo.Length - uploadedBytes);
                var buffer = new byte[bytesToRead];
                var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);

                using var content = new ByteArrayContent(buffer, 0, bytesRead);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                var uploadResponse = await _httpClient.PutAsync(uploadUrl, content, ct);
                uploadResponse.EnsureSuccessStatusCode();

                uploadedBytes += bytesRead;
                chunkIndex++;

                var elapsed = DateTime.UtcNow - startTime;
                progress?.Report(new UploadProgress(
                    uploadedBytes,
                    fileInfo.Length,
                    100.0 * uploadedBytes / fileInfo.Length,
                    elapsed,
                    elapsed.TotalSeconds > 0 ? (long)(uploadedBytes / elapsed.TotalSeconds) : 0));

                _logger.LogDebug("Uploaded chunk {Index}/{Total}", chunkIndex, asset.UploadUrls.Length);
            }

            // Complete the upload
            var completeResponse = await _httpClient.PostAsync(
                $"{ApiBaseUrl}/assets/{asset.Id}/uploaded",
                null,
                ct);

            completeResponse.EnsureSuccessStatusCode();

            _logger.LogInformation("Uploaded to Frame.io: {FileName} ({AssetId})", fileName, asset.Id);

            // Construct the web URL
            var webUrl = $"https://app.frame.io/player/{asset.Id}";

            return new UploadResult(true, asset.Id, webUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Frame.io upload failed");
            return new UploadResult(false, null, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
            return Array.Empty<StorageDestination>();

        try
        {
            var assetId = parentPath ?? _rootAssetId;

            var response = await _httpClient.GetAsync(
                $"{ApiBaseUrl}/assets/{assetId}/children?type=folder",
                ct);

            response.EnsureSuccessStatusCode();

            var folders = await response.Content.ReadFromJsonAsync<FrameIoAsset[]>(cancellationToken: ct);
            var destinations = new List<StorageDestination>();

            if (folders != null)
            {
                foreach (var folder in folders)
                {
                    destinations.Add(new StorageDestination(
                        folder.Id ?? "",
                        folder.Name ?? "Unknown",
                        folder.Name ?? "Unknown",
                        true));
                }
            }

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Frame.io folders");
            return Array.Empty<StorageDestination>();
        }
    }

    private static string GetMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mxf" => "application/mxf",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".prores" => "video/prores",
            _ => "application/octet-stream"
        };
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private class FrameIoUser
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private class FrameIoAsset
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("upload_urls")]
        public string[]? UploadUrls { get; set; }
    }
}
