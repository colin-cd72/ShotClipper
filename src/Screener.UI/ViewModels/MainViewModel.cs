using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Clipping;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Recording;
using Screener.Abstractions.Scheduling;
using Screener.Abstractions.Output;
using Screener.Abstractions.Streaming;
using Screener.Abstractions.Upload;
using Screener.Core.Persistence;
using Screener.Abstractions.Timecode;
using Screener.Core.Output;
using Screener.Core.Settings;
using Screener.Golf.Detection;
using Screener.Golf.Export;
using Screener.Golf.Models;
using Screener.Golf.Persistence;
using Screener.Golf.Switching;
using Screener.Preview;
using Screener.Scheduling;
using Screener.Streaming;
using Screener.Capture.Virtual;
using Screener.UI.Views;

namespace Screener.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDeviceManager _deviceManager;
    private readonly ISettingsService _settingsService;
    private readonly IRecordingService _recordingService;
    private readonly IClippingService _clippingService;
    private readonly ISchedulingService _schedulingService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly AudioPreviewService _audioPreviewService;
    private readonly IStreamingService _streamingService;
    private readonly OutputManager _outputManager;
    private readonly Dictionary<Guid, string> _activeScheduleIds = new(); // scheduleId → deviceId
    private readonly Dictionary<Guid, System.Windows.Threading.DispatcherTimer> _scheduleDurationTimers = new();

    // Multi-view preview state
    private readonly Dictionary<string, InputPreviewRenderer> _previewRenderers = new();
    private ICaptureDevice? _audioDevice;
    private InputViewModel? _audioLockedInput;
    private InputViewModel? _boundSource1;
    private InputViewModel? _boundSource2;
    private InputViewModel? _manualPreviewInput;

    // Upload service
    private readonly IUploadService _uploadService;

    // Panel relay (desktop → cloud push)
    private readonly PanelRelayService _panelRelayService;

    // Fullscreen preview
    private FullscreenPreviewWindow? _fullscreenWindow;

    // Golf mode services
    private readonly SwitcherService _switcherService;
    private readonly AutoCutService _autoCutService;
    private readonly GolfSession _golfSession;
    private readonly SequenceRecorder _sequenceRecorder;
    private readonly ClipExportService _clipExportService;
    private readonly GolferRepository _golferRepository;
    private System.Windows.Threading.DispatcherTimer? _autoCutTickTimer;

    // Transition engine
    private readonly TransitionEngine _transitionEngine;

    // Child ViewModels
    public RecordingControlsViewModel RecordingControls { get; }
    public TimecodeViewModel Timecode { get; }
    public AudioMetersViewModel AudioMeters { get; }
    public DriveStatusViewModel DriveStatus { get; }
    public ClipBinViewModel ClipBin { get; }
    public UploadQueueViewModel UploadQueue { get; }
    public InputConfigurationViewModel InputConfiguration { get; }

    [ObservableProperty]
    private SwitcherViewModel? _switcher;

    // Source preview images for the two-source switcher layout
    [ObservableProperty]
    private ImageSource? _source1PreviewImage;

    [ObservableProperty]
    private ImageSource? _source2PreviewImage;

    [ObservableProperty]
    private bool _source1HasSignal;

    [ObservableProperty]
    private bool _source2HasSignal;

    // Preview monitor image (whichever source is NOT on program)
    [ObservableProperty]
    private ImageSource? _previewSourceImage;

    [ObservableProperty]
    private bool _previewSourceHasSignal;

    // Preview source name for overlay display
    public string PreviewSourceName => _manualPreviewInput?.ShortName ?? "";

    // Recording State
    [ObservableProperty]
    private RecordingState _recordingState = RecordingState.Stopped;

    [ObservableProperty]
    private TimeSpan _recordingDuration;

    [ObservableProperty]
    private string _recordingName = string.Empty;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _recordingStatusText = "Ready";

    // Timecode
    [ObservableProperty]
    private Smpte12MTimecode _currentTimecode;

    [ObservableProperty]
    private string _timecodeSource = "NTP";

    [ObservableProperty]
    private string _timezone = "UTC";

    // Input Status
    [ObservableProperty]
    private string _inputStatusText = "No Input";

    [ObservableProperty]
    private string _inputStatus = "Disconnected";

    [ObservableProperty]
    private bool _hasNoSignal = true;

    // Presets
    [ObservableProperty]
    private EncodingPreset _selectedPreset = EncodingPreset.Medium;

    public ObservableCollection<EncodingPreset> AvailablePresets { get; } = new(EncodingPreset.AllPresets);

    // Drives
    [ObservableProperty]
    private DriveViewModel? _selectedDrive;

    public ObservableCollection<DriveViewModel> AvailableDrives { get; } = new();

    // Panel Visibility
    [ObservableProperty]
    private bool _isLeftPanelVisible = true;

    [ObservableProperty]
    private bool _isRightPanelVisible = true;

    // Multi-View Preview
    public ObservableCollection<InputViewModel> EnabledInputs { get; } = new();

    [ObservableProperty]
    private int _previewColumns = 1;

    // Streaming
    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string? _streamUrl;

    [ObservableProperty]
    private int _connectedViewers;

    [ObservableProperty]
    private bool _isQrCodeVisible;

    [ObservableProperty]
    private byte[]? _qrCodeImage;

    [ObservableProperty]
    private bool _isViewersPopupVisible;

    [ObservableProperty]
    private ObservableCollection<ViewerInfo> _connectedViewersList = new();

    // NDI Output
    [ObservableProperty]
    private bool _isNdiOutputActive;

    [ObservableProperty]
    private bool _isNdiAvailable;

    // SRT Output
    [ObservableProperty]
    private bool _isSrtOutputActive;

    [ObservableProperty]
    private string? _srtOutputStatus;

    // Golf Mode
    [ObservableProperty]
    private bool _isGolfModeEnabled;

    [ObservableProperty]
    private bool _isAutoCutEnabled;

    [ObservableProperty]
    private int _activeSourceIndex;

    [ObservableProperty]
    private string _autoCutStateText = "Disabled";

    [ObservableProperty]
    private int _swingCount;

    [ObservableProperty]
    private bool _isSessionActive;

    [ObservableProperty]
    private string? _sessionGolferName;

    [ObservableProperty]
    private bool _isIdleCalibrated;

    [ObservableProperty]
    private double _motionLevel;

    [ObservableProperty]
    private double _autoCutSensitivity = 4.0;

    // Audio Swing Detection
    [ObservableProperty]
    private double _audioLevel = -60.0;

    // Audio Lock
    [ObservableProperty]
    private bool _isAudioLocked;

    public InputViewModel? AudioLockedInput => _audioLockedInput;

    private ICaptureDevice? _golferAudioDevice;

    // Auto-Upload
    [ObservableProperty]
    private bool _autoUploadEnabled;

    [ObservableProperty]
    private string? _selectedUploadProviderId;

    public ObservableCollection<UploadProviderOption> AvailableUploadProviders { get; } = new();

    // Practice swing counter
    [ObservableProperty]
    private int _practiceSwingCount;

    // Cut history
    public ObservableCollection<CutHistoryItem> CutHistory { get; } = new();

    // Golfer Selection
    public ObservableCollection<GolferProfile> AvailableGolfers { get; } = new();

    [ObservableProperty]
    private GolferProfile? _selectedGolfer;

    // Export Queue
    public ObservableCollection<ExportQueueItem> ExportQueue { get; } = new();

    // Per-input schedule state
    private readonly Dictionary<string, InputScheduleState> _inputScheduleStates = new();

    public DateTime ScheduledStartTime
    {
        get => GetCurrentScheduleState().StartTime;
        set { GetCurrentScheduleState().StartTime = value; OnPropertyChanged(); }
    }

    public int ScheduledDurationHours
    {
        get => GetCurrentScheduleState().DurationHours;
        set { GetCurrentScheduleState().DurationHours = value; OnPropertyChanged(); }
    }

    public int ScheduledDurationMinutes
    {
        get => GetCurrentScheduleState().DurationMinutes;
        set { GetCurrentScheduleState().DurationMinutes = value; OnPropertyChanged(); }
    }

    public string? ScheduleStatusText
    {
        get => GetCurrentScheduleState().StatusText;
        set { GetCurrentScheduleState().StatusText = value; OnPropertyChanged(); }
    }

    public bool HasActiveSchedule
    {
        get => GetCurrentScheduleState().ActiveScheduleId.HasValue;
    }

    // Stats
    [ObservableProperty]
    private int _droppedFrames;

    [ObservableProperty]
    private Brush _droppedFramesColor = Brushes.Gray;

    [ObservableProperty]
    private DateTime _currentTime = DateTime.Now;

    // Recording Border (flashes red when recording)
    public Brush RecordingBorderBrush => IsRecording ? Brushes.Red : Brushes.Transparent;
    public Thickness RecordingBorderThickness => IsRecording ? new Thickness(4) : new Thickness(0);

    public MainViewModel(
        IServiceProvider serviceProvider,
        IDeviceManager deviceManager,
        ISettingsService settingsService,
        IRecordingService recordingService,
        IClippingService clippingService,
        ISchedulingService schedulingService,
        ILogger<MainViewModel> logger,
        RecordingControlsViewModel recordingControls,
        TimecodeViewModel timecode,
        AudioMetersViewModel audioMeters,
        DriveStatusViewModel driveStatus,
        ClipBinViewModel clipBin,
        UploadQueueViewModel uploadQueue,
        InputConfigurationViewModel inputConfiguration,
        AudioPreviewService audioPreviewService,
        IStreamingService streamingService,
        OutputManager outputManager,
        SwitcherService switcherService,
        AutoCutService autoCutService,
        GolfSession golfSession,
        SequenceRecorder sequenceRecorder,
        ClipExportService clipExportService,
        GolferRepository golferRepository,
        IUploadService uploadService,
        PanelRelayService panelRelayService,
        TransitionEngine transitionEngine,
        SwitcherViewModel switcherViewModel)
    {
        _serviceProvider = serviceProvider;
        _deviceManager = deviceManager;
        _settingsService = settingsService;
        _recordingService = recordingService;
        _clippingService = clippingService;
        _schedulingService = schedulingService;
        _logger = logger;
        _audioPreviewService = audioPreviewService;
        _streamingService = streamingService;
        _outputManager = outputManager;
        _switcherService = switcherService;
        _autoCutService = autoCutService;
        _golfSession = golfSession;
        _sequenceRecorder = sequenceRecorder;
        _clipExportService = clipExportService;
        _golferRepository = golferRepository;
        _uploadService = uploadService;
        _panelRelayService = panelRelayService;
        _transitionEngine = transitionEngine;
        Switcher = switcherViewModel;

        // Check NDI availability
        var ndiOutput = _outputManager.GetOutput("ndi-output");
        IsNdiAvailable = ndiOutput != null;
        RecordingControls = recordingControls;
        Timecode = timecode;
        AudioMeters = audioMeters;
        DriveStatus = driveStatus;
        ClipBin = clipBin;
        UploadQueue = uploadQueue;
        InputConfiguration = inputConfiguration;

        // Subscribe to recording service events
        _recordingService.StateChanged += OnRecordingStateChanged;
        _recordingService.Progress += OnRecordingProgress;

        // Subscribe to clipping service events
        _clippingService.MarkerAdded += OnClipMarkerAdded;

        // Subscribe to scheduling service events
        _schedulingService.ScheduleStarting += OnScheduleStarting;
        _schedulingService.ScheduleEnded += OnScheduleEnded;

        // Subscribe to streaming service events
        _streamingService.ClientConnected += (_, e) =>
            Application.Current.Dispatcher.Invoke(() => ConnectedViewers = _streamingService.ConnectedClients.Count);
        _streamingService.ClientDisconnected += (_, e) =>
            Application.Current.Dispatcher.Invoke(() => ConnectedViewers = _streamingService.ConnectedClients.Count);

        // Subscribe to input selection changes (for audio routing)
        InputConfiguration.SelectedInputChanged += OnSelectedInputChanged;

        // Subscribe to each input's IsEnabled changes for multi-view management
        foreach (var input in InputConfiguration.Inputs)
        {
            input.PropertyChanged += OnInputPropertyChanged;
        }

        // Wire up golf mode events
        _switcherService.ProgramSourceChanged += OnProgramSourceChanged;
        _autoCutService.CutTriggered += OnAutoCutTriggered;
        _autoCutService.StateChanged += OnAutoCutStateChanged;
        _golfSession.SwingCountChanged += (_, count) =>
            Application.Current.Dispatcher.Invoke(() => SwingCount = count);
        _sequenceRecorder.SequenceStarted += (_, seq) =>
            _golfSession.IncrementSwingCount();
        _sequenceRecorder.SequenceCompleted += OnSequenceCompleted;

        // Wire clip export pipeline
        _clipExportService.WireUp(_sequenceRecorder);
        _clipExportService.ExportProgressChanged += OnExportProgressChanged;
        _clipExportService.ExportCompleted += OnExportCompleted;

        // Load golfer list and auto-upload settings
        _ = LoadGolfersAsync();
        _ = LoadAutoUploadSettingsAsync();

        // Initialize available drives
        RefreshDrives();

        // Start clock timer
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        timer.Tick += (s, e) => CurrentTime = DateTime.Now;
        timer.Start();

        // Start multi-view preview for enabled inputs
        _ = RebuildPreviewRenderersAsync();
    }

    /// <summary>
    /// Rebuild all preview renderers for currently enabled inputs.
    /// Called on startup and whenever an input's IsEnabled state changes.
    /// </summary>
    private async Task RebuildPreviewRenderersAsync()
    {
        try
        {
            // Detach audio from current device
            DetachAudio();
            _audioPreviewService.Stop();

            // Stop and dispose all existing renderers
            foreach (var renderer in _previewRenderers.Values)
            {
                await renderer.StopAsync();
                renderer.Dispose();
            }
            _previewRenderers.Clear();
            EnabledInputs.Clear();

            var enabled = InputConfiguration.Inputs.Where(i => i.IsEnabled).ToList();
            if (enabled.Count == 0)
            {
                PreviewColumns = 1;
                HasNoSignal = true;
                InputStatus = "No Signal";
                InputStatusText = "No inputs enabled";
                _logger.LogInformation("No inputs enabled, preview stopped");
                return;
            }

            // Quarter-res (480x270) when 2+ inputs, half-res (960x540) for single input
            int resDivisor = enabled.Count > 1 ? 4 : 2;

            _logger.LogInformation("Starting multi-view preview: {Count} input(s), res divisor={Div}",
                enabled.Count, resDivisor);

            var selectedInput = InputConfiguration.SelectedInput;
            foreach (var input in enabled)
            {
                var renderer = new InputPreviewRenderer(input);
                input.PreviewRenderer = renderer;
                _previewRenderers[input.DeviceId] = renderer;
                renderer.SetOutputManager(_outputManager, input == selectedInput);
                await renderer.StartAsync(input.DeviceId, input.Connector, _deviceManager, resDivisor);
                EnabledInputs.Add(input);
            }

            PreviewColumns = enabled.Count <= 1 ? 1 : 2;
            HasNoSignal = false;

            // Bind source preview images for the two-source switcher layout
            BindSourcePreviews(enabled);

            // Wire BGRA callbacks and start rendering for program monitor output
            WireUpSwitcherRendering();

            // Start audio on selected input
            _audioPreviewService.Start();
            var selected = InputConfiguration.SelectedInput;
            if (selected != null && selected.IsEnabled)
            {
                AttachAudio(selected);
            }
            else if (enabled.Count > 0)
            {
                // Select the first enabled input if current selection is not enabled
                InputConfiguration.SelectedInput = enabled[0];
            }

            UpdateInputStatus(InputConfiguration.SelectedInput);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebuilding preview renderers");
        }
    }

    /// <summary>
    /// Bind the first two enabled inputs to Source1/Source2 preview images for the switcher layout.
    /// Works regardless of golf mode — in golf mode, WireUpGolfFrameCallbacks rebinds by role.
    /// </summary>
    private void BindSourcePreviews(List<InputViewModel> enabled)
    {
        // Unsubscribe from previous inputs
        if (_boundSource1 != null)
            _boundSource1.PropertyChanged -= OnSource1PropertyChanged;
        if (_boundSource2 != null)
            _boundSource2.PropertyChanged -= OnSource2PropertyChanged;

        _boundSource1 = null;
        _boundSource2 = null;
        Source1PreviewImage = null;
        Source2PreviewImage = null;
        Source1HasSignal = false;
        Source2HasSignal = false;

        if (enabled.Count > 0)
        {
            _boundSource1 = enabled[0];
            Source1PreviewImage = _boundSource1.PreviewImage;
            Source1HasSignal = _boundSource1.HasSignal;
            _boundSource1.PropertyChanged += OnSource1PropertyChanged;
        }

        if (enabled.Count > 1)
        {
            _boundSource2 = enabled[1];
            Source2PreviewImage = _boundSource2.PreviewImage;
            Source2HasSignal = _boundSource2.HasSignal;
            _boundSource2.PropertyChanged += OnSource2PropertyChanged;
        }

        UpdatePreviewSource();
    }

    /// <summary>
    /// Wire BGRA frame callbacks from the first two enabled inputs to the TransitionEngine
    /// and start the rendering loop. Enables Program monitor output regardless of golf mode.
    /// In golf mode, WireUpGolfFrameCallbacks overwrites these with role-based callbacks.
    /// </summary>
    private void WireUpSwitcherRendering()
    {
        var enabled = InputConfiguration.Inputs.Where(i => i.IsEnabled).ToList();

        if (enabled.Count > 0 && enabled[0].PreviewRenderer != null)
        {
            enabled[0].PreviewRenderer.SetBgraFrameCallback((bgra, w, h) =>
            {
                _transitionEngine.SetSource(0, bgra, w, h);
                Switcher?.SetProgramDimensions(w, h);
            });
        }

        if (enabled.Count > 1 && enabled[1].PreviewRenderer != null)
        {
            enabled[1].PreviewRenderer.SetBgraFrameCallback((bgra, w, h) =>
            {
                _transitionEngine.SetSource(1, bgra, w, h);
                Switcher?.SetProgramDimensions(w, h);
            });
        }

        Switcher?.StartRendering();
    }

    private void OnSource1PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not InputViewModel input) return;
        if (e.PropertyName == nameof(InputViewModel.PreviewImage))
            Application.Current?.Dispatcher.BeginInvoke(() => { Source1PreviewImage = input.PreviewImage; UpdatePreviewSource(); });
        else if (e.PropertyName == nameof(InputViewModel.HasSignal))
            Application.Current?.Dispatcher.BeginInvoke(() => { Source1HasSignal = input.HasSignal; UpdatePreviewSource(); });
    }

    private void OnSource2PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not InputViewModel input) return;
        if (e.PropertyName == nameof(InputViewModel.PreviewImage))
            Application.Current?.Dispatcher.BeginInvoke(() => { Source2PreviewImage = input.PreviewImage; UpdatePreviewSource(); });
        else if (e.PropertyName == nameof(InputViewModel.HasSignal))
            Application.Current?.Dispatcher.BeginInvoke(() => { Source2HasSignal = input.HasSignal; UpdatePreviewSource(); });
    }

    /// <summary>
    /// Update the preview monitor to show whichever source is NOT currently on program.
    /// </summary>
    private void UpdatePreviewSource()
    {
        // If the user manually selected a preview input, show that
        var manual = _manualPreviewInput;
        if (manual != null)
        {
            PreviewSourceImage = manual.PreviewImage;
            PreviewSourceHasSignal = manual.HasSignal;
            OnPropertyChanged(nameof(PreviewSourceName));
            return;
        }

        // Default A/B swap: show whichever source is NOT on program
        if (ActiveSourceIndex == 0)
        {
            PreviewSourceImage = Source2PreviewImage;
            PreviewSourceHasSignal = Source2HasSignal;
        }
        else
        {
            PreviewSourceImage = Source1PreviewImage;
            PreviewSourceHasSignal = Source1HasSignal;
        }
        OnPropertyChanged(nameof(PreviewSourceName));
    }

    private void OnInputPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InputViewModel.IsEnabled))
        {
            // Auto-unlock audio if the locked source is disabled
            if (sender is InputViewModel changedInput && !changedInput.IsEnabled
                && IsAudioLocked && _audioLockedInput == changedInput)
            {
                UnlockAudio();
            }

            _ = RebuildPreviewRenderersAsync();
        }
        else if (e.PropertyName == nameof(InputViewModel.HasSignal) || e.PropertyName == nameof(InputViewModel.FormatDescription))
        {
            // Update status bar when the selected input's signal/format changes
            if (sender == InputConfiguration.SelectedInput)
            {
                UpdateInputStatus(InputConfiguration.SelectedInput);
            }
        }
    }

    private void OnSelectedInputChanged(object? sender, InputViewModel? input)
    {
        if (input == null) return;

        _logger.LogInformation("Selected input changed to: {Input} ({DeviceId})",
            input.ShortName, input.DeviceId);

        // Only switch audio if not locked to a specific source
        if (!IsAudioLocked)
        {
            DetachAudio();
            AttachAudio(input);
        }

        UpdateInputStatus(input);

        // Update output selection on all renderers
        foreach (var kvp in _previewRenderers)
            kvp.Value.SetOutputManager(_outputManager, kvp.Key == input.DeviceId);

        // Rebind fullscreen preview to new selected input
        if (_fullscreenWindow != null)
        {
            _fullscreenWindow.SetBinding(
                FullscreenPreviewWindow.PreviewImageProperty,
                new System.Windows.Data.Binding(nameof(InputViewModel.PreviewImage))
                {
                    Source = input
                });
        }

        // Refresh schedule bindings for newly selected input
        NotifySchedulePropertiesChanged();
    }

    private void AttachAudio(InputViewModel? input)
    {
        if (input == null) return;

        if (_previewRenderers.TryGetValue(input.DeviceId, out var renderer) && renderer.Device != null)
        {
            _audioDevice = renderer.Device;
            _audioDevice.AudioSamplesReceived += OnAudioSamplesReceived;
            _logger.LogInformation("Audio attached to {Input}", input.ShortName);
        }
    }

    private void DetachAudio()
    {
        if (_audioDevice != null)
        {
            _audioDevice.AudioSamplesReceived -= OnAudioSamplesReceived;
            _audioDevice = null;
        }
    }

    public void LockAudioToSource(InputViewModel input)
    {
        DetachAudio();
        AttachAudio(input);
        _audioLockedInput = input;
        IsAudioLocked = true;
        _logger.LogInformation("Audio locked to {Input}", input.ShortName);
    }

    public void UnlockAudio()
    {
        _audioLockedInput = null;
        IsAudioLocked = false;
        DetachAudio();
        AttachAudio(InputConfiguration.SelectedInput);
        _logger.LogInformation("Audio unlocked, following selected input");
    }

    [RelayCommand]
    private void ToggleAudioLock()
    {
        if (IsAudioLocked)
        {
            UnlockAudio();
        }
        else if (InputConfiguration.SelectedInput is { } selected)
        {
            LockAudioToSource(selected);
        }
    }

    private void OnAudioSamplesReceived(object? sender, AudioSamplesEventArgs e)
    {
        _audioPreviewService.Configure(e.SampleRate, e.Channels, e.BitsPerSample);
        _audioPreviewService.WriteSamples(e.SampleData.Span);
    }

    private void UpdateInputStatus(InputViewModel? input)
    {
        if (input == null)
        {
            HasNoSignal = true;
            InputStatus = "No Signal";
            InputStatusText = "No Input";
            return;
        }

        HasNoSignal = EnabledInputs.Count == 0;
        InputStatus = input.HasSignal ? "Connected" : "No Signal";
        InputStatusText = input.HasSignal && !string.IsNullOrEmpty(input.FormatDescription)
            ? $"Signal: {input.FormatDescription}"
            : "No Signal";
    }

    [RelayCommand]
    private void SelectInput(InputViewModel input)
    {
        InputConfiguration.SelectInput(input);

        // Unsubscribe from previous manual preview input
        if (_manualPreviewInput != null)
            _manualPreviewInput.PropertyChanged -= OnManualPreviewPropertyChanged;

        // Show the selected input in the Preview monitor
        _manualPreviewInput = input;
        _manualPreviewInput.PropertyChanged += OnManualPreviewPropertyChanged;

        // Update IsPreviewSource on all inputs
        foreach (var inp in EnabledInputs)
            inp.IsPreviewSource = (inp == _manualPreviewInput);

        UpdatePreviewSource();
        OnPropertyChanged(nameof(PreviewSourceName));
    }

    private void OnManualPreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InputViewModel.PreviewImage) || e.PropertyName == nameof(InputViewModel.HasSignal))
            Application.Current?.Dispatcher.BeginInvoke(() => UpdatePreviewSource());
    }

    private InputScheduleState GetCurrentScheduleState()
    {
        var deviceId = InputConfiguration.SelectedInput?.DeviceId ?? "__none__";
        if (!_inputScheduleStates.TryGetValue(deviceId, out var state))
        {
            state = new InputScheduleState();
            _inputScheduleStates[deviceId] = state;
        }
        return state;
    }

    private void NotifySchedulePropertiesChanged()
    {
        OnPropertyChanged(nameof(ScheduledStartTime));
        OnPropertyChanged(nameof(ScheduledDurationHours));
        OnPropertyChanged(nameof(ScheduledDurationMinutes));
        OnPropertyChanged(nameof(ScheduleStatusText));
        OnPropertyChanged(nameof(HasActiveSchedule));
    }

    private void RefreshDrives()
    {
        AvailableDrives.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            AvailableDrives.Add(new DriveViewModel
            {
                Name = $"{drive.Name} {drive.VolumeLabel}",
                RootPath = drive.RootDirectory.FullName,
                TotalSize = drive.TotalSize,
                FreeSpace = drive.AvailableFreeSpace
            });
        }
        SelectedDrive = AvailableDrives.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopRecordingAsync();
        }
        else
        {
            await StartRecordingAsync();
        }
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            RecordingStatusText = "Starting...";

            var outputDir = SelectedDrive?.RootPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            outputDir = Path.Combine(outputDir, "Screener");

            // Build filename template including the recording name if provided
            var filenameTemplate = string.IsNullOrWhiteSpace(RecordingName)
                ? "{datetime}_{preset}"
                : "{name}_{datetime}";

            // Get enabled inputs for multi-input recording
            var enabledInputs = InputConfiguration.GetEnabledInputs();
            if (enabledInputs.Count == 0)
            {
                RecordingStatusText = "No inputs enabled";
                return;
            }

            var options = new RecordingOptions(
                OutputDirectory: outputDir,
                FilenameTemplate: filenameTemplate,
                Preset: SelectedPreset,
                Name: string.IsNullOrWhiteSpace(RecordingName) ? null : RecordingName,
                Inputs: enabledInputs);

            _logger.LogInformation("Starting recording with {InputCount} input(s)", enabledInputs.Count);
            await _recordingService.StartRecordingAsync(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            RecordingStatusText = $"Error: {ex.Message}";
            IsRecording = false;
        }
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        try
        {
            RecordingStatusText = "Stopping...";
            await _recordingService.StopRecordingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop recording");
            RecordingStatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        if (!IsRecording) return;

        try
        {
            if (IsPaused)
            {
                await _recordingService.ResumeRecordingAsync();
            }
            else
            {
                await _recordingService.PauseRecordingAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle pause");
        }
    }

    private void OnRecordingStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecordingState = e.NewState;
            IsRecording = e.NewState is RecordingState.Recording or RecordingState.Paused;
            IsPaused = e.NewState == RecordingState.Paused;

            RecordingStatusText = e.NewState switch
            {
                RecordingState.Stopped => "Ready",
                RecordingState.Starting => "Starting...",
                RecordingState.Recording => "Recording...",
                RecordingState.Paused => "Paused",
                RecordingState.Stopping => "Stopping...",
                _ => "Ready"
            };

            // Set active recording for clipping when recording starts
            if (e.NewState == RecordingState.Recording && e.Session != null)
            {
                var filePath = e.Session.InputSessions.Count > 0
                    ? e.Session.InputSessions[0].FilePath
                    : e.Session.FilePath;
                if (!string.IsNullOrEmpty(filePath))
                    _clippingService.SetActiveRecording(filePath);
            }

            // Clear markers when recording stops
            if (e.NewState == RecordingState.Stopped)
            {
                _clippingService.ClearMarkers();
                ClipBin.LiveMarkers.Clear();
            }

            // Add completed recording(s) to clip bin
            if (e.NewState == RecordingState.Stopped && e.Session != null)
            {
                RecordingDuration = TimeSpan.Zero;
                AddSessionToClipBin(e.Session);
            }
            else if (e.NewState == RecordingState.Stopped)
            {
                RecordingDuration = TimeSpan.Zero;
            }

            OnPropertyChanged(nameof(RecordingBorderBrush));
            OnPropertyChanged(nameof(RecordingBorderThickness));
        });
    }

    private void AddSessionToClipBin(RecordingSession session)
    {
        if (session.InputSessions.Count > 0)
        {
            // Multi-input: add each input as a separate clip
            foreach (var inputSession in session.InputSessions)
            {
                var fileInfo = File.Exists(inputSession.FilePath)
                    ? new FileInfo(inputSession.FilePath) : null;

                if (fileInfo == null || fileInfo.Length == 0)
                    continue;

                ClipBin.AddClip(new ClipViewModel
                {
                    Name = Path.GetFileNameWithoutExtension(inputSession.FilePath),
                    FilePath = inputSession.FilePath,
                    Duration = session.Duration,
                    FileSizeBytes = fileInfo.Length,
                    CreatedAt = session.StartTimeUtc.ToLocalTime()
                });
            }
        }
        else if (File.Exists(session.FilePath))
        {
            // Single-input
            var fileInfo = new FileInfo(session.FilePath);
            if (fileInfo.Length == 0)
                return;

            ClipBin.AddClip(new ClipViewModel
            {
                Name = Path.GetFileNameWithoutExtension(session.FilePath),
                FilePath = session.FilePath,
                Duration = session.Duration,
                FileSizeBytes = fileInfo.Length,
                CreatedAt = session.StartTimeUtc.ToLocalTime()
            });
        }
    }

    private void OnRecordingProgress(object? sender, RecordingProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RecordingDuration = e.Duration;
            DroppedFrames = e.DroppedFrames;
            DroppedFramesColor = e.DroppedFrames > 0 ? Brushes.Red : Brushes.Gray;
        });
    }

    private void OnClipMarkerAdded(object? sender, ClipMarkerEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var marker = e.Marker;
            ClipBin.LiveMarkers.Add(new MarkerViewModel
            {
                Name = marker.Name ?? marker.Type.ToString(),
                Position = marker.Position,
                Timecode = marker.Timecode.ToString(),
                MarkerType = marker.Type.ToString()
            });
        });
    }

    private async void OnScheduleStarting(object? sender, ScheduleEventArgs e)
    {
        _logger.LogInformation("Scheduled recording starting: {Name}", e.Schedule.Name);

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var schedule = e.Schedule;
                var deviceId = schedule.InputDeviceIds?.FirstOrDefault();

                // Track active schedule → device mapping
                if (deviceId != null)
                    _activeScheduleIds[schedule.Id] = deviceId;

                // Update per-input state
                if (deviceId != null && _inputScheduleStates.TryGetValue(deviceId, out var state))
                {
                    state.StatusText = $"Recording: {schedule.Name}";
                }

                // Refresh UI if this is the currently selected input
                if (InputConfiguration.SelectedInput?.DeviceId == deviceId)
                    NotifySchedulePropertiesChanged();

                RecordingName = schedule.Name;

                var outputDir = SelectedDrive?.RootPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                outputDir = Path.Combine(outputDir, "Screener");

                // Build inputs list from schedule's InputDeviceIds
                List<InputConfiguration>? inputs = null;
                if (schedule.InputDeviceIds is { Count: > 0 })
                {
                    inputs = InputConfiguration.Inputs
                        .Where(i => schedule.InputDeviceIds.Contains(i.DeviceId))
                        .Select(i => new Abstractions.Recording.InputConfiguration(
                            i.DeviceId, i.DisplayName, i.Connector, i.InputIndex, true))
                        .ToList();

                    _logger.LogInformation("Starting recording with {InputCount} input(s)", inputs.Count);
                }

                var options = new RecordingOptions(
                    OutputDirectory: outputDir,
                    FilenameTemplate: schedule.FilenameTemplate,
                    Preset: schedule.Preset,
                    Name: schedule.Name,
                    MaxDuration: schedule.Duration,
                    Inputs: inputs);

                await _recordingService.StartRecordingAsync(options);

                // Set up timer to stop recording after duration
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = schedule.Duration
                };
                timer.Tick += async (s, args) =>
                {
                    timer.Stop();
                    await StopScheduledRecordingAsync(schedule.Id);
                };
                timer.Start();
                _scheduleDurationTimers[schedule.Id] = timer;

                // Mark schedule as run
                if (_schedulingService is SchedulingService schedulingSvc)
                {
                    schedulingSvc.MarkScheduleRun(schedule.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start scheduled recording");
                var deviceId = e.Schedule.InputDeviceIds?.FirstOrDefault();
                if (deviceId != null && _inputScheduleStates.TryGetValue(deviceId, out var state))
                    state.StatusText = $"Failed: {ex.Message}";

                if (InputConfiguration.SelectedInput?.DeviceId == deviceId)
                    NotifySchedulePropertiesChanged();
            }
        });
    }

    private async Task StopScheduledRecordingAsync(Guid scheduleId)
    {
        if (!_activeScheduleIds.ContainsKey(scheduleId)) return;

        _logger.LogInformation("Stopping scheduled recording: {ScheduleId}", scheduleId);

        await _recordingService.StopRecordingAsync();
        ClearScheduleState(scheduleId);
    }

    private void OnScheduleEnded(object? sender, ScheduleEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ClearScheduleState(e.Schedule.Id);
        });
    }

    private void ClearScheduleState(Guid scheduleId)
    {
        if (_activeScheduleIds.TryGetValue(scheduleId, out var deviceId))
        {
            _activeScheduleIds.Remove(scheduleId);

            // Clean up timer
            if (_scheduleDurationTimers.TryGetValue(scheduleId, out var timer))
            {
                timer.Stop();
                _scheduleDurationTimers.Remove(scheduleId);
            }

            // Clear per-input state
            if (_inputScheduleStates.TryGetValue(deviceId, out var state))
            {
                state.ActiveScheduleId = null;
                state.StatusText = null;
            }

            // Clear HasSchedule on the input
            var input = InputConfiguration.Inputs.FirstOrDefault(i => i.DeviceId == deviceId);
            if (input != null)
                input.HasSchedule = false;

            // Refresh UI if this is the currently selected input
            if (InputConfiguration.SelectedInput?.DeviceId == deviceId)
                NotifySchedulePropertiesChanged();
        }
    }

    [RelayCommand]
    private async Task CreateQuickScheduleAsync()
    {
        try
        {
            var selectedInput = InputConfiguration.SelectedInput;
            if (selectedInput == null)
            {
                ScheduleStatusText = "No input selected";
                return;
            }

            var duration = TimeSpan.FromHours(ScheduledDurationHours) + TimeSpan.FromMinutes(ScheduledDurationMinutes);
            if (duration <= TimeSpan.Zero)
            {
                ScheduleStatusText = "Duration must be greater than zero";
                return;
            }

            var startTime = new DateTimeOffset(ScheduledStartTime);
            if (startTime <= DateTimeOffset.Now)
            {
                ScheduleStatusText = "Start time must be in the future";
                return;
            }

            // Check for conflicts
            var conflicts = await _schedulingService.CheckConflictsAsync(startTime, duration);
            if (conflicts.Count > 0)
            {
                ScheduleStatusText = $"Conflicts with: {conflicts[0].ExistingSchedule.Name}";
                return;
            }

            var inputName = selectedInput.ShortName;
            var name = string.IsNullOrWhiteSpace(RecordingName)
                ? $"{inputName}_{startTime:HHmm}"
                : $"{RecordingName}_{inputName}";

            var request = new ScheduledRecordingRequest(
                Name: name,
                StartTime: startTime,
                Duration: duration,
                Preset: SelectedPreset,
                FilenameTemplate: string.IsNullOrWhiteSpace(RecordingName)
                    ? "{datetime}_{preset}"
                    : "{name}_{datetime}",
                InputDeviceIds: new List<string> { selectedInput.DeviceId });

            var schedule = await _schedulingService.CreateScheduleAsync(request);

            // Store schedule ID in per-input state
            var state = GetCurrentScheduleState();
            state.ActiveScheduleId = schedule.Id;
            state.StatusText = $"Scheduled: {name} at {startTime:HH:mm}";
            OnPropertyChanged(nameof(HasActiveSchedule));
            OnPropertyChanged(nameof(ScheduleStatusText));

            // Mark input as having a schedule
            selectedInput.HasSchedule = true;

            _logger.LogInformation("Created quick schedule: {Name} at {StartTime} for {Duration} (input: {Input})",
                name, startTime, duration, inputName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create quick schedule");
            ScheduleStatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CancelScheduleAsync()
    {
        var state = GetCurrentScheduleState();
        if (state.ActiveScheduleId.HasValue)
        {
            var scheduleId = state.ActiveScheduleId.Value;
            await _schedulingService.DeleteScheduleAsync(scheduleId);
            _activeScheduleIds.Remove(scheduleId);

            state.ActiveScheduleId = null;
            state.StatusText = null;
            OnPropertyChanged(nameof(HasActiveSchedule));
            OnPropertyChanged(nameof(ScheduleStatusText));

            // Clear HasSchedule on the input
            var selectedInput = InputConfiguration.SelectedInput;
            if (selectedInput != null)
                selectedInput.HasSchedule = false;
        }
    }

    [RelayCommand]
    private void AddMarker()
    {
        if (!IsRecording) return;

        var position = RecordingDuration;
        var timecode = Timecode.CurrentTimecode;
        var name = $"Marker {ClipBin.LiveMarkers.Count + 1}";

        _clippingService.AddChapterMarker(position, timecode, name);
        _logger.LogInformation("Chapter marker added at {Position}", position);
    }

    [RelayCommand]
    private void SetInPoint()
    {
        if (!IsRecording) return;

        var position = RecordingDuration;
        var timecode = Timecode.CurrentTimecode;

        _clippingService.SetInPoint(position, timecode);
        _logger.LogInformation("In point set at {Position}", position);
    }

    [RelayCommand]
    private void SetOutPoint()
    {
        if (!IsRecording) return;

        var position = RecordingDuration;
        var timecode = Timecode.CurrentTimecode;

        _clippingService.SetOutPoint(position, timecode);
        _logger.LogInformation("Out point set at {Position}", position);
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        if (_fullscreenWindow != null)
        {
            _fullscreenWindow.Close();
            return;
        }

        _fullscreenWindow = new FullscreenPreviewWindow();

        // Bind PreviewImage to the currently selected input's preview
        var selectedInput = InputConfiguration.SelectedInput;
        if (selectedInput != null)
        {
            _fullscreenWindow.SetBinding(
                FullscreenPreviewWindow.PreviewImageProperty,
                new System.Windows.Data.Binding(nameof(InputViewModel.PreviewImage))
                {
                    Source = selectedInput
                });
        }

        _fullscreenWindow.Closed += (_, _) => _fullscreenWindow = null;
        _fullscreenWindow.Show();
    }

    [RelayCommand]
    private void ExitFullscreen()
    {
        _fullscreenWindow?.Close();
        _fullscreenWindow = null;
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
        settingsWindow.Owner = Application.Current.MainWindow;
        var result = settingsWindow.ShowDialog();

        // If settings were saved, rebuild preview renderers
        if (result == true)
        {
            _logger.LogInformation("Settings saved, rebuilding preview renderers...");
            await RebuildPreviewRenderersAsync();
        }
    }

    [RelayCommand]
    private void OpenScheduler()
    {
        var schedulerWindow = _serviceProvider.GetRequiredService<SchedulerWindow>();
        schedulerWindow.Owner = Application.Current.MainWindow;
        schedulerWindow.ShowDialog();
    }

    private bool _streamingChanging;

    partial void OnIsStreamingChanged(bool value)
    {
        if (_streamingChanging) return;
        _streamingChanging = true;

        if (value)
        {
            _ = StartStreamingAsync().ContinueWith(_ => _streamingChanging = false);
        }
        else
        {
            _ = StopStreamingAsync().ContinueWith(_ => _streamingChanging = false);
        }
    }

    private async Task StartStreamingAsync()
    {
        try
        {
            // Reset error state so we can restart
            if (_streamingService.State == StreamingState.Error)
                await _streamingService.StopAsync();

            var settings = await _settingsService.GetSettingsAsync();

            var (width, height, fps) = settings.StreamingResolution switch
            {
                "480p" => (854, 480, 30),
                "1080p" => (1920, 1080, 30),
                _ => (1280, 720, 30) // "720p" default
            };

            var quality = new StreamQualitySettings(width, height, fps, settings.StreamingBitrate);
            var config = new StreamingConfiguration(
                settings.StreamingPort,
                quality,
                settings.MaxViewers,
                settings.RequireAccessToken ? settings.AccessToken : null);

            await _streamingService.StartAsync(config);

            // Wire panel relay and lower third callback into streaming service
            if (_streamingService is WebRtcStreamingService webRtcService)
            {
                webRtcService.SetPanelRelay(_panelRelayService);
                webRtcService.SetLowerThirdCallback(text =>
                {
                    Application.Current?.Dispatcher.Invoke(() => Switcher.LowerThirdText = text);
                });
            }

            // Start panel relay if configured
            await StartPanelRelayAsync();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StreamUrl = _streamingService.SignalingUri?.ToString();
            });
            _logger.LogInformation("Streaming started at {Url}", StreamUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start streaming");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _streamingChanging = true;
                IsStreaming = false;
                StreamUrl = $"Error: {ex.Message}";
                _streamingChanging = false;
            });
        }
    }

    private async Task StopStreamingAsync()
    {
        try
        {
            _panelRelayService.Stop();
            await _streamingService.StopAsync();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StreamUrl = null;
                ConnectedViewers = 0;
            });
            _logger.LogInformation("Streaming stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop streaming");
        }
    }

    private async Task StartPanelRelayAsync()
    {
        try
        {
            var settingsRepo = _serviceProvider.GetService<SettingsRepository>();
            if (settingsRepo == null) return;

            var enabled = await settingsRepo.GetAsync<bool>(SettingsKeys.PanelRelayEnabled);
            if (!enabled) return;

            var panelUrl = await settingsRepo.GetAsync<string>(SettingsKeys.PanelRelayUrl);
            var apiKey = await settingsRepo.GetAsync<string>(SettingsKeys.PanelRelayApiKey);

            if (string.IsNullOrEmpty(panelUrl) || string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Panel relay enabled but URL or API key not configured");
                return;
            }

            _panelRelayService.Start(panelUrl, apiKey, () => new
            {
                type = "status",
                streamingState = _streamingService.State.ToString(),
                connectedViewers = _streamingService.ConnectedClients.Count,
                isRecording = IsRecording,
                swingCount = _golfSession.TotalSwings,
                golferName = _golfSession.CurrentSession?.GolferDisplayName,
                autoCutState = _autoCutService.State.ToString(),
                isSessionActive = _golfSession.IsSessionActive
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start panel relay");
        }
    }

    [RelayCommand]
    private void ToggleStream()
    {
        IsStreaming = !IsStreaming;
    }

    [RelayCommand]
    private void CopyStreamUrl()
    {
        var uri = _streamingService.SignalingUri;
        if (uri != null)
        {
            Clipboard.SetText(uri.ToString());
            _logger.LogInformation("Stream URL copied to clipboard");
        }
    }

    [RelayCommand]
    private void OpenStreamSettings()
    {
        var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
        settingsWindow.Owner = Application.Current.MainWindow;
        settingsWindow.SelectTab(4); // Streaming tab index
        settingsWindow.ShowDialog();
    }

    [RelayCommand]
    private void ShowQrCode()
    {
        if (IsQrCodeVisible)
        {
            IsQrCodeVisible = false;
            return;
        }

        QrCodeImage = _streamingService.GenerateConnectionQrCode();
        IsQrCodeVisible = true;
    }

    [RelayCommand]
    private void ShowViewers()
    {
        if (IsViewersPopupVisible)
        {
            IsViewersPopupVisible = false;
            return;
        }

        ConnectedViewersList.Clear();
        foreach (var client in _streamingService.ConnectedClients)
        {
            ConnectedViewersList.Add(new ViewerInfo
            {
                Id = client.Id,
                RemoteAddress = client.RemoteAddress,
                ConnectedAt = client.ConnectedAt,
            });
        }
        IsViewersPopupVisible = true;
    }

    [RelayCommand]
    private async Task DisconnectViewerAsync(string clientId)
    {
        await _streamingService.DisconnectClientAsync(clientId);
        ConnectedViewersList.Remove(ConnectedViewersList.FirstOrDefault(v => v.Id == clientId)!);
        ConnectedViewers = _streamingService.ConnectedClients.Count;
    }

    // NDI Output toggle
    private bool _ndiOutputChanging;

    partial void OnIsNdiOutputActiveChanged(bool value)
    {
        if (_ndiOutputChanging) return;
        _ndiOutputChanging = true;

        if (value)
        {
            _ = StartNdiOutputAsync().ContinueWith(_ => _ndiOutputChanging = false);
        }
        else
        {
            _ = StopNdiOutputAsync().ContinueWith(_ => _ndiOutputChanging = false);
        }
    }

    private async Task StartNdiOutputAsync()
    {
        try
        {
            var ndiOutput = _outputManager.GetOutput("ndi-output");
            if (ndiOutput == null) return;

            var settings = await _settingsService.GetSettingsAsync();
            var config = new OutputConfiguration(
                settings.NdiOutputSourceName,
                1920, 1080, 59.94);

            await ndiOutput.StartAsync(config);
            _logger.LogInformation("NDI output started: {Name}", config.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start NDI output");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _ndiOutputChanging = true;
                IsNdiOutputActive = false;
                _ndiOutputChanging = false;
            });
        }
    }

    private async Task StopNdiOutputAsync()
    {
        try
        {
            var ndiOutput = _outputManager.GetOutput("ndi-output");
            if (ndiOutput != null)
                await ndiOutput.StopAsync();
            _logger.LogInformation("NDI output stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop NDI output");
        }
    }

    [RelayCommand]
    private void ToggleNdiOutput()
    {
        IsNdiOutputActive = !IsNdiOutputActive;
    }

    // SRT Output toggle
    private bool _srtOutputChanging;

    partial void OnIsSrtOutputActiveChanged(bool value)
    {
        if (_srtOutputChanging) return;
        _srtOutputChanging = true;

        if (value)
        {
            _ = StartSrtOutputAsync().ContinueWith(_ => _srtOutputChanging = false);
        }
        else
        {
            _ = StopSrtOutputAsync().ContinueWith(_ => _srtOutputChanging = false);
        }
    }

    private async Task StartSrtOutputAsync()
    {
        try
        {
            var srtOutput = _outputManager.GetOutput("srt-output");
            if (srtOutput == null) return;

            var settings = await _settingsService.GetSettingsAsync();
            var config = new OutputConfiguration(
                "SRT Output",
                1920, 1080, 29.97,
                new Dictionary<string, string>
                {
                    ["mode"] = settings.SrtOutputMode,
                    ["address"] = settings.SrtOutputAddress,
                    ["port"] = settings.SrtOutputPort.ToString(),
                    ["latency"] = settings.SrtOutputLatency.ToString(),
                    ["bitrate"] = settings.SrtOutputBitrate.ToString()
                });

            await srtOutput.StartAsync(config);
            SrtOutputStatus = $"SRT: {settings.SrtOutputMode} {settings.SrtOutputAddress}:{settings.SrtOutputPort}";
            _logger.LogInformation("SRT output started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SRT output");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _srtOutputChanging = true;
                IsSrtOutputActive = false;
                SrtOutputStatus = $"Error: {ex.Message}";
                _srtOutputChanging = false;
            });
        }
    }

    private async Task StopSrtOutputAsync()
    {
        try
        {
            var srtOutput = _outputManager.GetOutput("srt-output");
            if (srtOutput != null)
                await srtOutput.StopAsync();
            SrtOutputStatus = null;
            _logger.LogInformation("SRT output stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop SRT output");
        }
    }

    [RelayCommand]
    private void ToggleSrtOutput()
    {
        IsSrtOutputActive = !IsSrtOutputActive;
    }

    // ========== Golf Mode Commands ==========

    [RelayCommand]
    private void CutToSource1()
    {
        if (!IsGolfModeEnabled) return;
        if (_switcherService.ActiveSourceIndex == 0) return; // Already on source 1
        if (Switcher != null)
            Switcher.TriggerTransition(TransitionType.Cut);
        else
            _switcherService.CutToSource(0, "manual");
    }

    [RelayCommand]
    private void CutToSource2()
    {
        if (!IsGolfModeEnabled) return;
        if (_switcherService.ActiveSourceIndex == 1) return; // Already on source 2
        if (Switcher != null)
            Switcher.TriggerTransition(TransitionType.Cut);
        else
            _switcherService.CutToSource(1, "manual");
    }

    partial void OnIsGolfModeEnabledChanged(bool value)
    {
        _switcherService.IsGolfModeEnabled = value;
        if (value)
        {
            _logger.LogInformation("Golf mode enabled");
            // Start auto-cut tick timer
            _autoCutTickTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _autoCutTickTimer.Tick += (s, e) => _autoCutService.Tick();
            _autoCutTickTimer.Start();
            WireUpGolfFrameCallbacks();
        }
        else
        {
            _logger.LogInformation("Golf mode disabled");
            _autoCutTickTimer?.Stop();
            _autoCutTickTimer = null;
            IsAutoCutEnabled = false;
            ClearGolfFrameCallbacks();
            // Restore generic position-based BGRA callbacks (golf mode overwrote with role-based ones)
            WireUpSwitcherRendering();
        }
    }

    partial void OnIsAutoCutEnabledChanged(bool value)
    {
        if (value)
            _autoCutService.Enable();
        else
            _autoCutService.Disable();
    }

    partial void OnAutoCutSensitivityChanged(double value)
    {
        var config = _autoCutService.Configuration;
        config.SwingSpikeMultiplier = value;
        _autoCutService.UpdateConfiguration(config);
    }

    [RelayCommand]
    private void SetSensitivityPreset(string preset)
    {
        var config = preset switch
        {
            "High" => AutoCutConfiguration.HighSensitivity,
            "Low" => AutoCutConfiguration.LowSensitivity,
            _ => AutoCutConfiguration.Default
        };
        _autoCutService.UpdateConfiguration(config);
        AutoCutSensitivity = config.SwingSpikeMultiplier;
    }

    [RelayCommand]
    private void CalibrateIdle()
    {
        var simInput = InputConfiguration.GetInputByGolfRole(InputRole.SimulatorOutput);
        if (simInput?.PreviewRenderer?.Device == null)
        {
            _logger.LogWarning("Cannot calibrate: no simulator input assigned");
            return;
        }

        // The calibration will happen on the next frame from Source 2
        // Set a one-shot callback to capture the idle frame
        _calibrateOnNextFrame = true;
        _logger.LogInformation("Calibration pending on next simulator frame...");
    }

    private volatile bool _calibrateOnNextFrame;

    [RelayCommand]
    private void StartGolfSession()
    {
        if (IsSessionActive) return;

        var session = _golfSession.StartSession(SelectedGolfer);
        _sequenceRecorder.StartSession(session.Id);

        // Set recording paths from current recording if active
        if (IsRecording && _recordingService.CurrentSession != null)
        {
            var golferInput = InputConfiguration.GetInputByGolfRole(InputRole.GolferCamera);
            var simInput = InputConfiguration.GetInputByGolfRole(InputRole.SimulatorOutput);
            var inputSessions = _recordingService.CurrentSession.InputSessions;

            var src1Path = golferInput != null
                ? inputSessions.FirstOrDefault(s => s.Input.DeviceId == golferInput.DeviceId)?.FilePath
                : null;
            var src2Path = simInput != null
                ? inputSessions.FirstOrDefault(s => s.Input.DeviceId == simInput.DeviceId)?.FilePath
                : null;

            // Fallback to single-session path if no multi-input sessions
            if (src2Path == null && inputSessions.Count == 0)
                src2Path = _recordingService.CurrentSession.FilePath;

            _golfSession.SetRecordingPaths(src1Path, src2Path);
        }

        IsSessionActive = true;
        SessionGolferName = session.GolferDisplayName;
        SwingCount = 0;
        PracticeSwingCount = 0;
        CutHistory.Clear();
        ExportQueue.Clear();

        _logger.LogInformation("Golf session started (Golfer: {Golfer})",
            SelectedGolfer?.EffectiveDisplayName ?? "None");
    }

    [RelayCommand]
    private void StopGolfSession()
    {
        if (!IsSessionActive) return;

        _sequenceRecorder.StopSession();
        var session = _golfSession.EndSession();

        IsSessionActive = false;
        IsAutoCutEnabled = false;

        _logger.LogInformation("Golf session ended. Swings: {Count}", session?.TotalSwings ?? 0);
    }

    [RelayCommand]
    private void AssignGolferCamera(InputViewModel input)
    {
        // Clear any existing golfer camera assignment
        foreach (var inp in InputConfiguration.Inputs)
        {
            if (inp.GolfRole == InputRole.GolferCamera)
                inp.GolfRole = InputRole.Unassigned;
        }
        input.GolfRole = InputRole.GolferCamera;
        WireUpGolfFrameCallbacks();
        _logger.LogInformation("Golfer camera assigned to {Input}", input.ShortName);
    }

    [RelayCommand]
    private void AssignSimulatorOutput(InputViewModel input)
    {
        // Clear any existing simulator assignment
        foreach (var inp in InputConfiguration.Inputs)
        {
            if (inp.GolfRole == InputRole.SimulatorOutput)
                inp.GolfRole = InputRole.Unassigned;
        }
        input.GolfRole = InputRole.SimulatorOutput;
        WireUpGolfFrameCallbacks();
        _logger.LogInformation("Simulator output assigned to {Input}", input.ShortName);
    }

    private void WireUpGolfFrameCallbacks()
    {
        ClearGolfFrameCallbacks();

        var golferInput = InputConfiguration.GetInputByGolfRole(InputRole.GolferCamera);
        var simInput = InputConfiguration.GetInputByGolfRole(InputRole.SimulatorOutput);

        if (golferInput?.PreviewRenderer != null)
        {
            golferInput.PreviewRenderer.SetFrameAnalysisCallback((data, w, h) =>
            {
                _autoCutService.ProcessSource1Frame(data, w, h);
                // Update motion level for diagnostic display
                Application.Current?.Dispatcher.BeginInvoke(() =>
                    MotionLevel = _autoCutService.SwingDetector.LastSad);
            });

            // Wire BGRA callback for TransitionEngine (golfer = slot 0)
            golferInput.PreviewRenderer.SetBgraFrameCallback((bgra, w, h) =>
            {
                _transitionEngine.SetSource(0, bgra, w, h);
                Switcher?.SetProgramDimensions(w, h);
            });

            // Bind source 1 preview image
            golferInput.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(InputViewModel.PreviewImage))
                    Application.Current?.Dispatcher.BeginInvoke(() => Source1PreviewImage = golferInput.PreviewImage);
                if (e.PropertyName == nameof(InputViewModel.HasSignal))
                    Application.Current?.Dispatcher.BeginInvoke(() => Source1HasSignal = golferInput.HasSignal);
            };
            Source1PreviewImage = golferInput.PreviewImage;
            Source1HasSignal = golferInput.HasSignal;

            // Wire audio from golfer camera for audio swing detection
            if (golferInput.PreviewRenderer.Device != null)
            {
                _golferAudioDevice = golferInput.PreviewRenderer.Device;
                _golferAudioDevice.AudioSamplesReceived += OnGolferAudioSamplesReceived;
            }
        }

        if (simInput?.PreviewRenderer != null)
        {
            simInput.PreviewRenderer.SetFrameAnalysisCallback((data, w, h) =>
            {
                // Handle calibration
                if (_calibrateOnNextFrame)
                {
                    _calibrateOnNextFrame = false;
                    _autoCutService.CalibrateIdleReference(data, w, h);
                    Application.Current?.Dispatcher.BeginInvoke(() => IsIdleCalibrated = true);
                }

                _autoCutService.ProcessSource2Frame(data, w, h);
            });

            // Wire BGRA callback for TransitionEngine (simulator = slot 1)
            simInput.PreviewRenderer.SetBgraFrameCallback((bgra, w, h) =>
            {
                _transitionEngine.SetSource(1, bgra, w, h);
                Switcher?.SetProgramDimensions(w, h);
            });

            // Bind source 2 preview image
            simInput.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(InputViewModel.PreviewImage))
                    Application.Current?.Dispatcher.BeginInvoke(() => Source2PreviewImage = simInput.PreviewImage);
                if (e.PropertyName == nameof(InputViewModel.HasSignal))
                    Application.Current?.Dispatcher.BeginInvoke(() => Source2HasSignal = simInput.HasSignal);
            };
            Source2PreviewImage = simInput.PreviewImage;
            Source2HasSignal = simInput.HasSignal;
        }

        // Start the switcher rendering loop
        Switcher?.StartRendering();
    }

    private void ClearGolfFrameCallbacks()
    {
        if (_golferAudioDevice != null)
        {
            _golferAudioDevice.AudioSamplesReceived -= OnGolferAudioSamplesReceived;
            _golferAudioDevice = null;
        }

        foreach (var renderer in _previewRenderers.Values)
        {
            renderer.SetFrameAnalysisCallback(null);
            // Don't clear BGRA callbacks or stop rendering —
            // switcher rendering is needed regardless of golf mode
        }
    }

    private void OnGolferAudioSamplesReceived(object? sender, AudioSamplesEventArgs e)
    {
        _autoCutService.ProcessAudioFrame(e.SampleData, e.SampleRate, e.Channels, e.BitsPerSample);
        Application.Current?.Dispatcher.BeginInvoke(() =>
            AudioLevel = _autoCutService.AudioSwingDetector.LastRmsDb);
    }

    private void OnProgramSourceChanged(object? sender, ProgramSourceChangedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveSourceIndex = e.NewSourceIndex;

            // Clear manual preview override so A/B flip-flop takes over
            if (_manualPreviewInput != null)
            {
                _manualPreviewInput.PropertyChanged -= OnManualPreviewPropertyChanged;
                _manualPreviewInput = null;
            }

            UpdatePreviewSource();

            // Update IsProgramSource on all inputs
            foreach (var input in EnabledInputs)
            {
                input.IsProgramSource =
                    (input.GolfRole == InputRole.GolferCamera && e.NewSourceIndex == 0) ||
                    (input.GolfRole == InputRole.SimulatorOutput && e.NewSourceIndex == 1);
            }

            // Track practice swings
            if (e.Reason == "practice_swing")
            {
                PracticeSwingCount++;
            }

            // Add cut history entry
            string direction = e.NewSourceIndex == 1 ? "-> SIM" : "-> GOLFER";
            CutHistory.Insert(0, new CutHistoryItem
            {
                Timestamp = e.Timestamp,
                TimeDisplay = e.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
                Reason = e.Reason,
                Direction = direction
            });
            while (CutHistory.Count > 10)
                CutHistory.RemoveAt(CutHistory.Count - 1);

            // Switch the selected input to match the active program source
            var targetInput = InputConfiguration.GetGolfSource(e.NewSourceIndex);
            if (targetInput != null)
            {
                InputConfiguration.SelectInput(targetInput);
            }

            // Update streaming selection on renderers
            foreach (var kvp in _previewRenderers)
            {
                var input = InputConfiguration.Inputs.FirstOrDefault(i => i.DeviceId == kvp.Key);
                if (input != null)
                {
                    bool isProgram = input.GolfRole == InputRole.GolferCamera && e.NewSourceIndex == 0
                                  || input.GolfRole == InputRole.SimulatorOutput && e.NewSourceIndex == 1;
                    kvp.Value.SetSelectedForStreaming(isProgram);
                }
            }
        });
    }

    private void OnAutoCutTriggered(object? sender, AutoCutEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            // Use TransitionEngine for auto-cuts if switcher is active
            if (Switcher != null)
            {
                Switcher.TriggerTransition();
            }
            else
            {
                _switcherService.CutToSource(e.TargetSourceIndex, e.Reason);
            }
        });
    }

    private void OnAutoCutStateChanged(object? sender, AutoCutState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AutoCutStateText = state switch
            {
                AutoCutState.WaitingForSwing => "Waiting",
                AutoCutState.SwingDetected => "Swing!",
                AutoCutState.FollowingShot => "Following",
                AutoCutState.ResetDetected => "Landing",
                AutoCutState.Cooldown => "Cooldown",
                AutoCutState.Disabled => "Disabled",
                _ => "Unknown"
            };
        });
    }

    private void OnSequenceCompleted(object? sender, SwingSequence sequence)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _logger.LogInformation("Swing #{Num} ready for export (Duration: {Duration:F1}s)",
                sequence.SequenceNumber, sequence.Duration?.TotalSeconds ?? 0);

            // Add to export queue
            ExportQueue.Add(new ExportQueueItem
            {
                SwingNumber = sequence.SequenceNumber,
                Status = "Pending",
                Duration = sequence.Duration
            });
        });
    }

    private void OnExportProgressChanged(object? sender, ExportProgressEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var item = ExportQueue.FirstOrDefault(q => q.SwingNumber == e.SwingNumber);
            if (item != null)
                item.Status = e.Status;
        });
    }

    private void OnExportCompleted(object? sender, ExportCompletedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var item = ExportQueue.FirstOrDefault(q => q.SwingNumber == e.SwingNumber);
            if (item != null)
            {
                item.Status = e.Success ? "Complete" : "Failed";
                item.OutputPath = e.OutputPath;
            }

            // Add completed clip to clip bin
            if (e.Success && e.OutputPath != null && System.IO.File.Exists(e.OutputPath))
            {
                var fileInfo = new System.IO.FileInfo(e.OutputPath);
                ClipBin.AddClip(new ClipViewModel
                {
                    Name = System.IO.Path.GetFileNameWithoutExtension(e.OutputPath),
                    FilePath = e.OutputPath,
                    Duration = e.Duration ?? TimeSpan.Zero,
                    FileSizeBytes = fileInfo.Length,
                    CreatedAt = DateTime.Now
                });
            }

            // Auto-upload completed export
            if (e.Success && AutoUploadEnabled && SelectedUploadProviderId != null && e.OutputPath != null)
            {
                var remotePath = $"golf/{System.IO.Path.GetFileName(e.OutputPath)}";
                _ = _uploadService.EnqueueAsync(new UploadRequest(
                    e.OutputPath,
                    SelectedUploadProviderId,
                    remotePath));
                _logger.LogInformation("Auto-upload enqueued for {Path}", e.OutputPath);
            }
        });
    }

    private async Task LoadGolfersAsync()
    {
        try
        {
            var golfers = await _golferRepository.GetAllAsync();
            AvailableGolfers.Clear();
            foreach (var golfer in golfers)
                AvailableGolfers.Add(golfer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load golfers");
        }
    }

    [RelayCommand]
    private void OpenGolferManagement()
    {
        var window = _serviceProvider.GetRequiredService<GolferManagementWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();

        // Refresh golfer list after dialog closes
        _ = LoadGolfersAsync();
    }

    [RelayCommand]
    private void OpenOverlaySettings()
    {
        var window = _serviceProvider.GetRequiredService<OverlaySettingsWindow>();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
    }

    private async Task LoadAutoUploadSettingsAsync()
    {
        try
        {
            AutoUploadEnabled = await _settingsService.GetAsync(SettingsKeys.GolfAutoUpload, false);
            SelectedUploadProviderId = await _settingsService.GetAsync<string?>(SettingsKeys.GolfAutoUploadProvider, null);

            AvailableUploadProviders.Clear();
            foreach (var provider in _uploadService.GetProviders().Where(p => p.IsConfigured))
            {
                AvailableUploadProviders.Add(new UploadProviderOption
                {
                    ProviderId = provider.ProviderId,
                    DisplayName = provider.DisplayName
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load auto-upload settings");
        }
    }

    partial void OnAutoUploadEnabledChanged(bool value)
    {
        _ = _settingsService.SetAsync(SettingsKeys.GolfAutoUpload, value);
    }

    partial void OnSelectedUploadProviderIdChanged(string? value)
    {
        if (value != null)
            _ = _settingsService.SetAsync(SettingsKeys.GolfAutoUploadProvider, value);
    }

    [RelayCommand]
    private async Task AddStillImageSourceAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Image for Virtual Input",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp|All Files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var virtualMgr = _serviceProvider.GetRequiredService<VirtualDeviceManager>();
        await virtualMgr.AddStillImageAsync(
            Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);

        _deviceManager.RefreshDevices();
        InputConfiguration.RefreshInputs();
        await RebuildPreviewRenderersAsync();
    }

    [RelayCommand]
    private async Task AddColorSourceAsync()
    {
        var dialog = new Views.ColorPickerDialog
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        var name = $"#{dialog.SelectedR:X2}{dialog.SelectedG:X2}{dialog.SelectedB:X2}";

        var virtualMgr = _serviceProvider.GetRequiredService<VirtualDeviceManager>();
        await virtualMgr.AddColorSourceAsync(name, dialog.SelectedR, dialog.SelectedG, dialog.SelectedB);

        _deviceManager.RefreshDevices();
        InputConfiguration.RefreshInputs();
        await RebuildPreviewRenderersAsync();
    }

    [RelayCommand]
    private void Exit()
    {
        Application.Current.Shutdown();
    }
}

public class InputScheduleState
{
    public DateTime StartTime { get; set; } = DateTime.Now;
    public int DurationHours { get; set; } = 1;
    public int DurationMinutes { get; set; }
    public Guid? ActiveScheduleId { get; set; }
    public string? StatusText { get; set; }
}

public partial class DriveViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    private long _totalSize;

    [ObservableProperty]
    private long _freeSpace;

    public string FreeSpaceDisplay => $"{FreeSpace / (1024.0 * 1024 * 1024):F1} GB free";
    public double UsedPercent => 100.0 * (TotalSize - FreeSpace) / TotalSize;
}

public class ViewerInfo
{
    public string Id { get; init; } = string.Empty;
    public string RemoteAddress { get; init; } = string.Empty;
    public DateTimeOffset ConnectedAt { get; init; }
    public string ConnectionDuration => (DateTimeOffset.UtcNow - ConnectedAt) switch
    {
        { TotalHours: >= 1 } ts => $"{ts.Hours}h {ts.Minutes}m",
        { TotalMinutes: >= 1 } ts => $"{ts.Minutes}m {ts.Seconds}s",
        var ts => $"{ts.Seconds}s"
    };
}

public class CutHistoryItem
{
    public DateTimeOffset Timestamp { get; init; }
    public string TimeDisplay { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
}

public class UploadProviderOption
{
    public string ProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
}
