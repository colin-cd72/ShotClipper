using System.Text.Json;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Core.Settings;

namespace Screener.Capture.Srt;

/// <summary>
/// Device manager for SRT inputs. SRT inputs are user-configured (not auto-discovered).
/// Reads configuration from settings and creates SrtCaptureDevice instances.
/// </summary>
public sealed class SrtDeviceManager : IDeviceManager
{
    private const string SrtInputsSettingsKey = "srt.inputs";

    private readonly ILogger<SrtDeviceManager> _logger;
    private readonly ISettingsService _settingsService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Dictionary<string, SrtCaptureDevice> _devices = new();
    private readonly object _lock = new();
    private bool _disposed;

    public IReadOnlyList<ICaptureDevice> AvailableDevices
    {
        get
        {
            lock (_lock)
            {
                return _devices.Values.Cast<ICaptureDevice>().ToList();
            }
        }
    }

    public event EventHandler<DeviceEventArgs>? DeviceArrived;
    public event EventHandler<DeviceEventArgs>? DeviceRemoved;

    public SrtDeviceManager(
        ILogger<SrtDeviceManager> logger,
        ISettingsService settingsService,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _settingsService = settingsService;
        _loggerFactory = loggerFactory;
    }

    public void RefreshDevices()
    {
        _logger.LogInformation("Refreshing SRT devices from settings...");

        try
        {
            // Read SRT input configurations from settings (JSON array)
            var json = _settingsService
                .GetAsync(SrtInputsSettingsKey, string.Empty)
                .GetAwaiter().GetResult();

            var configs = new List<SrtInputConfig>();

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<SrtInputConfig>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (parsed != null)
                        configs = parsed;
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse SRT inputs JSON: {Json}", json);
                }
            }

            var foundDeviceIds = new HashSet<string>();

            foreach (var config in configs)
            {
                var deviceId = $"srt-{config.Port}";
                foundDeviceIds.Add(deviceId);

                lock (_lock)
                {
                    if (!_devices.ContainsKey(deviceId))
                    {
                        var displayName = !string.IsNullOrWhiteSpace(config.Name)
                            ? config.Name
                            : $"SRT :{config.Port}";

                        var device = new SrtCaptureDevice(
                            deviceId,
                            displayName,
                            config,
                            _loggerFactory.CreateLogger<SrtCaptureDevice>());

                        _devices[deviceId] = device;
                        DeviceArrived?.Invoke(this, new DeviceEventArgs { Device = device });
                        _logger.LogInformation("Added SRT device: {DeviceName} (port {Port})",
                            displayName, config.Port);
                    }
                }
            }

            // Remove devices that are no longer configured
            RemoveOldDevices(foundDeviceIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh SRT devices");
        }
    }

    private void RemoveOldDevices(HashSet<string> foundDeviceIds)
    {
        lock (_lock)
        {
            var toRemove = _devices.Keys
                .Where(id => !foundDeviceIds.Contains(id))
                .ToList();

            foreach (var deviceId in toRemove)
            {
                if (_devices.TryGetValue(deviceId, out var device))
                {
                    _devices.Remove(deviceId);
                    DeviceRemoved?.Invoke(this, new DeviceEventArgs { Device = device });
                    _logger.LogInformation("Removed SRT device: {DeviceName}", device.DisplayName);
                    device.Dispose();
                }
            }
        }
    }

    public Task<ICaptureDevice?> GetDeviceAsync(string deviceId)
    {
        lock (_lock)
        {
            _devices.TryGetValue(deviceId, out var device);
            return Task.FromResult<ICaptureDevice?>(device);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var device in _devices.Values)
            {
                device.Dispose();
            }
            _devices.Clear();
        }
    }
}
