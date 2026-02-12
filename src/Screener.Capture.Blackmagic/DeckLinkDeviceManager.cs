using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Capture.Blackmagic.Interop;

namespace Screener.Capture.Blackmagic;

/// <summary>
/// Manages discovery and lifecycle of Blackmagic DeckLink devices.
/// </summary>
public sealed class DeckLinkDeviceManager : IDeviceManager
{
    private const string BlackmagicDesktopVideoPath = @"C:\Program Files\Blackmagic Design\Blackmagic Desktop Video";
    private const string DeckLinkApiDll = "DeckLinkAPI64.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    // DllGetClassObject delegate
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObjectDelegate(
        [In] ref Guid rclsid,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

    private static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid CLSID_CDeckLinkIterator = new(DeckLinkGuid.CDeckLinkIterator);
    private static readonly Guid CLSID_CDeckLinkDiscovery = new(DeckLinkGuid.CDeckLinkDiscovery);

    private static IntPtr _decklinkModule = IntPtr.Zero;

    private readonly ILogger<DeckLinkDeviceManager> _logger;
    private readonly Dictionary<string, DeckLinkCaptureDevice> _devices = new();
    private readonly object _lock = new();
    private bool _disposed;
    private bool _sdkAvailable;

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

    public DeckLinkDeviceManager(ILogger<DeckLinkDeviceManager> logger)
    {
        _logger = logger;
        _sdkAvailable = CheckSdkAvailable();
    }

    private bool CheckSdkAvailable()
    {
        try
        {
            // Initialize COM
            CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED

            // First, try standard COM activation (works if SDK is registered)
            var iteratorType = Type.GetTypeFromCLSID(CLSID_CDeckLinkIterator);
            if (iteratorType != null)
            {
                try
                {
                    var iterator = Activator.CreateInstance(iteratorType) as IDeckLinkIterator;
                    if (iterator != null)
                    {
                        Marshal.ReleaseComObject(iterator);
                        _logger.LogInformation("DeckLink SDK detected via COM registration");
                        return true;
                    }
                }
                catch (COMException)
                {
                    // COM registration failed, try direct DLL loading
                }
            }

            // Try direct DLL loading (registration-free approach)
            return TryLoadDeckLinkDirect();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeckLink SDK not available");
        }

        return false;
    }

    private bool TryLoadDeckLinkDirect()
    {
        var dllPath = Path.Combine(BlackmagicDesktopVideoPath, DeckLinkApiDll);

        if (!File.Exists(dllPath))
        {
            _logger.LogWarning("DeckLink API DLL not found at: {Path}", dllPath);
            return false;
        }

        _logger.LogDebug("Attempting to load DeckLink API from: {Path}", dllPath);

        // Set DLL directory for dependencies
        SetDllDirectory(BlackmagicDesktopVideoPath);

        // Load the DLL
        _decklinkModule = LoadLibrary(dllPath);
        if (_decklinkModule == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("Failed to load DeckLink DLL, error: {Error}", error);
            return false;
        }

        // Get DllGetClassObject export
        var procAddress = GetProcAddress(_decklinkModule, "DllGetClassObject");
        if (procAddress == IntPtr.Zero)
        {
            _logger.LogWarning("DllGetClassObject not found in DeckLink DLL");
            return false;
        }

        var dllGetClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(procAddress);

        // Try iterator CLSID first (older SDK)
        var clsid = CLSID_CDeckLinkIterator;
        var iid = IID_IClassFactory;
        var hr = dllGetClassObject(ref clsid, ref iid, out var classFactoryObj);

        if (hr != 0)
        {
            _logger.LogDebug("CDeckLinkIterator not available (0x{HR:X8}), trying CDeckLinkDiscovery...", hr);

            // Try discovery CLSID (newer SDK 10.x+)
            clsid = CLSID_CDeckLinkDiscovery;
            hr = dllGetClassObject(ref clsid, ref iid, out classFactoryObj);

            if (hr != 0)
            {
                _logger.LogWarning("Neither CDeckLinkIterator nor CDeckLinkDiscovery available. HRESULT: 0x{HR:X8}", hr);
                return false;
            }

            _logger.LogInformation("Using DeckLink Discovery API (SDK 10.x+)");
        }

        // Create instance via class factory
        try
        {
            var classFactory = (IClassFactory)classFactoryObj;

            // Try to create iterator first
            if (clsid == CLSID_CDeckLinkIterator)
            {
                var iidIterator = new Guid(DeckLinkGuid.IDeckLinkIterator);
                classFactory.CreateInstance(null, ref iidIterator, out var iteratorObj);

                if (iteratorObj is IDeckLinkIterator iterator)
                {
                    Marshal.ReleaseComObject(iterator);
                    _logger.LogInformation("DeckLink SDK loaded via direct DLL loading (Iterator API)");
                    return true;
                }
            }
            else // Discovery API
            {
                var iidDiscovery = new Guid(DeckLinkGuid.IDeckLinkDiscovery);
                classFactory.CreateInstance(null, ref iidDiscovery, out var discoveryObj);

                if (discoveryObj is IDeckLinkDiscovery discovery)
                {
                    Marshal.ReleaseComObject(discovery);
                    _logger.LogInformation("DeckLink SDK loaded via direct DLL loading (Discovery API)");
                    return true;
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(classFactoryObj);
        }

        return false;
    }

    [ComImport]
    [Guid("00000001-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(
            [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
    }

    public void RefreshDevices()
    {
        _logger.LogInformation("Refreshing DeckLink devices...");

        if (_sdkAvailable)
        {
            RefreshRealDevices();
        }
        else
        {
            RefreshSimulatedDevices();
        }
    }

    private void RefreshRealDevices()
    {
        try
        {
            var iteratorType = Type.GetTypeFromCLSID(new Guid(DeckLinkGuid.CDeckLinkIterator));
            if (iteratorType == null) return;

            var iterator = Activator.CreateInstance(iteratorType) as IDeckLinkIterator;
            if (iterator == null) return;

            try
            {
                var foundDeviceIds = new HashSet<string>();
                int deviceIndex = 0;

                while (iterator.Next(out var deckLink) == DeckLinkHResult.S_OK && deckLink != null)
                {
                    try
                    {
                        deckLink.GetDisplayName(out var displayName);
                        var deviceId = $"decklink-{deviceIndex}";

                        foundDeviceIds.Add(deviceId);

                        lock (_lock)
                        {
                            if (!_devices.ContainsKey(deviceId))
                            {
                                // Query for input interface
                                var deckLinkInput = deckLink as IDeckLinkInput;
                                if (deckLinkInput != null)
                                {
                                    var device = new DeckLinkCaptureDevice(
                                        deviceId,
                                        displayName,
                                        deckLink,
                                        deckLinkInput,
                                        _logger);

                                    _devices[deviceId] = device;
                                    DeviceArrived?.Invoke(this, new DeviceEventArgs { Device = device });
                                    _logger.LogInformation("Added device: {DeviceName}", displayName);
                                }
                                else
                                {
                                    _logger.LogDebug("Device {DeviceName} does not support input", displayName);
                                    Marshal.ReleaseComObject(deckLink);
                                }
                            }
                        }

                        deviceIndex++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing DeckLink device");
                        Marshal.ReleaseComObject(deckLink);
                    }
                }

                // Remove devices that are no longer present
                RemoveDisconnectedDevices(foundDeviceIds);
            }
            finally
            {
                Marshal.ReleaseComObject(iterator);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate DeckLink devices");
            // Fall back to simulation if real enumeration fails
            _sdkAvailable = false;
            RefreshSimulatedDevices();
        }
    }

    private void RefreshSimulatedDevices()
    {
        lock (_lock)
        {
            if (!_devices.ContainsKey("simulated-decklink-1"))
            {
                var device = new DeckLinkCaptureDevice(
                    "simulated-decklink-1",
                    "DeckLink Mini Recorder (Simulated)",
                    _logger);

                _devices[device.DeviceId] = device;
                DeviceArrived?.Invoke(this, new DeviceEventArgs { Device = device });
                _logger.LogInformation("Added simulated device: {DeviceName}", device.DisplayName);
            }
        }
    }

    private void RemoveDisconnectedDevices(HashSet<string> foundDeviceIds)
    {
        lock (_lock)
        {
            var toRemove = _devices.Keys
                .Where(id => !id.StartsWith("simulated-") && !foundDeviceIds.Contains(id))
                .ToList();

            foreach (var deviceId in toRemove)
            {
                if (_devices.TryGetValue(deviceId, out var device))
                {
                    _devices.Remove(deviceId);
                    DeviceRemoved?.Invoke(this, new DeviceEventArgs { Device = device });
                    _logger.LogInformation("Removed device: {DeviceName}", device.DisplayName);
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

/// <summary>
/// Represents a single DeckLink capture device.
/// </summary>
public sealed class DeckLinkCaptureDevice : ICaptureDevice, IDeckLinkInputCallback
{
    private readonly ILogger _logger;
    private readonly IDeckLink? _deckLink;
    private readonly IDeckLinkInput? _deckLinkInput;
    private readonly IDeckLinkConfiguration? _deckLinkConfiguration;
    private readonly IDeckLinkProfileAttributes? _deckLinkAttributes;
    private readonly bool _isSimulated;
    private readonly List<VideoMode> _supportedModes;
    private readonly List<VideoConnector> _availableConnectors;
    private VideoConnector _selectedConnector = VideoConnector.SDI;
    private DeviceStatus _status = DeviceStatus.Idle;
    private VideoMode? _currentMode;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private long _frameCount;
    private bool _disposed;
    private BMDPixelFormat _capturePixelFormat = BMDPixelFormat.bmdFormat8BitYUV;  // UYVY format - native hardware format
    private int _audioChannels = 16;
    private BMDDisplayMode _pendingFormatChange = BMDDisplayMode.bmdModeUnknown;
    private bool _formatChangeInProgress = false;
    private IntPtr _unmanagedBuffer = IntPtr.Zero; // Reusable unmanaged buffer
    private byte[]? _lastGoodFrameCache; // Cache last successful frame to avoid flashing on partial copies
    private int _unmanagedBufferSize = 0;

    // Ring buffer for immediate frame capture (avoids DMA buffer recycling issues)
    private const int RingBufferSlots = 3;
    private byte[][]? _ringBuffer;
    private int _ringBufferWriteIndex = 0;
    private readonly object _ringBufferLock = new();

    // Pooled event buffers to avoid 4MB allocation per frame (reduces GC pressure)
    private const int EventBufferPoolSize = 3;
    private byte[][]? _eventBufferPool;
    private int _eventBufferIndex = 0;

    // Native DLL imports for frame data access
    // These bypass COM interop marshaling issues by calling native code directly
    [DllImport("Screener.Capture.Blackmagic.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CopyDeckLinkFrameBytes(IntPtr framePtr, IntPtr buffer, int bufferSize);

    [DllImport("Screener.Capture.Blackmagic.Native.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetDeckLinkFrameInfo(IntPtr framePtr, out int width, out int height, out int rowBytes, out uint flags);

    public string DeviceId { get; }
    public string DisplayName { get; }
    public DeviceStatus Status => _status;
    public VideoMode? CurrentVideoMode => _currentMode;
    public IReadOnlyList<VideoMode> SupportedVideoModes => _supportedModes;
    public IReadOnlyList<VideoConnector> AvailableConnectors => _availableConnectors;

    public VideoConnector SelectedConnector
    {
        get => _selectedConnector;
        set
        {
            if (_availableConnectors.Contains(value))
            {
                _selectedConnector = value;
                ApplyConnectorSelection();
            }
        }
    }

    private void ApplyConnectorSelection()
    {
        if (_isSimulated || _deckLinkConfiguration == null) return;

        try
        {
            var bmdConnection = _selectedConnector switch
            {
                VideoConnector.SDI => BMDVideoConnection.bmdVideoConnectionSDI,
                VideoConnector.HDMI => BMDVideoConnection.bmdVideoConnectionHDMI,
                VideoConnector.OpticalSDI => BMDVideoConnection.bmdVideoConnectionOpticalSDI,
                VideoConnector.Component => BMDVideoConnection.bmdVideoConnectionComponent,
                VideoConnector.Composite => BMDVideoConnection.bmdVideoConnectionComposite,
                VideoConnector.SVideo => BMDVideoConnection.bmdVideoConnectionSVideo,
                _ => BMDVideoConnection.bmdVideoConnectionSDI
            };

            _logger.LogInformation("Setting video input connector to {Connector} (BMD value: {BMDValue})",
                _selectedConnector, bmdConnection);

            var hr = _deckLinkConfiguration.SetInt(
                BMDDeckLinkConfigurationID.bmdDeckLinkConfigVideoInputConnection,
                (long)bmdConnection);

            if (hr == DeckLinkHResult.S_OK)
            {
                _logger.LogInformation("Set video input connector to {Connector}", _selectedConnector);

                // Verify the setting was applied
                hr = _deckLinkConfiguration.GetInt(
                    BMDDeckLinkConfigurationID.bmdDeckLinkConfigVideoInputConnection,
                    out var currentConnection);

                if (hr == DeckLinkHResult.S_OK)
                {
                    _logger.LogInformation("Verified current connector setting: 0x{Value:X8}", currentConnection);
                }
            }
            else
            {
                _logger.LogWarning("Failed to set video input connector: 0x{ErrorCode:X8}", hr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting video input connector");
        }
    }

    public event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;
    public event EventHandler<AudioSamplesEventArgs>? AudioSamplesReceived;
    public event EventHandler<DeviceStatusChangedEventArgs>? StatusChanged;

    // Constructor for real DeckLink device
    public DeckLinkCaptureDevice(
        string deviceId,
        string displayName,
        IDeckLink deckLink,
        IDeckLinkInput deckLinkInput,
        ILogger logger)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        _deckLink = deckLink;
        _deckLinkInput = deckLinkInput;
        _logger = logger;
        _isSimulated = false;

        // Query for configuration interface via COM QueryInterface
        try
        {
            _deckLinkConfiguration = (IDeckLinkConfiguration)deckLink;
            _logger.LogDebug("IDeckLinkConfiguration obtained successfully");
        }
        catch (InvalidCastException)
        {
            _logger.LogWarning("Device does not support IDeckLinkConfiguration");
            _deckLinkConfiguration = null;
        }

        // Query for profile attributes interface
        try
        {
            _deckLinkAttributes = (IDeckLinkProfileAttributes)deckLink;
            _logger.LogDebug("IDeckLinkProfileAttributes obtained successfully");
        }
        catch (InvalidCastException)
        {
            _logger.LogWarning("Device does not support IDeckLinkProfileAttributes");
            _deckLinkAttributes = null;
        }

        _supportedModes = EnumerateSupportedModes();
        _availableConnectors = EnumerateAvailableConnectors();

        // Set default connector if available
        if (_availableConnectors.Count > 0)
        {
            _selectedConnector = _availableConnectors[0];
        }
    }

    // Constructor for simulated device
    public DeckLinkCaptureDevice(string deviceId, string displayName, ILogger logger)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        _logger = logger;
        _isSimulated = true;

        _supportedModes = GetSimulatedModes();
        _availableConnectors = new List<VideoConnector> { VideoConnector.SDI, VideoConnector.HDMI };
        _selectedConnector = VideoConnector.HDMI;
    }

    private List<VideoMode> EnumerateSupportedModes()
    {
        var modes = new List<VideoMode>();

        if (_deckLinkInput == null) return modes;

        try
        {
            if (_deckLinkInput.GetDisplayModeIterator(out var iterator) == DeckLinkHResult.S_OK && iterator != null)
            {
                try
                {
                    while (iterator.Next(out var displayMode) == DeckLinkHResult.S_OK && displayMode != null)
                    {
                        try
                        {
                            displayMode.GetName(out var name);
                            var width = displayMode.GetWidth();
                            var height = displayMode.GetHeight();
                            displayMode.GetFrameRate(out var frameDuration, out var timeScale);
                            var fieldDominance = displayMode.GetFieldDominance();

                            var frameRate = ConvertToFrameRate(frameDuration, timeScale);
                            var isInterlaced = fieldDominance == BMDFieldDominance.bmdLowerFieldFirst ||
                                              fieldDominance == BMDFieldDominance.bmdUpperFieldFirst;

                            _logger.LogDebug("Found display mode: {Name} {Width}x{Height} @ {FrameRate}fps",
                                name, width, height, frameRate.Value);

                            modes.Add(new VideoMode(
                                width,
                                height,
                                frameRate,
                                PixelFormat.YUV422_10bit,
                                isInterlaced,
                                name));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(displayMode);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(iterator);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate display modes");
        }

        if (modes.Count == 0)
        {
            _logger.LogWarning("No display modes found, using defaults");
            return GetSimulatedModes();
        }

        return modes;
    }

    private static List<VideoMode> GetSimulatedModes()
    {
        return new List<VideoMode>
        {
            new(1920, 1080, FrameRate.Fps23_976, PixelFormat.YUV422_10bit, false, "1080p23.98"),
            new(1920, 1080, FrameRate.Fps24, PixelFormat.YUV422_10bit, false, "1080p24"),
            new(1920, 1080, FrameRate.Fps25, PixelFormat.YUV422_10bit, false, "1080p25"),
            new(1920, 1080, FrameRate.Fps29_97, PixelFormat.YUV422_10bit, false, "1080p29.97"),
            new(1920, 1080, FrameRate.Fps30, PixelFormat.YUV422_10bit, false, "1080p30"),
            new(1920, 1080, FrameRate.Fps50, PixelFormat.YUV422_10bit, false, "1080p50"),
            new(1920, 1080, FrameRate.Fps59_94, PixelFormat.YUV422_10bit, false, "1080p59.94"),
            new(1920, 1080, FrameRate.Fps60, PixelFormat.YUV422_10bit, false, "1080p60"),
            new(1920, 1080, FrameRate.Fps25, PixelFormat.YUV422_10bit, true, "1080i50"),
            new(1920, 1080, FrameRate.Fps29_97, PixelFormat.YUV422_10bit, true, "1080i59.94"),
            new(3840, 2160, FrameRate.Fps23_976, PixelFormat.YUV422_10bit, false, "2160p23.98"),
            new(3840, 2160, FrameRate.Fps24, PixelFormat.YUV422_10bit, false, "2160p24"),
            new(3840, 2160, FrameRate.Fps25, PixelFormat.YUV422_10bit, false, "2160p25"),
            new(3840, 2160, FrameRate.Fps29_97, PixelFormat.YUV422_10bit, false, "2160p29.97"),
            new(3840, 2160, FrameRate.Fps30, PixelFormat.YUV422_10bit, false, "2160p30"),
            new(720, 486, FrameRate.Fps29_97, PixelFormat.YUV422_8bit, true, "NTSC"),
            new(720, 576, FrameRate.Fps25, PixelFormat.YUV422_8bit, true, "PAL"),
        };
    }

    private static FrameRate ConvertToFrameRate(long frameDuration, long timeScale)
    {
        var fps = (double)timeScale / frameDuration;

        // Check drop-frame rates first (they're more specific)
        // 59.94 = 60000/1001 ≈ 59.94005994
        // 29.97 = 30000/1001 ≈ 29.97002997
        // 23.976 = 24000/1001 ≈ 23.97602398
        return fps switch
        {
            >= 59.93 and < 59.95 => FrameRate.Fps59_94,
            >= 59.99 and <= 60.01 => FrameRate.Fps60,
            >= 49.99 and <= 50.01 => FrameRate.Fps50,
            >= 29.96 and < 29.98 => FrameRate.Fps29_97,
            >= 29.99 and <= 30.01 => FrameRate.Fps30,
            >= 24.99 and <= 25.01 => FrameRate.Fps25,
            >= 23.97 and < 23.99 => FrameRate.Fps23_976,
            >= 23.99 and <= 24.01 => FrameRate.Fps24,
            _ => FrameRate.Fps30 // Default
        };
    }

    private List<VideoConnector> EnumerateAvailableConnectors()
    {
        var connectors = new List<VideoConnector>();

        if (_deckLinkAttributes == null)
        {
            _logger.LogWarning("No attributes interface, returning default connectors");
            return new List<VideoConnector> { VideoConnector.SDI, VideoConnector.HDMI };
        }

        try
        {
            // Query available connectors via IDeckLinkProfileAttributes (not IDeckLinkConfiguration)
            var hr = _deckLinkAttributes.GetInt(
                BMDDeckLinkAttributeID.BMDDeckLinkVideoInputConnections,
                out var availableConnections);

            if (hr == DeckLinkHResult.S_OK)
            {
                // Decode the bitmask
                if ((availableConnections & (long)BMDVideoConnection.bmdVideoConnectionSDI) != 0)
                    connectors.Add(VideoConnector.SDI);
                if ((availableConnections & (long)BMDVideoConnection.bmdVideoConnectionHDMI) != 0)
                    connectors.Add(VideoConnector.HDMI);
                if ((availableConnections & (long)BMDVideoConnection.bmdVideoConnectionOpticalSDI) != 0)
                    connectors.Add(VideoConnector.OpticalSDI);
                if ((availableConnections & (long)BMDVideoConnection.bmdVideoConnectionComponent) != 0)
                    connectors.Add(VideoConnector.Component);
                if ((availableConnections & (long)BMDVideoConnection.bmdVideoConnectionComposite) != 0)
                    connectors.Add(VideoConnector.Composite);
                if ((availableConnections & (long)BMDVideoConnection.bmdVideoConnectionSVideo) != 0)
                    connectors.Add(VideoConnector.SVideo);

                _logger.LogInformation("Device supports {Count} input connectors: {Connectors}",
                    connectors.Count, string.Join(", ", connectors));
            }
            else
            {
                _logger.LogWarning("Failed to query available connectors: 0x{ErrorCode:X8}", hr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enumerating available connectors");
        }

        // Return defaults if none found
        if (connectors.Count == 0)
        {
            connectors.Add(VideoConnector.SDI);
            connectors.Add(VideoConnector.HDMI);
        }

        return connectors;
    }

    public async Task<bool> StartCaptureAsync(VideoMode mode, CancellationToken ct = default)
    {
        if (_status == DeviceStatus.Capturing)
        {
            _logger.LogWarning("Capture already in progress");
            return false;
        }

        _logger.LogInformation("Starting capture: {Mode}", mode.DisplayName);

        try
        {
            SetStatus(DeviceStatus.Initializing);
            _currentMode = mode;
            _frameCount = 0;

            if (_isSimulated)
            {
                return await StartSimulatedCaptureAsync(ct);
            }
            else
            {
                return await StartRealCaptureAsync(mode, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start capture");
            SetStatus(DeviceStatus.Error);
            return false;
        }
    }

    private async Task<bool> StartRealCaptureAsync(VideoMode mode, CancellationToken ct)
    {
        if (_deckLinkInput == null) return false;

        try
        {
            // Apply the selected connector before starting capture
            ApplyConnectorSelection();

            // Give hardware time to switch connectors
            await Task.Delay(500, ct);

            // Determine the BMD display mode
            var bmdMode = GetBMDDisplayMode(mode);
            // Use 8-bit YUV (UYVY) - native hardware format for best compatibility
            _capturePixelFormat = BMDPixelFormat.bmdFormat8BitYUV;

            // Enable video input
            _logger.LogInformation("Enabling video input: Mode={Mode}, PixelFormat={PixelFormat}",
                bmdMode, _capturePixelFormat);

            var hr = _deckLinkInput.EnableVideoInput(
                bmdMode,
                _capturePixelFormat,
                BMDVideoInputFlags.bmdVideoInputEnableFormatDetection);

            if (hr != DeckLinkHResult.S_OK)
            {
                var errorMsg = hr switch
                {
                    unchecked((int)0x80004005) => "E_FAIL - Device may be in use by another application (close Blackmagic Desktop Video)",
                    unchecked((int)0x80070005) => "E_ACCESSDENIED - Access denied to device",
                    unchecked((int)0x80004002) => "E_NOINTERFACE - Interface not supported",
                    _ => $"Unknown error 0x{hr:X8}"
                };
                _logger.LogError("Failed to enable video input: {Error}", errorMsg);
                return false;
            }

            // Enable audio input (48kHz, 32-bit, 16 channels)
            hr = _deckLinkInput.EnableAudioInput(
                BMDAudioSampleRate.bmdAudioSampleRate48kHz,
                BMDAudioSampleType.bmdAudioSampleType32bitInteger,
                (uint)_audioChannels);

            if (hr != DeckLinkHResult.S_OK)
            {
                _logger.LogWarning("Failed to enable audio input: 0x{ErrorCode:X8}", hr);
                // Continue without audio
            }

            // Set callback
            hr = _deckLinkInput.SetCallback(this);
            if (hr != DeckLinkHResult.S_OK)
            {
                _logger.LogError("Failed to set callback: 0x{ErrorCode:X8}", hr);
                return false;
            }

            // Start streams
            hr = _deckLinkInput.StartStreams();
            if (hr != DeckLinkHResult.S_OK)
            {
                _logger.LogError("Failed to start streams: 0x{ErrorCode:X8}", hr);
                return false;
            }

            SetStatus(DeviceStatus.Capturing);
            _logger.LogInformation(
                "Capture started on {DeviceName} - Connector: {Connector}, Mode: {Mode}, Format: {Format}",
                DisplayName, _selectedConnector, bmdMode, _capturePixelFormat);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start real capture");
            return false;
        }
    }

    private async Task<bool> StartSimulatedCaptureAsync(CancellationToken ct)
    {
        _captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _captureTask = SimulateCaptureAsync(_captureCts.Token);

        SetStatus(DeviceStatus.Capturing);
        return true;
    }

    private static BMDDisplayMode GetBMDDisplayMode(VideoMode mode)
    {
        // Map video mode to BMD display mode based on resolution and frame rate
        var fps = mode.FrameRate.Value;

        if (mode.Width == 720)
        {
            return mode.Height == 486 ? BMDDisplayMode.bmdModeNTSC : BMDDisplayMode.bmdModePAL;
        }

        if (mode.Width == 3840 && mode.Height == 2160)
        {
            return fps switch
            {
                >= 59.99 and <= 60.01 => BMDDisplayMode.bmdMode4K2160p60,
                >= 59.93 and < 59.95 => BMDDisplayMode.bmdMode4K2160p5994,
                >= 49.99 and <= 50.01 => BMDDisplayMode.bmdMode4K2160p50,
                >= 29.99 and <= 30.01 => BMDDisplayMode.bmdMode4K2160p30,
                >= 29.96 and < 29.98 => BMDDisplayMode.bmdMode4K2160p2997,
                >= 24.99 and <= 25.01 => BMDDisplayMode.bmdMode4K2160p25,
                >= 23.99 and <= 24.01 => BMDDisplayMode.bmdMode4K2160p24,
                >= 23.97 and < 23.99 => BMDDisplayMode.bmdMode4K2160p2398,
                _ => BMDDisplayMode.bmdMode4K2160p2398
            };
        }

        // Default to 1080
        if (mode.IsInterlaced)
        {
            return fps switch
            {
                >= 29.96 and < 29.98 => BMDDisplayMode.bmdModeHD1080i5994,
                >= 29.99 and <= 30.01 => BMDDisplayMode.bmdModeHD1080i6000,
                _ => BMDDisplayMode.bmdModeHD1080i50
            };
        }

        return fps switch
        {
            >= 59.99 and <= 60.01 => BMDDisplayMode.bmdModeHD1080p6000,
            >= 59.93 and < 59.95 => BMDDisplayMode.bmdModeHD1080p5994,
            >= 49.99 and <= 50.01 => BMDDisplayMode.bmdModeHD1080p50,
            >= 29.99 and <= 30.01 => BMDDisplayMode.bmdModeHD1080p30,
            >= 29.96 and < 29.98 => BMDDisplayMode.bmdModeHD1080p2997,
            >= 24.99 and <= 25.01 => BMDDisplayMode.bmdModeHD1080p25,
            >= 23.99 and <= 24.01 => BMDDisplayMode.bmdModeHD1080p24,
            >= 23.97 and < 23.99 => BMDDisplayMode.bmdModeHD1080p2398,
            _ => BMDDisplayMode.bmdModeHD1080p2398
        };
    }

    public async Task StopCaptureAsync()
    {
        if (_status != DeviceStatus.Capturing)
            return;

        _logger.LogInformation("Stopping capture");

        if (_isSimulated)
        {
            _captureCts?.Cancel();
            if (_captureTask != null)
            {
                try
                {
                    await _captureTask;
                }
                catch (OperationCanceledException) { }
            }
        }
        else
        {
            if (_deckLinkInput != null)
            {
                _deckLinkInput.StopStreams();
                _deckLinkInput.SetCallback(null);
                _deckLinkInput.DisableVideoInput();
                _deckLinkInput.DisableAudioInput();
            }

        }

        _currentMode = null;
        SetStatus(DeviceStatus.Idle);
    }

    // IDeckLinkInputCallback implementation
    public int VideoInputFormatChanged(
        BMDVideoInputFormatChangedEvents notificationEvents,
        IDeckLinkDisplayMode newDisplayMode,
        BMDDetectedVideoInputFormatFlags detectedSignalFlags)
    {
        try
        {
            newDisplayMode.GetName(out var name);
            var width = newDisplayMode.GetWidth();
            var height = newDisplayMode.GetHeight();
            newDisplayMode.GetFrameRate(out var frameDuration, out var timeScale);
            var fieldDominance = newDisplayMode.GetFieldDominance();
            var displayModeId = newDisplayMode.GetDisplayMode();

            _logger.LogInformation(
                "Video format changed: {Name} ({Width}x{Height}), events=0x{Events:X}, signalFlags=0x{SignalFlags:X}, modeId=0x{ModeId:X8}",
                name, width, height, (uint)notificationEvents, (uint)detectedSignalFlags, (uint)displayModeId);

            var frameRate = ConvertToFrameRate(frameDuration, timeScale);
            var isInterlaced = fieldDominance == BMDFieldDominance.bmdLowerFieldFirst ||
                              fieldDominance == BMDFieldDominance.bmdUpperFieldFirst;

            // Determine pixel format from what we're actually capturing
            // _capturePixelFormat is set to bmdFormat8BitYUV, so use YUV422_8bit
            var pixelFormat = _capturePixelFormat == BMDPixelFormat.bmdFormat8BitYUV
                ? PixelFormat.YUV422_8bit
                : PixelFormat.YUV422_10bit;

            _currentMode = new VideoMode(
                width,
                height,
                frameRate,
                pixelFormat,
                isInterlaced,
                name);

            _logger.LogInformation("Updated current mode to: {Width}x{Height} @ {FrameRate}fps, interlaced={Interlaced}",
                width, height, frameRate.Value, isInterlaced);

            // Apply format change when display mode changes
            if ((notificationEvents & BMDVideoInputFormatChangedEvents.bmdVideoInputDisplayModeChanged) != 0)
            {
                _logger.LogInformation("Display mode changed to: {Mode} - re-enabling video input", displayModeId);
                _pendingFormatChange = displayModeId;

                // Apply the format change immediately
                Task.Run(() => ApplyPendingFormatChange());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling format change");
        }

        return DeckLinkHResult.S_OK;
    }

    private void ApplyPendingFormatChange()
    {
        if (_pendingFormatChange == BMDDisplayMode.bmdModeUnknown || _formatChangeInProgress)
            return;

        if (_deckLinkInput == null) return;

        _formatChangeInProgress = true;
        var newMode = _pendingFormatChange;
        _pendingFormatChange = BMDDisplayMode.bmdModeUnknown;

        try
        {
            _logger.LogInformation("Applying format change to: {Mode} with pixel format: {PixelFormat}",
                newMode, _capturePixelFormat);

            // Proper sequence: Stop → Disable → Enable → Start
            // 1. Stop streams
            var hr = _deckLinkInput.StopStreams();
            _logger.LogDebug("StopStreams result: 0x{HR:X8}", hr);

            // 2. Disable video input
            hr = _deckLinkInput.DisableVideoInput();
            _logger.LogDebug("DisableVideoInput result: 0x{HR:X8}", hr);

            // 3. Re-enable video input with the detected format
            // Note: Memory allocator is already set from initial EnableVideoInput call
            hr = _deckLinkInput.EnableVideoInput(
                newMode,
                _capturePixelFormat,
                BMDVideoInputFlags.bmdVideoInputEnableFormatDetection);

            if (hr == DeckLinkHResult.S_OK)
            {
                _logger.LogInformation("Video input re-enabled with format: {Mode}", newMode);
            }
            else
            {
                _logger.LogError("Failed to re-enable video input: 0x{ErrorCode:X8}", hr);
            }

            // 4. Restart streams
            hr = _deckLinkInput.StartStreams();
            _logger.LogDebug("StartStreams result: 0x{HR:X8}", hr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying format change");
        }
        finally
        {
            _formatChangeInProgress = false;
        }
    }

    public int VideoInputFrameArrived(
        IDeckLinkVideoInputFrame? videoFrame,
        IDeckLinkAudioInputPacket? audioPacket)
    {
        try
        {
            if (videoFrame != null && _currentMode != null)
            {
                ProcessVideoFrame(videoFrame);
            }

            if (audioPacket != null)
            {
                ProcessAudioPacket(audioPacket);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing frame");
        }

        return DeckLinkHResult.S_OK;
    }

    private void ProcessVideoFrame(IDeckLinkVideoInputFrame frame)
    {
        if (_currentMode == null) return;

        _frameCount++;

        // Diagnostic: Check what type of object we're receiving
        if (_frameCount == 1)
        {
            var isComObject = Marshal.IsComObject(frame);
            var typeName = frame.GetType().FullName;
            var isCCW = typeName?.StartsWith("System.__ComObject") == false;
            _logger.LogInformation(
                "Frame object diagnostics: IsComObject={IsComObject}, TypeName={TypeName}, IsCCW={IsCCW}",
                isComObject, typeName, isCCW);
        }

        var width = frame.GetWidth();
        var height = frame.GetHeight();
        var rowBytes = frame.GetRowBytes();
        var flags = frame.GetFlags();
        var bmdPixelFormat = frame.GetPixelFormat();

        // Determine actual pixel format from frame
        // For 8-bit UYVY (bmdFormat8BitYUV): rowBytes = width * 2
        // For 10-bit YUV (bmdFormat10BitYUV): rowBytes = width * 16/6 (packed format)
        var actualPixelFormat = bmdPixelFormat switch
        {
            BMDPixelFormat.bmdFormat8BitYUV => PixelFormat.YUV422_8bit,
            BMDPixelFormat.bmdFormat10BitYUV => PixelFormat.YUV422_10bit,
            _ => PixelFormat.YUV422_8bit // Default to 8-bit
        };

        // Calculate expected rowBytes based on pixel format
        int expectedRowBytes = actualPixelFormat == PixelFormat.YUV422_8bit
            ? width * 2                    // 8-bit UYVY: 2 bytes per pixel
            : (width * 16 + 5) / 6;        // 10-bit packed: 16 bits per 6 pixels (roughly 2.67 bytes/pixel)

        // Log every 100 frames for debugging
        if (_frameCount % 100 == 0)
        {
            _logger.LogInformation("Frame {FrameCount}: {Width}x{Height}, rowBytes={RowBytes} (expected={Expected}), pixelFormat=0x{PixelFmt:X8}, flags=0x{Flags:X8}",
                _frameCount, width, height, rowBytes, expectedRowBytes, (uint)bmdPixelFormat, (uint)flags);
        }

        // Warn if rowBytes doesn't match expectation (indicates format mismatch)
        if (rowBytes != expectedRowBytes && _frameCount <= 10)
        {
            _logger.LogWarning("Frame {FrameCount}: rowBytes mismatch! Actual={Actual}, Expected={Expected} for pixelFormat=0x{PixelFmt:X8}",
                _frameCount, rowBytes, expectedRowBytes, (uint)bmdPixelFormat);
        }

        // Check for no input - but still try to process, the flag may be incorrect
        bool hasNoInputFlag = (flags & BMDFrameFlags.bmdFrameHasNoInputSource) != 0;
        if (hasNoInputFlag && _frameCount % 300 == 0)
        {
            _logger.LogWarning("Frame has no-input flag (frame {FrameCount}, flags=0x{Flags:X8}) on connector {Connector} - still attempting to process",
                _frameCount, (uint)flags, _selectedConnector);
        }

        // Log that we got a valid frame
        if (_frameCount <= 5 || _frameCount % 100 == 0)
        {
            _logger.LogInformation("Valid frame {FrameCount}: {Width}x{Height}, rowBytes={RowBytes}, flags=0x{Flags:X8}",
                _frameCount, width, height, rowBytes, (uint)flags);
        }

        var frameSize = rowBytes * height;

        // Initialize ring buffer if needed (to avoid DMA buffer recycling issues)
        // Ring buffer allows us to copy immediately and queue for processing
        lock (_ringBufferLock)
        {
            if (_ringBuffer == null || _ringBuffer[0].Length != frameSize)
            {
                _ringBuffer = new byte[RingBufferSlots][];
                for (int i = 0; i < RingBufferSlots; i++)
                {
                    _ringBuffer[i] = new byte[frameSize];
                }
                _logger.LogInformation("Initialized ring buffer: {Slots} slots x {Size} bytes", RingBufferSlots, frameSize);
            }
        }

        // Get the current write slot
        byte[] currentSlot;
        lock (_ringBufferLock)
        {
            currentSlot = _ringBuffer![_ringBufferWriteIndex];
            _ringBufferWriteIndex = (_ringBufferWriteIndex + 1) % RingBufferSlots;
        }

        // Reuse unmanaged buffer for native copy
        if (_unmanagedBuffer == IntPtr.Zero || _unmanagedBufferSize != frameSize)
        {
            if (_unmanagedBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_unmanagedBuffer);
            }
            _unmanagedBuffer = Marshal.AllocHGlobal(frameSize);
            _unmanagedBufferSize = frameSize;
        }

        // CRITICAL: Copy frame data IMMEDIATELY using native DLL
        // GetBytes was removed from IDeckLinkVideoFrame in SDK 14.3.
        // Native DLL uses IDeckLinkVideoBuffer::GetBytes, legacy v14.2.1 GetBytes, or offset-280 fallback.
        bool copySuccess = false;
        try
        {
            // Use GetIUnknownForObject to get the raw native IUnknown pointer
            IntPtr framePtr = Marshal.GetIUnknownForObject(frame);
            if (framePtr != IntPtr.Zero)
            {
                try
                {
                    // Native DLL copies from DMA buffer using SEH-protected memory access
                    // Returns: 1 = success, 0 = not enough data, -1 = data looks corrupt (BGRA-like)
                    int result = CopyDeckLinkFrameBytes(framePtr, _unmanagedBuffer, frameSize);

                    if (result == 1)
                    {
                        // Good frame - copy to ring buffer slot
                        Marshal.Copy(_unmanagedBuffer, currentSlot, 0, frameSize);
                        copySuccess = true;

                        if (_frameCount <= 5)
                        {
                            int midOffset = (height / 2) * rowBytes + (width / 2) * 2;
                            _logger.LogInformation("Frame {FrameCount}: Copied {Size} bytes. First: {B0:X2}{B1:X2}{B2:X2}{B3:X2}, Mid: {M0:X2}{M1:X2}{M2:X2}{M3:X2}",
                                _frameCount, frameSize,
                                currentSlot[0], currentSlot[1], currentSlot[2], currentSlot[3],
                                currentSlot[midOffset], currentSlot[midOffset + 1], currentSlot[midOffset + 2], currentSlot[midOffset + 3]);
                        }
                    }
                    else if (result == -1)
                    {
                        // Corrupt frame detected by native DLL - use cached frame if available
                        if (_lastGoodFrameCache != null && _lastGoodFrameCache.Length == frameSize)
                        {
                            Buffer.BlockCopy(_lastGoodFrameCache, 0, currentSlot, 0, frameSize);
                            copySuccess = true;
                            if (_frameCount <= 20 || _frameCount % 100 == 0)
                            {
                                _logger.LogWarning("Frame {FrameCount}: Corrupt data detected, using cached frame", _frameCount);
                            }
                        }
                        else if (_frameCount <= 5)
                        {
                            _logger.LogWarning("Frame {FrameCount}: Corrupt data, no cache available", _frameCount);
                        }
                    }
                    else if (_frameCount <= 5)
                    {
                        _logger.LogWarning("Frame {FrameCount}: Native copy returned {Result}", _frameCount, result);
                    }
                }
                finally
                {
                    Marshal.Release(framePtr);
                }
            }
        }
        catch (Exception ex)
        {
            if (_frameCount <= 5)
            {
                _logger.LogError(ex, "Frame {FrameCount}: Exception during native copy", _frameCount);
            }
        }

        // If copy failed, skip frame
        if (!copySuccess)
        {
            return;
        }

        // Check multiple points for black fill pattern (indicates partial frame copy)
        // The native DLL fills failed regions with 0x80, 0x10, 0x80, 0x10
        bool frameValid = true;
        int badPoints = 0;

        // Calculate HANC offset: DeckLink may include horizontal blanking at the start of each row
        int activeVideoBytes = width * 2;  // UYVY: 2 bytes per pixel
        int hancOffset = rowBytes > activeVideoBytes ? rowBytes - activeVideoBytes : 0;

        // Check points for visual quality - focus on what the user sees
        // NOTE: End-of-frame region consistently fails due to DMA timing (last 64KB)
        // so we don't check there - instead we check the visible middle portion
        int[] checkOffsets = new int[]
        {
            // Quadrant centers (visual quality check)
            (height / 4) * rowBytes + hancOffset + (width / 4) * 2,
            (height / 4) * rowBytes + hancOffset + (3 * width / 4) * 2,
            (3 * height / 4) * rowBytes + hancOffset + (width / 4) * 2,
            (3 * height / 4) * rowBytes + hancOffset + (3 * width / 4) * 2,
            (height / 2) * rowBytes + hancOffset + (width / 2) * 2,
            // Additional points spread across the frame (avoid end-of-frame region)
            (height / 8) * rowBytes + hancOffset + (width / 2) * 2,
            (3 * height / 8) * rowBytes + hancOffset + (width / 2) * 2,
            (5 * height / 8) * rowBytes + hancOffset + (width / 2) * 2,
            (7 * height / 8) * rowBytes + hancOffset + (width / 2) * 2,
        };

        foreach (int offset in checkOffsets)
        {
            if (offset >= 0 && offset + 3 < frameSize)
            {
                byte u = currentSlot[offset];
                byte y0 = currentSlot[offset + 1];
                byte v = currentSlot[offset + 2];
                byte y1 = currentSlot[offset + 3];

                // Check for corruption patterns:
                // 1. Black fill pattern from native DLL (indicates failed copy region)
                // 2. All-zeros (uninitialized or failed copy)
                // Note: Y=255 is valid super-white in UYVY, don't reject it
                // Note: Y<16 can occur in valid dark scenes, only reject if ALL checked points are bad
                bool isBlackFill = (u == 0x80 && y0 == 0x10 && v == 0x80 && y1 == 0x10);
                bool isZeros = (u == 0 && y0 == 0 && v == 0 && y1 == 0);

                if (isBlackFill || isZeros)
                {
                    badPoints++;
                }
            }
        }

        // Only reject if majority of check points are bad (avoids rejecting legitimate black scenes)
        // blackFill = 80 10 80 10 is valid UYVY black, so single point match is not corruption
        if (badPoints >= 5) // 5 out of 9 points = majority
        {
            frameValid = false;
        }

        if (frameValid)
        {
            // Update cache with good frame for corrupt frame recovery
            if (_lastGoodFrameCache == null || _lastGoodFrameCache.Length != frameSize)
            {
                _lastGoodFrameCache = new byte[frameSize];
            }
            Buffer.BlockCopy(currentSlot, 0, _lastGoodFrameCache, 0, frameSize);
        }
        else
        {
            // Use cached frame if available
            if (_lastGoodFrameCache != null && _lastGoodFrameCache.Length == frameSize)
            {
                Buffer.BlockCopy(_lastGoodFrameCache, 0, currentSlot, 0, frameSize);
            }
            else
            {
                // No cache yet, skip frame
                return;
            }
        }

        // Skip first 15 frames to avoid startup instability (DMA buffers settling)
        // Native DLL shows frames 1-3 fail, and frames 4-5 have incomplete data
        if (_frameCount <= 15)
        {
            if (_frameCount == 15)
            {
                _logger.LogInformation("Startup frames complete, beginning display");
            }
            return;
        }

        // Use frame rate from current mode for accurate timestamp
        var frameRate = _currentMode.FrameRate.Value > 0 ? _currentMode.FrameRate.Value : 30.0;
        var timestamp = TimeSpan.FromSeconds(_frameCount / frameRate);

        // Copy to pooled event buffer (avoids 4MB allocation per frame at 60fps)
        if (_eventBufferPool == null || _eventBufferPool[0].Length != frameSize)
        {
            _eventBufferPool = new byte[EventBufferPoolSize][];
            for (int i = 0; i < EventBufferPoolSize; i++)
                _eventBufferPool[i] = new byte[frameSize];
        }
        var eventBuffer = _eventBufferPool[_eventBufferIndex];
        _eventBufferIndex = (_eventBufferIndex + 1) % EventBufferPoolSize;
        Buffer.BlockCopy(currentSlot, 0, eventBuffer, 0, frameSize);

        // Create an accurate mode reflecting the actual frame properties
        // This ensures downstream code (like YUV conversion) uses correct pixel format
        var actualMode = new VideoMode(
            width,
            height,
            _currentMode.FrameRate,
            actualPixelFormat,
            _currentMode.IsInterlaced,
            _currentMode.DisplayName);

        VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
        {
            FrameData = eventBuffer.AsMemory(),
            Mode = actualMode,
            Timestamp = timestamp,
            FrameNumber = _frameCount
        });
    }

    private long _audioPacketCount;

    private void ProcessAudioPacket(IDeckLinkAudioInputPacket packet)
    {
        _audioPacketCount++;
        var sampleCount = packet.GetSampleFrameCount();
        packet.GetBytes(out var bufferPtr);

        if (_audioPacketCount <= 5 || _audioPacketCount % 500 == 0)
        {
            _logger.LogInformation("Audio packet {Count}: samples={Samples}, bufferPtr={Ptr}",
                _audioPacketCount, sampleCount, bufferPtr);
        }

        if (bufferPtr == IntPtr.Zero || sampleCount <= 0) return;

        var bytesPerSample = 4; // 32-bit audio
        var bufferSize = sampleCount * _audioChannels * bytesPerSample;
        var audioBuffer = new byte[bufferSize];

        Marshal.Copy(bufferPtr, audioBuffer, 0, bufferSize);

        packet.GetPacketTime(out var packetTime, 10000000);
        var timestamp = TimeSpan.FromTicks(packetTime);

        AudioSamplesReceived?.Invoke(this, new AudioSamplesEventArgs
        {
            SampleData = audioBuffer.AsMemory(),
            SampleRate = 48000,
            Channels = _audioChannels,
            BitsPerSample = 32,
            Timestamp = timestamp
        });
    }

    private async Task SimulateCaptureAsync(CancellationToken ct)
    {
        if (_currentMode == null) return;

        var frameInterval = TimeSpan.FromSeconds(1.0 / _currentMode.FrameRate.Value);
        var bytesPerPixel = _currentMode.PixelFormat == PixelFormat.YUV422_10bit ? 4 : 2;
        var rowBytes = _currentMode.Width * bytesPerPixel;
        var frameSize = rowBytes * _currentMode.Height;

        var frameBuffer = new byte[frameSize];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(frameInterval, ct);

                _frameCount++;
                var timestamp = TimeSpan.FromSeconds(_frameCount / _currentMode.FrameRate.Value);

                GenerateTestPattern(frameBuffer, _currentMode.Width, _currentMode.Height, _frameCount);

                VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
                {
                    FrameData = frameBuffer.AsMemory(),
                    Mode = _currentMode,
                    Timestamp = timestamp,
                    FrameNumber = _frameCount
                });

                var samplesPerFrame = (int)(48000 / _currentMode.FrameRate.Value);
                var audioBuffer = new byte[samplesPerFrame * 16 * 4];

                AudioSamplesReceived?.Invoke(this, new AudioSamplesEventArgs
                {
                    SampleData = audioBuffer.AsMemory(),
                    SampleRate = 48000,
                    Channels = 16,
                    BitsPerSample = 32,
                    Timestamp = timestamp
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void GenerateTestPattern(byte[] buffer, int width, int height, long frameNumber)
    {
        var colors = new byte[][]
        {
            new byte[] { 128, 235, 128, 235 },
            new byte[] { 16, 210, 146, 170 },
            new byte[] { 166, 170, 16, 105 },
            new byte[] { 54, 145, 34, 63 },
            new byte[] { 202, 106, 222, 193 },
            new byte[] { 90, 81, 240, 127 },
            new byte[] { 240, 41, 109, 35 },
            new byte[] { 128, 16, 128, 16 },
        };

        var barWidth = width / 8;
        var rowBytes = width * 2;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x += 2)
            {
                var colorIndex = (x / barWidth) % 8;
                var color = colors[colorIndex];
                var offset = y * rowBytes + x * 2;

                if (offset + 3 < buffer.Length)
                {
                    buffer[offset] = color[0];
                    buffer[offset + 1] = color[1];
                    buffer[offset + 2] = color[2];
                    buffer[offset + 3] = color[3];
                }
            }
        }
    }

    private void SetStatus(DeviceStatus newStatus)
    {
        var oldStatus = _status;
        _status = newStatus;

        if (oldStatus != newStatus)
        {
            StatusChanged?.Invoke(this, new DeviceStatusChangedEventArgs
            {
                OldStatus = oldStatus,
                NewStatus = newStatus
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _captureCts?.Cancel();
        _captureCts?.Dispose();

        if (!_isSimulated)
        {
            try
            {
                _deckLinkInput?.StopStreams();
                _deckLinkInput?.SetCallback(null);

                if (_deckLinkConfiguration != null)
                    Marshal.ReleaseComObject(_deckLinkConfiguration);
                if (_deckLinkInput != null)
                    Marshal.ReleaseComObject(_deckLinkInput);
                if (_deckLink != null)
                    Marshal.ReleaseComObject(_deckLink);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing DeckLink device");
            }
        }

        // Clean up reusable unmanaged buffer
        if (_unmanagedBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_unmanagedBuffer);
            _unmanagedBuffer = IntPtr.Zero;
            _unmanagedBufferSize = 0;
        }

        SetStatus(DeviceStatus.Disconnected);
    }
}
