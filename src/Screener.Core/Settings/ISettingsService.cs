using Screener.Abstractions.Capture;

namespace Screener.Core.Settings;

/// <summary>
/// Application settings model.
/// </summary>
public class AppSettings
{
    public string SelectedDeviceId { get; set; } = string.Empty;
    public VideoConnector SelectedConnector { get; set; } = VideoConnector.SDI;
    public string DefaultRecordingPath { get; set; } = string.Empty;
    public bool PreferHardwareEncoding { get; set; } = true;

    // Streaming
    public bool EnableStreaming { get; set; }
    public int StreamingPort { get; set; } = 8080;
    public int MaxViewers { get; set; } = 10;
    public bool RequireAccessToken { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string StreamingResolution { get; set; } = "720p";
    public int StreamingBitrate { get; set; } = 2500;

    // NDI
    public bool EnableNdiDiscovery { get; set; } = true;
    public bool EnableNdiOutput { get; set; }
    public string NdiOutputSourceName { get; set; } = "Screener Output";

    // SRT
    public bool EnableSrtOutput { get; set; }
    public string SrtOutputMode { get; set; } = "caller";
    public string SrtOutputAddress { get; set; } = string.Empty;
    public int SrtOutputPort { get; set; } = 9000;
    public int SrtOutputLatency { get; set; } = 120;
    public int SrtOutputBitrate { get; set; } = 5000;
    public string SrtInputsJson { get; set; } = "[]";
}

/// <summary>
/// Service for accessing application settings.
/// </summary>
public interface ISettingsService
{
    Task<AppSettings> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);
}
