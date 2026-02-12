using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Core.Persistence;

namespace Screener.Core.Settings;

/// <summary>
/// Implementation of settings service using SettingsRepository.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly SettingsRepository _repository;

    public SettingsService(ILogger<SettingsService> logger, SettingsRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var deviceId = await _repository.GetAsync<string>(SettingsKeys.SelectedDeviceId, ct);
        var connectorValue = await _repository.GetAsync(SettingsKeys.SelectedConnector, 1, ct); // Default to SDI (1)
        var recordingPath = await _repository.GetAsync<string>(SettingsKeys.DefaultRecordingPath, ct);
        var preferHardware = await _repository.GetAsync(SettingsKeys.PreferHardwareEncoding, true, ct);

        // Streaming
        var enableStreaming = await _repository.GetAsync(SettingsKeys.EnableStreaming, false, ct);
        var streamingPort = await _repository.GetAsync(SettingsKeys.StreamingPort, 8080, ct);
        var maxViewers = await _repository.GetAsync(SettingsKeys.MaxViewers, 10, ct);
        var requireAccessToken = await _repository.GetAsync(SettingsKeys.RequireAccessToken, false, ct);
        var accessToken = await _repository.GetAsync<string>(SettingsKeys.AccessToken, ct);
        var streamingResolution = await _repository.GetAsync<string>(SettingsKeys.StreamingResolution, ct);
        var streamingBitrate = await _repository.GetAsync(SettingsKeys.StreamingBitrate, 2500, ct);

        // NDI
        var enableNdiDiscovery = await _repository.GetAsync(SettingsKeys.NdiEnableDiscovery, true, ct);
        var enableNdiOutput = await _repository.GetAsync(SettingsKeys.NdiEnableOutput, false, ct);
        var ndiOutputSourceName = await _repository.GetAsync<string>(SettingsKeys.NdiOutputSourceName, ct);

        // SRT
        var enableSrtOutput = await _repository.GetAsync(SettingsKeys.SrtEnableOutput, false, ct);
        var srtOutputMode = await _repository.GetAsync<string>(SettingsKeys.SrtOutputMode, ct);
        var srtOutputAddress = await _repository.GetAsync<string>(SettingsKeys.SrtOutputAddress, ct);
        var srtOutputPort = await _repository.GetAsync(SettingsKeys.SrtOutputPort, 9000, ct);
        var srtOutputLatency = await _repository.GetAsync(SettingsKeys.SrtOutputLatency, 120, ct);
        var srtOutputBitrate = await _repository.GetAsync(SettingsKeys.SrtOutputBitrate, 5000, ct);
        var srtInputsJson = await _repository.GetAsync<string>(SettingsKeys.SrtInputs, ct);

        return new AppSettings
        {
            SelectedDeviceId = deviceId ?? string.Empty,
            SelectedConnector = (VideoConnector)connectorValue,
            DefaultRecordingPath = recordingPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            PreferHardwareEncoding = preferHardware,
            EnableStreaming = enableStreaming,
            StreamingPort = streamingPort,
            MaxViewers = maxViewers,
            RequireAccessToken = requireAccessToken,
            AccessToken = accessToken ?? string.Empty,
            StreamingResolution = streamingResolution ?? "720p",
            StreamingBitrate = streamingBitrate,
            EnableNdiDiscovery = enableNdiDiscovery,
            EnableNdiOutput = enableNdiOutput,
            NdiOutputSourceName = ndiOutputSourceName ?? "Screener Output",
            EnableSrtOutput = enableSrtOutput,
            SrtOutputMode = srtOutputMode ?? "caller",
            SrtOutputAddress = srtOutputAddress ?? string.Empty,
            SrtOutputPort = srtOutputPort,
            SrtOutputLatency = srtOutputLatency,
            SrtOutputBitrate = srtOutputBitrate,
            SrtInputsJson = srtInputsJson ?? "[]"
        };
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _repository.SetAsync(SettingsKeys.SelectedDeviceId, settings.SelectedDeviceId, ct);
        await _repository.SetAsync(SettingsKeys.SelectedConnector, (int)settings.SelectedConnector, ct);
        await _repository.SetAsync(SettingsKeys.DefaultRecordingPath, settings.DefaultRecordingPath, ct);
        await _repository.SetAsync(SettingsKeys.PreferHardwareEncoding, settings.PreferHardwareEncoding, ct);

        // Streaming
        await _repository.SetAsync(SettingsKeys.EnableStreaming, settings.EnableStreaming, ct);
        await _repository.SetAsync(SettingsKeys.StreamingPort, settings.StreamingPort, ct);
        await _repository.SetAsync(SettingsKeys.MaxViewers, settings.MaxViewers, ct);
        await _repository.SetAsync(SettingsKeys.RequireAccessToken, settings.RequireAccessToken, ct);
        await _repository.SetAsync(SettingsKeys.AccessToken, settings.AccessToken, ct);
        await _repository.SetAsync(SettingsKeys.StreamingResolution, settings.StreamingResolution, ct);
        await _repository.SetAsync(SettingsKeys.StreamingBitrate, settings.StreamingBitrate, ct);

        // NDI
        await _repository.SetAsync(SettingsKeys.NdiEnableDiscovery, settings.EnableNdiDiscovery, ct);
        await _repository.SetAsync(SettingsKeys.NdiEnableOutput, settings.EnableNdiOutput, ct);
        await _repository.SetAsync(SettingsKeys.NdiOutputSourceName, settings.NdiOutputSourceName, ct);

        // SRT
        await _repository.SetAsync(SettingsKeys.SrtEnableOutput, settings.EnableSrtOutput, ct);
        await _repository.SetAsync(SettingsKeys.SrtOutputMode, settings.SrtOutputMode, ct);
        await _repository.SetAsync(SettingsKeys.SrtOutputAddress, settings.SrtOutputAddress, ct);
        await _repository.SetAsync(SettingsKeys.SrtOutputPort, settings.SrtOutputPort, ct);
        await _repository.SetAsync(SettingsKeys.SrtOutputLatency, settings.SrtOutputLatency, ct);
        await _repository.SetAsync(SettingsKeys.SrtOutputBitrate, settings.SrtOutputBitrate, ct);
        await _repository.SetAsync(SettingsKeys.SrtInputs, settings.SrtInputsJson, ct);

        _logger.LogInformation("Settings saved");
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        return await _repository.GetAsync(key, defaultValue, ct);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await _repository.SetAsync(key, value, ct);
    }
}
