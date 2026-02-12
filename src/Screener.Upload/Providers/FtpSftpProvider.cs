using FluentFTP;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Screener.Abstractions.Upload;

namespace Screener.Upload.Providers;

/// <summary>
/// FTP/SFTP storage provider with resume support.
/// </summary>
public sealed class FtpSftpProvider : ICloudStorageProvider
{
    private readonly ILogger<FtpSftpProvider> _logger;

    private string _host = string.Empty;
    private int _port;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string? _privateKeyPath;
    private string _basePath = string.Empty;
    private bool _useSftp;
    private bool _usePassiveMode = true;

    private AsyncFtpClient? _ftpClient;
    private SftpClient? _sftpClient;

    public string ProviderId => "ftp-sftp";
    public string DisplayName => _useSftp ? "SFTP" : "FTP";
    public bool IsConfigured => !string.IsNullOrEmpty(_host);

    public FtpSftpProvider(ILogger<FtpSftpProvider> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(ProviderCredentials credentials, CancellationToken ct = default)
    {
        if (!credentials.Values.TryGetValue("Host", out var host))
        {
            throw new ArgumentException("Missing host");
        }

        if (!credentials.Values.TryGetValue("Username", out var username))
        {
            throw new ArgumentException("Missing username");
        }

        _host = host;
        _username = username;

        credentials.Values.TryGetValue("Password", out var password);
        _password = password ?? string.Empty;

        credentials.Values.TryGetValue("PrivateKeyPath", out var privateKeyPath);
        _privateKeyPath = privateKeyPath;

        credentials.Values.TryGetValue("BasePath", out var basePath);
        _basePath = basePath ?? "/";

        if (credentials.Values.TryGetValue("Port", out var portStr) && int.TryParse(portStr, out var port))
        {
            _port = port;
        }

        if (credentials.Values.TryGetValue("UseSftp", out var useSftpStr))
        {
            _useSftp = useSftpStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (credentials.Values.TryGetValue("PassiveMode", out var passiveModeStr))
        {
            _usePassiveMode = passiveModeStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // Set default ports
        if (_port == 0)
        {
            _port = _useSftp ? 22 : 21;
        }

        _logger.LogInformation("{Protocol} provider initialized for {Host}:{Port}",
            _useSftp ? "SFTP" : "FTP", _host, _port);

        return Task.CompletedTask;
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            if (_useSftp)
            {
                using var client = CreateSftpClient();
                client.Connect();
                var connected = client.IsConnected;
                client.Disconnect();
                return connected;
            }
            else
            {
                using var client = CreateFtpClient();
                await client.Connect(ct);
                var connected = client.IsConnected;
                await client.Disconnect(ct);
                return connected;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Protocol} connection validation failed", _useSftp ? "SFTP" : "FTP");
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
        if (string.IsNullOrEmpty(_host))
            return new UploadResult(false, null, null, "Provider not initialized");

        try
        {
            var fullRemotePath = CombinePath(_basePath, remotePath);
            var fileInfo = new FileInfo(localFilePath);
            var startTime = DateTime.UtcNow;

            if (_useSftp)
            {
                await UploadViaSftpAsync(localFilePath, fullRemotePath, fileInfo.Length, startTime, progress, ct);
            }
            else
            {
                await UploadViaFtpAsync(localFilePath, fullRemotePath, fileInfo.Length, startTime, progress, ct);
            }

            _logger.LogInformation("Uploaded via {Protocol}: {Path}", _useSftp ? "SFTP" : "FTP", fullRemotePath);

            return new UploadResult(true, fullRemotePath, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Protocol} upload failed", _useSftp ? "SFTP" : "FTP");
            return new UploadResult(false, null, null, ex.Message);
        }
    }

    private async Task UploadViaSftpAsync(
        string localFilePath,
        string remotePath,
        long fileSize,
        DateTime startTime,
        IProgress<UploadProgress>? progress,
        CancellationToken ct)
    {
        using var client = CreateSftpClient();
        client.Connect();

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
            {
                EnsureSftpDirectoryExists(client, directory);
            }

            await using var fileStream = File.OpenRead(localFilePath);

            // Use SFTP upload with progress
            long uploadedBytes = 0;
            var buffer = new byte[32 * 1024]; // 32KB buffer

            using var remoteStream = client.Create(remotePath);

            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                await remoteStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                uploadedBytes += bytesRead;

                var elapsed = DateTime.UtcNow - startTime;
                progress?.Report(new UploadProgress(
                    uploadedBytes,
                    fileSize,
                    100.0 * uploadedBytes / fileSize,
                    elapsed,
                    elapsed.TotalSeconds > 0 ? (long)(uploadedBytes / elapsed.TotalSeconds) : 0));
            }
        }
        finally
        {
            client.Disconnect();
        }
    }

    private async Task UploadViaFtpAsync(
        string localFilePath,
        string remotePath,
        long fileSize,
        DateTime startTime,
        IProgress<UploadProgress>? progress,
        CancellationToken ct)
    {
        using var client = CreateFtpClient();
        await client.Connect(ct);

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
            {
                await client.CreateDirectory(directory, true, ct);
            }

            // Upload with progress callback
            var ftpProgress = new Progress<FtpProgress>(p =>
            {
                var elapsed = DateTime.UtcNow - startTime;
                var uploadedBytes = (long)(fileSize * p.Progress / 100);
                progress?.Report(new UploadProgress(
                    uploadedBytes,
                    fileSize,
                    p.Progress,
                    elapsed,
                    elapsed.TotalSeconds > 0 ? (long)(uploadedBytes / elapsed.TotalSeconds) : 0));
            });

            await client.UploadFile(localFilePath, remotePath, FtpRemoteExists.Overwrite, true, FtpVerify.None, ftpProgress, ct);
        }
        finally
        {
            await client.Disconnect(ct);
        }
    }

