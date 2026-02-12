using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Recording;

namespace Screener.UI.ViewModels;

public partial class InputConfigurationViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly ILogger<InputConfigurationViewModel> _logger;

    public ObservableCollection<InputViewModel> Inputs { get; } = new();

    [ObservableProperty]
    private InputViewModel? _selectedInput;

    /// <summary>
    /// Event raised when the selected input changes, allowing the preview to be switched.
    /// </summary>
    public event EventHandler<InputViewModel?>? SelectedInputChanged;

    public InputConfigurationViewModel(IDeviceManager deviceManager, ILogger<InputConfigurationViewModel> logger)
    {
        _deviceManager = deviceManager;
        _logger = logger;

        RefreshInputs();
    }

    partial void OnSelectedInputChanged(InputViewModel? value)
    {
        // Update IsSelected on all inputs
        foreach (var input in Inputs)
        {
            input.IsSelected = input == value;
        }

        _logger.LogInformation("Selected input changed to: {Input}", value?.ShortName ?? "None");
        SelectedInputChanged?.Invoke(this, value);
    }

    /// <summary>
    /// Selects an input for preview.
    /// </summary>
    public void SelectInput(InputViewModel input)
    {
        SelectedInput = input;
    }

    public void RefreshInputs()
    {
        Inputs.Clear();

        int index = 0;
        foreach (var device in _deviceManager.AvailableDevices)
        {
            // Add an input entry for the device's primary connector
            var connector = device.AvailableConnectors.FirstOrDefault();
            if (connector == VideoConnector.Unknown)
                connector = VideoConnector.SDI; // Default to SDI

            Inputs.Add(new InputViewModel
            {
                DeviceId = device.DeviceId,
                DisplayName = device.DisplayName,
                Connector = connector,
                InputIndex = index,
                IsEnabled = index == 0, // Enable first input by default
                IsSelected = index == 0, // Select first input by default
                HasSignal = device.Status == DeviceStatus.Capturing
            });

            index++;
        }

        // Select the first input by default
        SelectedInput = Inputs.FirstOrDefault();

        _logger.LogInformation("Refreshed inputs: {Count} devices available", Inputs.Count);
    }

    /// <summary>
    /// Gets the list of enabled input configurations for recording.
    /// </summary>
    public List<InputConfiguration> GetEnabledInputs()
    {
        return Inputs
            .Where(i => i.IsEnabled)
            .Select(i => new InputConfiguration(
                i.DeviceId,
                i.DisplayName,
                i.Connector,
                i.InputIndex,
                true))
            .ToList();
    }

    /// <summary>
    /// Updates signal status for all inputs.
    /// </summary>
    public void UpdateSignalStatus()
    {
        foreach (var input in Inputs)
        {
            var device = _deviceManager.AvailableDevices
                .FirstOrDefault(d => d.DeviceId == input.DeviceId);

            if (device != null)
            {
                input.HasSignal = device.Status == DeviceStatus.Capturing;
            }
        }
    }
}

public partial class InputViewModel : ObservableObject
{
    [ObservableProperty]
    private string _deviceId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private VideoConnector _connector = VideoConnector.SDI;

    [ObservableProperty]
    private int _inputIndex;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _hasSignal;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private long _framesRecorded;

    [ObservableProperty]
    private int _droppedFrames;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _hasSchedule;

    [ObservableProperty]
    private ImageSource? _previewImage;

    [ObservableProperty]
    private string _formatDescription = string.Empty;

    /// <summary>
    /// Internal renderer managed by MainViewModel.
    /// </summary>
    internal InputPreviewRenderer? PreviewRenderer { get; set; }

    public string ShortName => Connector switch
    {
        VideoConnector.NDI => DisplayName.Replace(" (NDI)", ""),
        VideoConnector.SRT => DisplayName,
        _ => $"Input {InputIndex + 1}"
    };

    public string StatusText => HasSignal ? "Signal OK" : "No Signal";
}
