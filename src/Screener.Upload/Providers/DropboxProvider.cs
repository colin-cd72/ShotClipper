using Dropbox.Api;
using Dropbox.Api.Files;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// Dropbox storage provider with chunked upload support.
/// </summary>
public sealed class DropboxProvider : ICloudStorageProvider
{
    private readonly ILogger<DropboxProvider> _logger;
    private DropboxClient? _client;
    private string _basePath = string.Empty;

    private const int ChunkSize = 150 * 1024 * 1024; // 150MB chunks

    public string ProviderId => "dropbox";
    public string DisplayName => "Dropbox";
    public bool IsConfigured => _client != null;

    public DropboxProvider(ILogger<DropboxProvider> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        if (!credentials.Values.TryGetValue("AccessToken", out var accessToken))
        {
            throw new ArgumentException("Missing Dropbox access token");
        }

        credentials.Values.TryGetValue("BasePath", out var basePath);
        _basePath = basePath ?? string.Empty;

        // Ensure base path starts with /
        if (!string.IsNullOrEmpty(_basePath) && !_basePath.StartsWith('/'))
            _basePath = "/" + _basePath;

        _client = new DropboxClient(accessToken);

        _logger.LogInformation("Dropbox provider initialized with base path: {BasePath}", _basePath);

        return Task.CompletedTask;
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (_client == null)
            return false;

        try
        {
            var account = await _client.Users.GetCurrentAccountAsync();
            _logger.LogDebug("Dropbox connected as: {Email}", account.Email);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dropbox connection validation failed");
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
            var fullPath = string.IsNullOrEmpty(_basePath)
                ? "/" + remotePath.TrimStart('/')
                : _basePath.TrimEnd('/') + "/" + remotePath.TrimStart('/');

            var fileInfo = new FileInfo(localFilePath);
            var startTime = DateTime.UtcNow;

            await using var fileStream = File.OpenRead(localFilePath);

            FileMetadata result;

            if (fileInfo.Length <= ChunkSize)
            {
                // Simple upload for small files
                result = await _client.Files.UploadAsync(
                    fullPath,
                    WriteMode.Overwrite.Instance,
                    body: fileStream);
            }
            else
            {
                // Chunked upload for large files
                result = await UploadLargeFileAsync(fileStream, fileInfo.Length, fullPath, startTime, progress, ct);
            }

            _logger.LogInformation("Uploaded to Dropbox: {Path}", fullPath);

            return new UploadResult(true, fullPath, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dropbox upload failed");
            return new UploadResult(false, null, null, ex.Message);
        }
    }

    private async Task<FileMetadata> UploadLargeFileAsync(
        FileStream fileStream,
        long fileSize,
        string remotePath,
        DateTime startTime,
        IProgress<UploadProgress>? progress,
        CancellationToken ct)
    {
        var buffer = new byte[ChunkSize];
        long uploadedBytes = 0;
        string? sessionId = null;

        // Start upload session
        var bytesRead = await fileStream.ReadAsync(buffer, ct);
        using (var memStream = new MemoryStream(buffer, 0, bytesRead))
        {
            var sessionStart = await _client!.Files.UploadSessionStartAsync(body: memStream);
            sessionId = sessionStart.SessionId;
            uploadedBytes = bytesRead;
        }

        ReportProgress(progress, uploadedBytes, fileSize, startTime);

        // Upload chunks
        while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            var cursor = new UploadSessionCursor(sessionId, (ulong)uploadedBytes);

            if (fileStream.Position == fileStream.Length)
            {
                // Last chunk - finish session
                using var memStream = new MemoryStream(buffer, 0, bytesRead);
                var commit = new CommitInfo(remotePath, WriteMode.Overwrite.Instance);

                return await _client.Files.UploadSessionFinishAsync(cursor, commit, body: memStream);
            }
            else
            {
                // Append chunk
                using var memStream = new MemoryStream(buffer, 0, bytesRead);
                await _client.Files.UploadSessionAppendV2Async(cursor, body: memStream);
            }

            uploadedBytes += bytesRead;
            ReportProgress(progress, uploadedBytes, fileSize, startTime);
        }

        // Should not reach here normally
        throw new InvalidOperationException("Upload session did not complete properly");
    }

    private static void ReportProgress(IProgress<UploadProgress>? progress, long uploadedBytes, long totalBytes, DateTime startTime)
    {
        if (progress == null) return;

        var elapsed = DateTime.UtcNow - startTime;
        progress.Report(new UploadProgress(
            uploadedBytes,
            totalBytes,
            100.0 * uploadedBytes / totalBytes,
            elapsed,
            elapsed.TotalSeconds > 0 ? (long)(uploadedBytes / elapsed.TotalSeconds) : 0));
    }

    public async Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default)
    {
        if (_client == null)
            return Array.Empty<StorageDestination>();

        try
        {
            var path = parentPath ?? _basePath;
            if (string.IsNullOrEmpty(path))
                path = "";

            var result = await _client.Files.ListFolderAsync(path);
            var destinations = new List<StorageDestination>();

            foreach (var entry in result.Entries.OfType<FolderMetadata>())
            {
                destinations.Add(new StorageDestination(
                    entry.PathLower,
                    entry.Name,
                    entry.PathDisplay,
                    true));
            }

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Dropbox folders");
            return Array.Empty<StorageDestination>();
        }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }
}
