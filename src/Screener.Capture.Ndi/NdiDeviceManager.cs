using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Capture.Ndi.Interop;

namespace Screener.Capture.Ndi;

/// <summary>
/// Discovers NDI sources on the network and exposes them as <see cref="ICaptureDevice"/> instances.
/// Uses the NDI Find API with periodic polling to detect sources that appear or disappear.
/// </summary>
public sealed class NdiDeviceManager : IDeviceManager
{
    private readonly ILogger<NdiDeviceManager> _logger;
    private readonly NdiRuntime _runtime;
    private readonly Dictionary<string, NdiCaptureDevice> _devices = new();
    private readonly object _lock = new();

    private IntPtr _findInstance = IntPtr.Zero;
    private Timer? _pollTimer;
    private bool _disposed;

    /// <summary>
    /// Polling interval for NDI source discovery.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

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

    public NdiDeviceManager(ILogger<NdiDeviceManager> logger, NdiRuntime runtime)
    {
        _logger = logger;
        _runtime = runtime;

        if (!_runtime.IsAvailable)
        {
            _logger.LogWarning("NDI runtime not available; NdiDeviceManager will return no devices");
            return;
        }

        if (!_runtime.Initialize())
        {
            _logger.LogError("NDI initialization failed; NdiDeviceManager will return no devices");
            return;
        }

        // Create the NDI finder
        var findSettings = new NDIlib_find_create_t
        {
            show_local_sources = true,
            p_groups = IntPtr.Zero,
            p_extra_ips = IntPtr.Zero
        };

        _findInstance = NdiInterop.NDIlib_find_create_v2(ref findSettings);
        if (_findInstance == IntPtr.Zero)
        {
            _logger.LogError("Failed to create NDI finder instance");
            return;
        }

        _logger.LogInformation("NDI finder created, starting source polling");

        // Start periodic polling for source changes
        _pollTimer = new Timer(OnPollTimerTick, null, TimeSpan.FromMilliseconds(500), PollInterval);
    }

    private void OnPollTimerTick(object? state)
    {
        if (_disposed || _findInstance == IntPtr.Zero) return;

        try
        {
            // Non-blocking check: did the source list change?
            // Use a short timeout so we don't block the timer thread
            NdiInterop.NDIlib_find_wait_for_sources(_findInstance, 0);

            var sourcesPtr = NdiInterop.NDIlib_find_get_current_sources(_findInstance, out var count);
            if (sourcesPtr == IntPtr.Zero || count == 0)
            {
                // No sources found; remove any previously discovered devices
                RemoveAllDevices();
                return;
            }

            var foundIds = new HashSet<string>();
            var structSize = Marshal.SizeOf<NDIlib_source_t>();

            for (uint i = 0; i < count; i++)
            {
                var sourcePtr = sourcesPtr + (int)(i * structSize);
                var source = Marshal.PtrToStructure<NDIlib_source_t>(sourcePtr);

                var name = Marshal.PtrToStringAnsi(source.p_ndi_name) ?? $"NDI Source {i}";
                var url = Marshal.PtrToStringAnsi(source.p_url_address) ?? string.Empty;
                var deviceId = $"ndi-{ComputeStableHash(name):x8}";

                foundIds.Add(deviceId);

                lock (_lock)
                {
                    if (!_devices.ContainsKey(deviceId))
                    {
                        var device = new NdiCaptureDevice(
                            deviceId,
                            name,
                            url,
                            _logger);

                        _devices[deviceId] = device;

                        _logger.LogInformation("NDI source discovered: {Name} ({Url}) -> {DeviceId}", name, url, deviceId);
                        DeviceArrived?.Invoke(this, new DeviceEventArgs { Device = device });
                    }
                }
            }

            // Remove devices that are no longer on the network
            RemoveDisconnectedDevices(foundIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling NDI sources");
        }
    }

    public void RefreshDevices()
    {
        if (!_runtime.IsAvailable || _findInstance == IntPtr.Zero)
        {
            _logger.LogDebug("NDI not available, RefreshDevices is a no-op");
            return;
        }

        _logger.LogInformation("Refreshing NDI sources...");

        // Force an immediate poll
        OnPollTimerTick(null);
    }

    public Task<ICaptureDevice?> GetDeviceAsync(string deviceId)
    {
        lock (_lock)
        {
            _devices.TryGetValue(deviceId, out var device);
            return Task.FromResult<ICaptureDevice?>(device);
        }
    }

    private void RemoveDisconnectedDevices(HashSet<string> foundIds)
    {
        lock (_lock)
        {
            var toRemove = _devices.Keys
                .Where(id => !foundIds.Contains(id))
                .ToList();

            foreach (var deviceId in toRemove)
            {
                if (_devices.TryGetValue(deviceId, out var device))
                {
                    _devices.Remove(deviceId);
                    _logger.LogInformation("NDI source removed: {DeviceName}", device.DisplayName);
                    DeviceRemoved?.Invoke(this, new DeviceEventArgs { Device = device });
                    device.Dispose();
                }
            }
        }
    }

    private void RemoveAllDevices()
    {
        lock (_lock)
        {
            if (_devices.Count == 0) return;

            foreach (var device in _devices.Values)
            {
                _logger.LogInformation("NDI source removed: {DeviceName}", device.DisplayName);
                DeviceRemoved?.Invoke(this, new DeviceEventArgs { Device = device });
                device.Dispose();
            }
            _devices.Clear();
        }
    }

    /// <summary>
    /// Computes a stable 32-bit hash of a string for use in device IDs.
    /// </summary>
    private static uint ComputeStableHash(string input)
    {
        // FNV-1a 32-bit hash
        uint hash = 2166136261;
        foreach (char c in input)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer?.Dispose();
        _pollTimer = null;

        lock (_lock)
        {
            foreach (var device in _devices.Values)
            {
                device.Dispose();
            }
            _devices.Clear();
        }

        if (_findInstance != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_find_destroy(_findInstance); } catch { }
            _findInstance = IntPtr.Zero;
        }
    }
}
