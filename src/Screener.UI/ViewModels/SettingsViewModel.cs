using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Upload;
using Screener.Core.Settings;

namespace Screener.UI.ViewModels;

/// <summary>
/// ViewModel for the settings window.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IDeviceManager _deviceManager;
    private readonly IUploadService _uploadService;
    private readonly ISettingsService _settingsService;

    // General Settings
    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _minimizeToTray = true;

    [ObservableProperty]
    private bool _showNotifications = true;

    [ObservableProperty]
    private string _defaultRecordingPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    [ObservableProperty]
    private string _filenameTemplate = "{date}_{time}_{device}_{preset}";

    // Video Settings
    [ObservableProperty]
    private ObservableCollection<CaptureDeviceInfo> _captureDevices = new();

    [ObservableProperty]
    private CaptureDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<VideoConnectorInfo> _videoInputs = new();

    [ObservableProperty]
    private VideoConnectorInfo? _selectedInput;

    [ObservableProperty]
    private ObservableCollection<EncodingPresetInfo> _encodingPresets = new();

    [ObservableProperty]
    private EncodingPresetInfo? _selectedPreset;

    [ObservableProperty]
    private ObservableCollection<string> _availableEncoders = new();

    [ObservableProperty]
    private bool _preferHardwareEncoding = true;

    // Audio Settings
    [ObservableProperty]
    private ObservableCollection<string> _audioChannelOptions = new() { "2 (Stereo)", "4", "8", "16" };

    [ObservableProperty]
    private string _selectedAudioChannels = "8";

    [ObservableProperty]
    private ObservableCollection<string> _sampleRateOptions = new() { "44100 Hz", "48000 Hz", "96000 Hz" };

    [ObservableProperty]
    private string _selectedSampleRate = "48000 Hz";

    [ObservableProperty]
    private bool _enableAudioPreview = true;

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _audioOutputDevices = new();

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioOutput;

    [ObservableProperty]
    private ObservableCollection<string> _meterTypes = new() { "VU", "PPM", "True Peak" };

    [ObservableProperty]
    private string _selectedMeterType = "PPM";

    [ObservableProperty]
    private double _referenceLevel = -18;

    // Timecode Settings
    [ObservableProperty]
    private bool _useNtpTime = true;

    [ObservableProperty]
    private bool _useSystemTime;

    [ObservableProperty]
    private bool _useManualTime;

    [ObservableProperty]
    private string _ntpServer = "pool.ntp.org";

    [ObservableProperty]
    private bool _autoSyncNtp = true;

    [ObservableProperty]
    private ObservableCollection<string> _timezones = new();

    [ObservableProperty]
    private string _selectedTimezone = TimeZoneInfo.Local.DisplayName;

    [ObservableProperty]
    private bool _useDropFrame = true;

    // Streaming Settings
    [ObservableProperty]
    private bool _enableStreaming;

    [ObservableProperty]
    private int _streamingPort = 8080;

    [ObservableProperty]
    private int _maxViewers = 10;

    [ObservableProperty]
    private bool _requireAccessToken;

    [ObservableProperty]
    private string _accessToken = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _streamingResolutions = new() { "480p", "720p", "1080p" };

    [ObservableProperty]
    private string _selectedStreamingResolution = "720p";

    [ObservableProperty]
    private int _streamingBitrate = 2500;

    // NDI Settings
    [ObservableProperty]
    private bool _enableNdiDiscovery = true;

    [ObservableProperty]
    private bool _enableNdiOutput;

    [ObservableProperty]
    private string _ndiOutputSourceName = "Screener Output";

    [ObservableProperty]
    private string _ndiRuntimeStatus = "Checking...";

    // SRT Input Settings
    [ObservableProperty]
    private ObservableCollection<SrtInputViewModel> _srtInputs = new();

    // SRT Output Settings
    [ObservableProperty]
    private bool _enableSrtOutput;

    [ObservableProperty]
    private ObservableCollection<string> _srtOutputModes = new() { "caller", "listener" };

    [ObservableProperty]
    private string _selectedSrtOutputMode = "caller";

    [ObservableProperty]
    private string _srtOutputAddress = string.Empty;

    [ObservableProperty]
    private int _srtOutputPort = 9000;

    [ObservableProperty]
    private int _srtOutputLatency = 120;

    [ObservableProperty]
    private int _srtOutputBitrate = 5000;

    // Cloud Storage Settings
    [ObservableProperty]
    private ObservableCollection<CloudProviderInfo> _cloudProviders = new();

    [ObservableProperty]
    private CloudProviderInfo? _selectedProvider;

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private bool _autoUpload;

    [ObservableProperty]
    private bool _autoUploadClips;

    [ObservableProperty]
    private ObservableCollection<int> _concurrentUploadOptions = new() { 1, 2, 3, 4, 5 };

    [ObservableProperty]
    private int _maxConcurrentUploads = 2;

    // Keyboard Shortcuts
    [ObservableProperty]
    private ObservableCollection<ShortcutInfo> _shortcuts = new();

    /// <summary>
    /// Event raised when the window should close.
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IDeviceManager deviceManager,
        IUploadService uploadService,
        ISettingsService settingsService)
    {
        _logger = logger;
        _deviceManager = deviceManager;
        _uploadService = uploadService;
        _settingsService = settingsService;

        InitializeData();
        _ = LoadSavedSettingsAsync();
    }

    private async Task LoadSavedSettingsAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();

            // Select the saved device
            if (!string.IsNullOrEmpty(settings.SelectedDeviceId))
            {
                var device = CaptureDevices.FirstOrDefault(d => d.Id == settings.SelectedDeviceId);
                if (device != null)
                {
                    SelectedDevice = device;

                    // Select the saved connector
                    var connector = VideoInputs.FirstOrDefault(v => v.Connector == settings.SelectedConnector);
                    if (connector != null)
                    {
                        SelectedInput = connector;
                    }
                }
            }

            DefaultRecordingPath = settings.DefaultRecordingPath;
            PreferHardwareEncoding = settings.PreferHardwareEncoding;

            // Streaming
            EnableStreaming = settings.EnableStreaming;
            StreamingPort = settings.StreamingPort;
            MaxViewers = settings.MaxViewers;
            RequireAccessToken = settings.RequireAccessToken;
            AccessToken = settings.AccessToken;
            SelectedStreamingResolution = settings.StreamingResolution;
            StreamingBitrate = settings.StreamingBitrate;

            // NDI
            EnableNdiDiscovery = settings.EnableNdiDiscovery;
            EnableNdiOutput = settings.EnableNdiOutput;
            NdiOutputSourceName = settings.NdiOutputSourceName;

            // SRT Output
            EnableSrtOutput = settings.EnableSrtOutput;
            SelectedSrtOutputMode = settings.SrtOutputMode;
            SrtOutputAddress = settings.SrtOutputAddress;
            SrtOutputPort = settings.SrtOutputPort;
            SrtOutputLatency = settings.SrtOutputLatency;
            SrtOutputBitrate = settings.SrtOutputBitrate;

            // SRT Inputs
            try
            {
                var inputs = System.Text.Json.JsonSerializer.Deserialize<List<SrtInputData>>(settings.SrtInputsJson);
                if (inputs != null)
                {
                    foreach (var input in inputs)
                        SrtInputs.Add(new SrtInputViewModel { Name = input.Name, Port = input.Port, Latency = input.Latency });
                }
            }
            catch { /* Invalid JSON, start fresh */ }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading saved settings");
        }
    }

    private void InitializeData()
    {
        // Load timezones
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
        {
            Timezones.Add(tz.DisplayName);
        }

        // Load encoding presets
        EncodingPresets.Add(new EncodingPresetInfo("Proxy", "720p H.264, low bitrate for review"));
        EncodingPresets.Add(new EncodingPresetInfo("Medium", "1080p H.264, balanced quality"));
        EncodingPresets.Add(new EncodingPresetInfo("High", "1080p H.264, high bitrate"));
        EncodingPresets.Add(new EncodingPresetInfo("Master", "Full resolution H.264/H.265, highest quality"));
        SelectedPreset = EncodingPresets[1];

        // Load available hardware encoders
        AvailableEncoders.Add("NVIDIA NVENC (H.264)");
        AvailableEncoders.Add("NVIDIA NVENC (HEVC)");

        // Load cloud providers
        foreach (var provider in _uploadService.GetProviders())
        {
            CloudProviders.Add(new CloudProviderInfo(
                provider.ProviderId,
                provider.DisplayName,
                provider.IsConfigured));
        }

        // Load keyboard shortcuts
        Shortcuts.Add(new ShortcutInfo("Start/Stop Recording", "F9"));
        Shortcuts.Add(new ShortcutInfo("Add Marker", "M"));
        Shortcuts.Add(new ShortcutInfo("Set In Point", "I"));
        Shortcuts.Add(new ShortcutInfo("Set Out Point", "O"));
        Shortcuts.Add(new ShortcutInfo("Create Clip", "C"));
        Shortcuts.Add(new ShortcutInfo("Fullscreen Preview", "F11"));
        Shortcuts.Add(new ShortcutInfo("Settings", "Ctrl+,"));

        // Refresh capture devices
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        CaptureDevices.Clear();

        foreach (var device in _deviceManager.AvailableDevices)
        {
            CaptureDevices.Add(new CaptureDeviceInfo(device.DeviceId, device.DisplayName));
        }

        if (CaptureDevices.Count > 0)
        {
            SelectedDevice = CaptureDevices[0];
        }
    }

    partial void OnSelectedDeviceChanged(CaptureDeviceInfo? value)
    {
        RefreshConnectors();
    }

    private void RefreshConnectors()
    {
        VideoInputs.Clear();

        if (SelectedDevice == null)
        {
            return;
        }

        // Get the actual device from the device manager
        var device = _deviceManager.AvailableDevices
            .FirstOrDefault(d => d.DeviceId == SelectedDevice.Id);

        if (device != null)
        {
            foreach (var connector in device.AvailableConnectors)
            {
                VideoInputs.Add(new VideoConnectorInfo(connector, GetConnectorDisplayName(connector)));
            }

            // Select the current connector or the first available
            var currentConnector = device.SelectedConnector;
            SelectedInput = VideoInputs.FirstOrDefault(v => v.Connector == currentConnector)
                ?? VideoInputs.FirstOrDefault();
        }
        else
        {
            // Fallback for devices without connector info
            VideoInputs.Add(new VideoConnectorInfo(VideoConnector.SDI, "SDI"));
            VideoInputs.Add(new VideoConnectorInfo(VideoConnector.HDMI, "HDMI"));
            SelectedInput = VideoInputs.FirstOrDefault();
        }
    }

    partial void OnSelectedInputChanged(VideoConnectorInfo? value)
    {
        if (value == null || SelectedDevice == null) return;

        // Apply the selection to the device
        var device = _deviceManager.AvailableDevices
            .FirstOrDefault(d => d.DeviceId == SelectedDevice.Id);

        if (device != null)
        {
            device.SelectedConnector = value.Connector;
            _logger.LogInformation("Selected connector {Connector} for device {Device}",
                value.DisplayName, device.DisplayName);
        }
    }

    private static string GetConnectorDisplayName(VideoConnector connector)
    {
        return connector switch
        {
            VideoConnector.SDI => "SDI",
            VideoConnector.HDMI => "HDMI",
            VideoConnector.OpticalSDI => "Optical SDI",
            VideoConnector.Component => "Component",
            VideoConnector.Composite => "Composite",
            VideoConnector.SVideo => "S-Video",
            VideoConnector.NDI => "NDI",
            VideoConnector.SRT => "SRT",
            _ => "Unknown"
        };
    }

    [RelayCommand]
    private void BrowseRecordingPath()
    {
        // In a real implementation, this would open a folder browser dialog
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Select Recording Folder",
            FileName = "Select Folder",
            CheckFileExists = false,
            CheckPathExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            DefaultRecordingPath = System.IO.Path.GetDirectoryName(dialog.FileName) ?? DefaultRecordingPath;
        }
    }

    [RelayCommand]
    private void GenerateToken()
    {
        AccessToken = Guid.NewGuid().ToString("N")[..16];
    }

    [RelayCommand]
    private void ConfigureProvider()
    {
        if (SelectedProvider == null)
            return;

        // In a real implementation, this would open a provider-specific configuration dialog
        _logger.LogInformation("Configuring provider: {Provider}", SelectedProvider.DisplayName);
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        if (SelectedProvider == null)
            return;

        ConnectionStatus = "Testing connection...";

        try
        {
            var provider = _uploadService.GetProviders()
                .FirstOrDefault(p => p.ProviderId == SelectedProvider.ProviderId);

            if (provider != null)
            {
                var isValid = await provider.ValidateConnectionAsync();
                ConnectionStatus = isValid ? "Connected successfully!" : "Connection failed";
                SelectedProvider.IsConfigured = isValid;
            }
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Error: {ex.Message}";
            _logger.LogError(ex, "Connection test failed");
        }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        // Reset all settings to defaults
        StartWithWindows = false;
        MinimizeToTray = true;
        ShowNotifications = true;
        DefaultRecordingPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        FilenameTemplate = "{date}_{time}_{device}_{preset}";
        PreferHardwareEncoding = true;
        EnableAudioPreview = true;
        UseNtpTime = true;
        NtpServer = "pool.ntp.org";
        AutoSyncNtp = true;
        UseDropFrame = true;
        EnableStreaming = false;
        StreamingPort = 8080;
        MaxViewers = 10;
        RequireAccessToken = false;
        SelectedStreamingResolution = "720p";
        StreamingBitrate = 2500;
        EnableNdiDiscovery = true;
        EnableNdiOutput = false;
        NdiOutputSourceName = "Screener Output";
        EnableSrtOutput = false;
        SelectedSrtOutputMode = "caller";
        SrtOutputAddress = string.Empty;
        SrtOutputPort = 9000;
        SrtOutputLatency = 120;
        SrtOutputBitrate = 5000;
        SrtInputs.Clear();

        _logger.LogInformation("Settings reset to defaults");
    }

    [RelayCommand]
    private void AddSrtInput()
    {
        SrtInputs.Add(new SrtInputViewModel
        {
            Name = $"SRT Input {SrtInputs.Count + 1}",
            Port = 9000 + SrtInputs.Count,
            Latency = 120
        });
    }

    [RelayCommand]
    private void RemoveSrtInput(SrtInputViewModel? input)
    {
        if (input != null)
            SrtInputs.Remove(input);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private async Task Save()
    {
        try
        {
            // Serialize SRT inputs
            var srtInputData = SrtInputs.Select(i => new SrtInputData { Name = i.Name, Port = i.Port, Latency = i.Latency }).ToList();
            var srtInputsJson = System.Text.Json.JsonSerializer.Serialize(srtInputData);

            var settings = new AppSettings
            {
                SelectedDeviceId = SelectedDevice?.Id ?? string.Empty,
                SelectedConnector = SelectedInput?.Connector ?? VideoConnector.SDI,
                DefaultRecordingPath = DefaultRecordingPath,
                PreferHardwareEncoding = PreferHardwareEncoding,
                EnableStreaming = EnableStreaming,
                StreamingPort = StreamingPort,
                MaxViewers = MaxViewers,
                RequireAccessToken = RequireAccessToken,
                AccessToken = AccessToken,
                StreamingResolution = SelectedStreamingResolution,
                StreamingBitrate = StreamingBitrate,
                EnableNdiDiscovery = EnableNdiDiscovery,
                EnableNdiOutput = EnableNdiOutput,
                NdiOutputSourceName = NdiOutputSourceName,
                EnableSrtOutput = EnableSrtOutput,
                SrtOutputMode = SelectedSrtOutputMode,
                SrtOutputAddress = SrtOutputAddress,
                SrtOutputPort = SrtOutputPort,
                SrtOutputLatency = SrtOutputLatency,
                SrtOutputBitrate = SrtOutputBitrate,
                SrtInputsJson = srtInputsJson
            };

            await _settingsService.SaveSettingsAsync(settings);
            _logger.LogInformation("Settings saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
        }

        CloseRequested?.Invoke(this, true);
    }
}

/// <summary>
/// Information about a capture device.
/// </summary>
public class CaptureDeviceInfo
{
    public string Id { get; }
    public string Name { get; }

    public CaptureDeviceInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }
}

