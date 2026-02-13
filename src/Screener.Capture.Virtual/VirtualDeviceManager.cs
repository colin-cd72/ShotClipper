using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Core.Settings;

namespace Screener.Capture.Virtual;

/// <summary>
/// Device manager for virtual input sources (Black, Colors, Still Images).
/// Always includes a Black source. Color and still image sources are persisted via ISettingsService.
/// </summary>
public sealed class VirtualDeviceManager : IDeviceManager
{
    private const string StillImagesSettingsKey = "virtual.stillimages";
    private const string ColorsSettingsKey = "virtual.colors";

    private readonly ILogger<VirtualDeviceManager> _logger;
    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, ICaptureDevice> _devices = new();
    private readonly object _lock = new();
    private bool _disposed;

    public IReadOnlyList<ICaptureDevice> AvailableDevices
    {
        get
        {
            lock (_lock)
            {
                return _devices.Values.ToList();
            }
        }
    }

    public event EventHandler<DeviceEventArgs>? DeviceArrived;
    public event EventHandler<DeviceEventArgs>? DeviceRemoved;

    public VirtualDeviceManager(
        ILogger<VirtualDeviceManager> logger,
        ISettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
    }

    public void RefreshDevices()
    {
        _logger.LogInformation("Refreshing virtual devices...");

        try
        {
            var foundDeviceIds = new HashSet<string>();

            // Always include the Black device
            const string blackId = "virtual-black";
            foundDeviceIds.Add(blackId);

            lock (_lock)
            {
                if (!_devices.ContainsKey(blackId))
                {
                    var blackDevice = new BlackCaptureDevice();
                    _devices[blackId] = blackDevice;
                    DeviceArrived?.Invoke(this, new DeviceEventArgs { Device = blackDevice });
                    _logger.LogInformation("Added virtual device: Black");
                }
            }

            // Read color configurations from settings
            var colorJson = _settingsService
                .GetAsync(ColorsSettingsKey, string.Empty)
                .GetAwaiter().GetResult();

            var colorConfigs = new List<ColorConfig>();
            if (!string.IsNullOrWhiteSpace(colorJson))
            {
                try
                {
                    colorConfigs = JsonSerializer.Deserialize<List<ColorConfig>>(colorJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new List<ColorConfig>();
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse virtual colors JSON: {Json}", colorJson);
                }
            }

            foreach (var colorCfg in colorConfigs)
            {
                var deviceId = GetColorDeviceId(colorCfg.Name, colorCfg.R, colorCfg.G, colorCfg.B);
                foundDeviceIds.Add(deviceId);

                lock (_lock)
                {
                    if (!_devices.ContainsKey(deviceId))
                    {
                        var device = new ColorCaptureDevice(deviceId, colorCfg.Name, colorCfg.R, colorCfg.G, colorCfg.B);
                        _devices[deviceId] = device;
                        DeviceArrived?.Invoke(this, new DeviceEventArgs { Device = device });
                        _logger.LogInformation("Added virtual color device: {Name} (#{R:X2}{G:X2}{B:X2})",
                            colorCfg.Name, colorCfg.R, colorCfg.G, colorCfg.B);
                    }
                }
            }

            // Read still image configurations from settings
            var json = _settingsService
                .GetAsync(StillImagesSettingsKey, string.Empty)
                .GetAwaiter().GetResult();

            var configs = new List<StillImageConfig>();

            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<StillImageConfig>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (parsed != null)
                        configs = parsed;
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse virtual still images JSON: {Json}", json);
                }
            }

            foreach (var config in configs)
            {
                var deviceId = GetStillImageDeviceId(config.ImagePath);
                foundDeviceIds.Add(deviceId);

                lock (_lock)
                {
                    if (!_devices.ContainsKey(deviceId))
                    {
                        var displayName = !string.IsNullOrWhiteSpace(config.Name)
                            ? config.Name
                            : Path.GetFileNameWithoutExtension(config.ImagePath);

                        var device = new StillImageCaptureDevice(deviceId, displayName, config.ImagePath);
                        _devices[deviceId] = device;
                        DeviceArrived?.Invoke(this, new DeviceEventArgs { Device = device });
                        _logger.LogInformation("Added virtual still image device: {Name} ({Path})",
                            displayName, config.ImagePath);
                    }
                }
            }

            // Remove devices that are no longer configured (except Black)
            RemoveOldDevices(foundDeviceIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh virtual devices");
        }
    }

