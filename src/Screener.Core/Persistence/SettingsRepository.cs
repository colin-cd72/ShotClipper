using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Screener.Core.Persistence;

/// <summary>
/// Repository for application settings persistence.
/// </summary>
public sealed class SettingsRepository
{
    private readonly ILogger<SettingsRepository> _logger;
    private readonly DatabaseContext _db;

    public SettingsRepository(ILogger<SettingsRepository> logger, DatabaseContext db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// Get a setting value.
    /// </summary>
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        const string sql = "SELECT value FROM settings WHERE key = @Key";

        var value = await _db.QuerySingleOrDefaultAsync<string>(sql, new { Key = key });

        if (value == null)
            return default;

        return JsonSerializer.Deserialize<T>(value);
    }

    /// <summary>
    /// Get a setting value with a default.
    /// </summary>
    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        var value = await GetAsync<T>(key, ct);
        return value ?? defaultValue;
    }

    /// <summary>
    /// Set a setting value.
    /// </summary>
    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO settings (key, value, updated_at)
            VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = @Value,
                updated_at = @UpdatedAt
            """;

        var json = JsonSerializer.Serialize(value);

        await _db.ExecuteAsync(sql, new
        {
            Key = key,
            Value = json,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        });

        _logger.LogDebug("Set setting: {Key}", key);
    }

    /// <summary>
    /// Delete a setting.
    /// </summary>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM settings WHERE key = @Key";

        await _db.ExecuteAsync(sql, new { Key = key });

        _logger.LogDebug("Deleted setting: {Key}", key);
    }

    /// <summary>
    /// Get all settings.
    /// </summary>
    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT key, value FROM settings";

        var rows = await _db.QueryAsync<(string key, string value)>(sql);
        return rows.ToDictionary(r => r.key, r => r.value);
    }
}

/// <summary>
/// Well-known settings keys.
/// </summary>
public static class SettingsKeys
{
    // General
    public const string StartWithWindows = "general.startWithWindows";
    public const string MinimizeToTray = "general.minimizeToTray";
    public const string ShowNotifications = "general.showNotifications";
    public const string DefaultRecordingPath = "general.defaultRecordingPath";
    public const string FilenameTemplate = "general.filenameTemplate";

    // Video
    public const string SelectedDeviceId = "video.selectedDeviceId";
    public const string SelectedConnector = "video.selectedConnector";
    public const string SelectedVideoMode = "video.selectedVideoMode";
    public const string SelectedPreset = "video.selectedPreset";
    public const string PreferHardwareEncoding = "video.preferHardwareEncoding";

    // Audio
    public const string AudioChannels = "audio.channels";
    public const string AudioSampleRate = "audio.sampleRate";
    public const string EnableAudioPreview = "audio.enablePreview";
    public const string AudioOutputDevice = "audio.outputDevice";
    public const string MeterType = "audio.meterType";
    public const string ReferenceLevel = "audio.referenceLevel";

    // Timecode
    public const string TimecodeSource = "timecode.source";
    public const string NtpServer = "timecode.ntpServer";
    public const string AutoSyncNtp = "timecode.autoSync";
    public const string Timezone = "timecode.timezone";
    public const string UseDropFrame = "timecode.useDropFrame";

    // Streaming
    public const string EnableStreaming = "streaming.enabled";
    public const string StreamingPort = "streaming.port";
    public const string MaxViewers = "streaming.maxViewers";
    public const string RequireAccessToken = "streaming.requireToken";
    public const string AccessToken = "streaming.accessToken";
    public const string StreamingResolution = "streaming.resolution";
    public const string StreamingBitrate = "streaming.bitrate";

    // NDI
    public const string NdiEnableDiscovery = "ndi.enableDiscovery";
    public const string NdiEnableOutput = "ndi.enableOutput";
    public const string NdiOutputSourceName = "ndi.outputSourceName";

    // SRT
    public const string SrtEnableOutput = "srt.enableOutput";
    public const string SrtOutputMode = "srt.outputMode";
    public const string SrtOutputAddress = "srt.outputAddress";
    public const string SrtOutputPort = "srt.outputPort";
    public const string SrtOutputLatency = "srt.outputLatency";
    public const string SrtOutputBitrate = "srt.outputBitrate";
    public const string SrtInputs = "srt.inputs";

    // Upload
    public const string AutoUpload = "upload.autoUpload";
    public const string AutoUploadClips = "upload.autoUploadClips";
    public const string MaxConcurrentUploads = "upload.maxConcurrent";
    public const string DefaultUploadProvider = "upload.defaultProvider";

    // Golf
    public const string GolfAutoUpload = "golf.autoUpload";
    public const string GolfAutoUploadProvider = "golf.autoUploadProvider";
    public const string GolfAutoUploadRemotePath = "golf.autoUploadRemotePath";

    // API
    public const string ApiKey = "api.key";

    // UI
    public const string WindowState = "ui.windowState";
    public const string WindowBounds = "ui.windowBounds";
    public const string LeftPanelWidth = "ui.leftPanelWidth";
    public const string RightPanelWidth = "ui.rightPanelWidth";
    public const string ShowTimecodeOverlay = "ui.showTimecodeOverlay";
    public const string ShowSafeAreas = "ui.showSafeAreas";
}