/// <summary>
/// Information about an encoding preset.
/// </summary>
public class EncodingPresetInfo
{
    public string Name { get; }
    public string Description { get; }

    public EncodingPresetInfo(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

/// <summary>
/// Information about an audio device.
/// </summary>
public class AudioDeviceInfo
{
    public string Id { get; }
    public string Name { get; }

    public AudioDeviceInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }
}

/// <summary>
/// Information about a cloud storage provider.
/// </summary>
public class CloudProviderInfo : ObservableObject
{
    public string ProviderId { get; }
    public string DisplayName { get; }

    private bool _isConfigured;
    public bool IsConfigured
    {
        get => _isConfigured;
        set => SetProperty(ref _isConfigured, value);
    }

    public string StatusColor => IsConfigured ? "#22C55E" : "#EF4444";

    public CloudProviderInfo(string providerId, string displayName, bool isConfigured)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        _isConfigured = isConfigured;
    }
}

/// <summary>
/// Information about a keyboard shortcut.
/// </summary>
public class ShortcutInfo
{
    public string Action { get; }
    public string Shortcut { get; set; }

    public ShortcutInfo(string action, string shortcut)
    {
        Action = action;
        Shortcut = shortcut;
    }
}

/// <summary>
/// Information about a video input connector.
/// </summary>
public class VideoConnectorInfo
{
    public VideoConnector Connector { get; }
    public string DisplayName { get; }

    public VideoConnectorInfo(VideoConnector connector, string displayName)
    {
        Connector = connector;
        DisplayName = displayName;
    }
}

/// <summary>
/// ViewModel for an SRT input entry in settings.
/// </summary>
public partial class SrtInputViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _port = 9000;

    [ObservableProperty]
    private int _latency = 120;
}

/// <summary>
/// JSON serialization model for SRT input config.
/// </summary>
public class SrtInputData
{
    public string Name { get; set; } = string.Empty;
    public int Port { get; set; } = 9000;
    public int Latency { get; set; } = 120;
}