    public async Task<IReadOnlyList<StorageDestination>> ListDestinationsAsync(
        string? parentPath = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_host))
            return Array.Empty<StorageDestination>();

        try
        {
            var path = parentPath ?? _basePath;
            var destinations = new List<StorageDestination>();

            if (_useSftp)
            {
                using var client = CreateSftpClient();
                client.Connect();

                try
                {
                    foreach (var item in client.ListDirectory(path))
                    {
                        if (item.IsDirectory && item.Name != "." && item.Name != "..")
                        {
                            destinations.Add(new StorageDestination(
                                item.FullName,
                                item.Name,
                                item.FullName,
                                true));
                        }
                    }
                }
                finally
                {
                    client.Disconnect();
                }
            }
            else
            {
                using var client = CreateFtpClient();
                await client.Connect(ct);

                try
                {
                    foreach (var item in await client.GetListing(path, ct))
                    {
                        if (item.Type == FtpObjectType.Directory)
                        {
                            destinations.Add(new StorageDestination(
                                item.FullName,
                                item.Name,
                                item.FullName,
                                true));
                        }
                    }
                }
                finally
                {
                    await client.Disconnect(ct);
                }
            }

            return destinations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list {Protocol} directories", _useSftp ? "SFTP" : "FTP");
            return Array.Empty<StorageDestination>();
        }
    }

    private SftpClient CreateSftpClient()
    {
        if (!string.IsNullOrEmpty(_privateKeyPath) && File.Exists(_privateKeyPath))
        {
            var keyFile = new PrivateKeyFile(_privateKeyPath);
            return new SftpClient(_host, _port, _username, keyFile);
        }

        return new SftpClient(_host, _port, _username, _password);
    }

    private AsyncFtpClient CreateFtpClient()
    {
        var client = new AsyncFtpClient(_host, _username, _password, _port);
        client.Config.DataConnectionType = _usePassiveMode
            ? FtpDataConnectionType.AutoPassive
            : FtpDataConnectionType.AutoActive;
        return client;
    }

    private void EnsureSftpDirectoryExists(SftpClient client, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "";

        foreach (var part in parts)
        {
            currentPath += "/" + part;

            try
            {
                client.GetAttributes(currentPath);
            }
            catch (Renci.SshNet.Common.SftpPathNotFoundException)
            {
                client.CreateDirectory(currentPath);
            }
        }
    }

    private static string CombinePath(string basePath, string relativePath)
    {
        basePath = basePath.TrimEnd('/');
        relativePath = relativePath.TrimStart('/');
        return $"{basePath}/{relativePath}";
    }

    public ValueTask DisposeAsync()
    {
        _ftpClient?.Dispose();
        _sftpClient?.Dispose();
        return ValueTask.CompletedTask;
    }
}