    /// <summary>
    /// Add a still image source. Persists to settings and refreshes devices.
    /// </summary>
    public async Task AddStillImageAsync(string name, string imagePath)
    {
        var json = await _settingsService.GetAsync(StillImagesSettingsKey, string.Empty);
        var configs = new List<StillImageConfig>();

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                configs = JsonSerializer.Deserialize<List<StillImageConfig>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<StillImageConfig>();
            }
            catch (JsonException)
            {
                configs = new List<StillImageConfig>();
            }
        }

        configs.Add(new StillImageConfig(name, imagePath));

        var updatedJson = JsonSerializer.Serialize(configs);
        await _settingsService.SetAsync(StillImagesSettingsKey, updatedJson);

        RefreshDevices();
        _logger.LogInformation("Added still image source: {Name} ({Path})", name, imagePath);
    }

    /// <summary>
    /// Remove a still image source by device ID. Persists to settings and refreshes devices.
    /// </summary>
    public async Task RemoveStillImageAsync(string deviceId)
    {
        var json = await _settingsService.GetAsync(StillImagesSettingsKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json)) return;

        var configs = JsonSerializer.Deserialize<List<StillImageConfig>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<StillImageConfig>();

        configs.RemoveAll(c => GetStillImageDeviceId(c.ImagePath) == deviceId);

        var updatedJson = JsonSerializer.Serialize(configs);
        await _settingsService.SetAsync(StillImagesSettingsKey, updatedJson);

        RefreshDevices();
        _logger.LogInformation("Removed still image source: {DeviceId}", deviceId);
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
                    _logger.LogInformation("Removed virtual device: {DeviceName}", device.DisplayName);
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

    /// <summary>
    /// Add a color source. Persists to settings and refreshes devices.
    /// </summary>
    public async Task AddColorSourceAsync(string name, byte r, byte g, byte b)
    {
        var json = await _settingsService.GetAsync(ColorsSettingsKey, string.Empty);
        var configs = new List<ColorConfig>();

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                configs = JsonSerializer.Deserialize<List<ColorConfig>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<ColorConfig>();
            }
            catch (JsonException)
            {
                configs = new List<ColorConfig>();
            }
        }

        configs.Add(new ColorConfig(name, r, g, b));

        var updatedJson = JsonSerializer.Serialize(configs);
        await _settingsService.SetAsync(ColorsSettingsKey, updatedJson);

        RefreshDevices();
        _logger.LogInformation("Added color source: {Name} (#{R:X2}{G:X2}{B:X2})", name, r, g, b);
    }

    /// <summary>
    /// Remove a color source by device ID. Persists to settings and refreshes devices.
    /// </summary>
    public async Task RemoveColorSourceAsync(string deviceId)
    {
        var json = await _settingsService.GetAsync(ColorsSettingsKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json)) return;

        var configs = JsonSerializer.Deserialize<List<ColorConfig>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<ColorConfig>();

        configs.RemoveAll(c => GetColorDeviceId(c.Name, c.R, c.G, c.B) == deviceId);

        var updatedJson = JsonSerializer.Serialize(configs);
        await _settingsService.SetAsync(ColorsSettingsKey, updatedJson);

        RefreshDevices();
        _logger.LogInformation("Removed color source: {DeviceId}", deviceId);
    }

    private static string GetColorDeviceId(string name, byte r, byte g, byte b)
    {
        var key = $"{name}-{r:X2}{g:X2}{b:X2}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"virtual-color-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    /// <summary>
    /// Generate a stable device ID from the image file path.
    /// </summary>
    private static string GetStillImageDeviceId(string imagePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(imagePath));
        return $"virtual-still-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
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
