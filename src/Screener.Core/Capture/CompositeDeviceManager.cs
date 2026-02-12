using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;

namespace Screener.Core.Capture;

/// <summary>
/// Aggregates multiple IDeviceManager implementations (DeckLink, NDI, SRT)
/// into a single unified device manager.
/// </summary>
public sealed class CompositeDeviceManager : IDeviceManager
{
    private readonly ILogger<CompositeDeviceManager> _logger;
    private readonly IDeviceManager[] _managers;
    private List<ICaptureDevice> _devices = new();

    public IReadOnlyList<ICaptureDevice> AvailableDevices => _devices;

    public event EventHandler<DeviceEventArgs>? DeviceArrived;
    public event EventHandler<DeviceEventArgs>? DeviceRemoved;

    public CompositeDeviceManager(ILogger<CompositeDeviceManager> logger, IEnumerable<IDeviceManager> managers)
    {
        _logger = logger;
        _managers = managers.ToArray();

        foreach (var mgr in _managers)
        {
            mgr.DeviceArrived += (_, e) => DeviceArrived?.Invoke(this, e);
            mgr.DeviceRemoved += (_, e) => DeviceRemoved?.Invoke(this, e);
        }
    }

    public void RefreshDevices()
    {
        var allDevices = new List<ICaptureDevice>();

        foreach (var mgr in _managers)
        {
            try
            {
                mgr.RefreshDevices();
                allDevices.AddRange(mgr.AvailableDevices);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh devices from {Manager}, skipping",
                    mgr.GetType().Name);
            }
        }

        _devices = allDevices;
        _logger.LogInformation("CompositeDeviceManager: {Count} total devices from {ManagerCount} managers",
            _devices.Count, _managers.Length);
    }

    public async Task<ICaptureDevice?> GetDeviceAsync(string deviceId)
    {
        foreach (var mgr in _managers)
        {
            try
            {
                var device = await mgr.GetDeviceAsync(deviceId);
                if (device != null) return device;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetDeviceAsync failed on {Manager} for {DeviceId}",
                    mgr.GetType().Name, deviceId);
            }
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var mgr in _managers)
        {
            try
            {
                mgr.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing {Manager}", mgr.GetType().Name);
            }
        }
    }
}
