using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using GoogleUpload = Google.Apis.Upload;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// Google Drive storage provider with resumable upload support.
/// </summary>
public sealed class GoogleDriveProvider : ICloudStorageProvider
{
    private readonly ILogger<GoogleDriveProvider> _logger;
    private DriveService? _service;
    private string _folderId = string.Empty;

    public string ProviderId => "google-drive";
    public string DisplayName => "Google Drive";
    public bool IsConfigured => _service != null;

    public GoogleDriveProvider(ILogger<GoogleDriveProvider> logger)
    {
        _logger = logger;
    }

    public async Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        credentials.Values.TryGetValue("ServiceAccountJson", out var serviceAccountJson);
        credentials.Values.TryGetValue("AccessToken", out var accessToken);

        if (string.IsNullOrEmpty(serviceAccountJson) && string.IsNullOrEmpty(accessToken))
        {
            throw new ArgumentException("Missing Google Drive credentials (ServiceAccountJson or AccessToken)");
        }

        credentials.Values.TryGetValue("FolderId", out var folderId);
        _folderId = folderId ?? "root";

        GoogleCredential credential;

        if (!string.IsNullOrEmpty(serviceAccountJson))
        {
            // Service account authentication
            credential = GoogleCredential.FromJson(serviceAccountJson)
                .CreateScoped(DriveService.Scope.DriveFile);
        }
        else
        {
            // OAuth access token (would need refresh token handling for production)
            credential = GoogleCredential.FromAccessToken(accessToken);
        }

        _service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Screener"
        });

        _logger.LogInformation("Google Drive provider initialized");
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        if (_service == null)
            return false;

        try
        {
            var request = _service.About.Get();
            request.Fields = "user";
            var about = await request.ExecuteAsync(ct);
            _logger.LogDebug("Google Drive connected as: {User}", about.User?.EmailAddress);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Drive connection validation failed");
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
        if (_service == null)
            return new UploadResult(false, null, null, "Provider not initialized");

        try
        {
            var fileInfo = new FileInfo(localFilePath);
            var fileName = Path.GetFileName(remotePath);
            var startTime = DateTime.UtcNow;

            // Create file metadata
            var fileMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = fileName,
                Parents = new List<string> { _folderId }
            };

            // Add custom properties if metadata provided
            if (metadata != null)
            {
                fileMetadata.Properties = new Dictionary<string, string>
                {
                    ["recordedAt"] = metadata.RecordedAt.ToString("O"),
                    ["duration"] = metadata.Duration.ToString()
                };

                if (!string.IsNullOrEmpty(metadata.Timecode))
                    fileMetadata.Properties["timecode"] = metadata.Timecode;

                if (!string.IsNullOrEmpty(metadata.ProjectName))
                    fileMetadata.Properties["project"] = metadata.ProjectName;
            }

            await using var fileStream = File.OpenRead(localFilePath);

            var mimeType = GetMimeType(localFilePath);

            var request = _service.Files.Create(fileMetadata, fileStream, mimeType);
            request.Fields = "id, name, webViewLink";

            // Track progress
            request.ProgressChanged += (uploadProgress) =>
            {
                var elapsed = DateTime.UtcNow - startTime;
                progress?.Report(new UploadProgress(
                    uploadProgress.BytesSent,
                    fileInfo.Length,
                    100.0 * uploadProgress.BytesSent / fileInfo.Length,
                    elapsed,
                    elapsed.TotalSeconds > 0 ? (long)(uploadProgress.BytesSent / elapsed.TotalSeconds) : 0));
            };

            var uploadResult = await request.UploadAsync(ct);

            if (uploadResult.Status == GoogleUpload.UploadStatus.Failed)
            {
                throw uploadResult.Exception ?? new Exception("Upload failed with unknown error");
            }

            var uploadedFile = request.ResponseBody;
            _logger.LogInformation("Uploaded to Google Drive: {FileName} ({Id})", uploadedFile.Name, uploadedFile.Id);

            return new UploadResult(true, uploadedFile.Id, uploadedFile.WebViewLink);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google Drive upload failed");
            return new UploadResult(false, null, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default)
    {
        if (_service == null)
            return Array.Empty<StorageDestination>();

        try
        {
            var folderId = parentPath ?? _folderId;

            var request = _service.Files.List();
            request.Q = $"'{folderId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            request.Fields = "files(id, name)";

            var result = await request.ExecuteAsync(ct);
            var destinations = new List<StorageDestination>();

            foreach (var folder in result.Files)
            {
                destinations.Add(new StorageDestination(
                    folder.Id,
                    folder.Name,
                    folder.Name,
                    true));
            }

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Google Drive folders");
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
            _ => "application/octet-stream"
        };
    }

    public ValueTask DisposeAsync()
    {
        _service?.Dispose();
        _service = null;
        return ValueTask.CompletedTask;
    }
}
