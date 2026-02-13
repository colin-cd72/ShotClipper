using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Screener.Core.Output;
using Screener.Golf.Overlays;
using Screener.Golf.Persistence;
using Screener.Golf.Switching;
using Screener.Streaming;

namespace Screener.UI.ViewModels;

/// <summary>
/// Owns transition state, program output rendering, and overlay positioning.
/// Child ViewModel of MainViewModel.
/// </summary>
public partial class SwitcherViewModel : ObservableObject
{
    private readonly TransitionEngine _engine;
    private readonly SwitcherService _switcherService;
    private readonly OverlayRepository _overlayRepository;
    private readonly OutputManager _outputManager;
    private readonly PanelRelayService _panelRelayService;
    private readonly ILogger<SwitcherViewModel> _logger;

    private DateTime _lastRenderTime;
    private bool _renderingSubscribed;

    // Callback to push overlay state to streaming service
    private Action<bool, string?, double, double>? _overlayStateCallback;

    // Transition controls
    [ObservableProperty]
    private TransitionType _selectedTransition = TransitionType.Cut;

    [ObservableProperty]
    private int _transitionDurationMs = 1000;

    [ObservableProperty]
    private double _transitionPosition;

    [ObservableProperty]
    private bool _isTransitioning;

    // Overlay state - logo
    [ObservableProperty]
    private double _logoX = 1700;

    [ObservableProperty]
    private double _logoY = 20;

    [ObservableProperty]
    private double _logoScale = 1.0;

    [ObservableProperty]
    private double _logoOpacity = 1.0;

    [ObservableProperty]
    private double _logoDisplayWidth = 200;

    [ObservableProperty]
    private double _logoDisplayHeight = 100;

    [ObservableProperty]
    private bool _isLogoVisible;

    [ObservableProperty]
    private ImageSource? _logoImageSource;

    // Overlay state - lower third
    [ObservableProperty]
    private double _lowerThirdX = 40;

    [ObservableProperty]
    private double _lowerThirdY = 960;

    [ObservableProperty]
    private bool _isLowerThirdVisible;

    [ObservableProperty]
    private string _lowerThirdText = "";

    // Preview lower third visibility (always shows when text is set, regardless of KEY)
    [ObservableProperty]
    private bool _isPreviewLowerThirdVisible;

    // KEY toggle (downstream key for overlays)
    [ObservableProperty]
    private bool _isKeyActive;

    // Program output
    [ObservableProperty]
    private WriteableBitmap? _programImage;

    private int _programWidth;
    private int _programHeight;

    public TransitionEngine Engine => _engine;

    public SwitcherViewModel(
        TransitionEngine engine,
        SwitcherService switcherService,
        OverlayRepository overlayRepository,
        OutputManager outputManager,
        PanelRelayService panelRelayService,
        ILogger<SwitcherViewModel> logger)
    {
        _engine = engine;
        _switcherService = switcherService;
        _overlayRepository = overlayRepository;
        _outputManager = outputManager;
        _panelRelayService = panelRelayService;
        _logger = logger;

        _engine.TransitionCompleted += OnTransitionCompleted;

        // Load overlay settings
        _ = LoadOverlaySettingsAsync();
    }

    /// <summary>
    /// Start the render loop (subscribe to CompositionTarget.Rendering).
    /// Must be called from the UI thread.
    /// </summary>
    public void StartRendering()
    {
        if (_renderingSubscribed) return;
        _lastRenderTime = DateTime.UtcNow;
        CompositionTarget.Rendering += OnCompositionTargetRendering;
        _renderingSubscribed = true;
    }

    /// <summary>
    /// Stop the render loop.
    /// </summary>
    public void StopRendering()
    {
        if (!_renderingSubscribed) return;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _renderingSubscribed = false;
    }

    /// <summary>
    /// Set callback for pushing overlay state changes to the streaming service.
    /// </summary>
    public void SetOverlayStateCallback(Action<bool, string?, double, double>? callback)
    {
        _overlayStateCallback = callback;
    }

    partial void OnTransitionDurationMsChanged(int value)
    {
        _engine.DurationMs = value;
    }

    partial void OnTransitionPositionChanged(double value)
    {
        if (!_engine.IsTransitioning || _isManualControl)
        {
            _isManualControl = true;

            // Compute effective engine position based on T-bar direction.
            // When resting at 1.0, dragging back toward 0 is a new transition.
            double effectivePosition = _tbarRestPosition >= 1.0 ? 1.0 - value : value;

            _engine.SetManualPosition(effectivePosition);
            IsTransitioning = _engine.IsTransitioning;
        }
    }

    private bool _isManualControl;
    private double _tbarRestPosition; // 0.0 or 1.0 — where the T-bar last completed

    partial void OnIsKeyActiveChanged(bool value)
    {
        IsLogoVisible = value && LogoImageSource != null;
        IsLowerThirdVisible = value && !string.IsNullOrEmpty(LowerThirdText);
        _overlayStateCallback?.Invoke(IsLowerThirdVisible, LowerThirdText, LowerThirdX, LowerThirdY);
    }

    partial void OnLowerThirdTextChanged(string value)
    {
        // Preview always shows when text is set, regardless of KEY state
        IsPreviewLowerThirdVisible = !string.IsNullOrEmpty(value);
        // Program lower third respects KEY toggle
        IsLowerThirdVisible = IsKeyActive && !string.IsNullOrEmpty(value);
        _overlayStateCallback?.Invoke(IsLowerThirdVisible, value, LowerThirdX, LowerThirdY);
    }

    partial void OnLowerThirdXChanged(double value)
    {
        _overlayStateCallback?.Invoke(IsLowerThirdVisible, LowerThirdText, value, LowerThirdY);
    }

    partial void OnLowerThirdYChanged(double value)
    {
        _overlayStateCallback?.Invoke(IsLowerThirdVisible, LowerThirdText, LowerThirdX, value);
    }

    [RelayCommand]
    private void ExecuteCut()
    {
        _isManualControl = false;
        _engine.TriggerCut();
    }

    [RelayCommand]
    private void ExecuteAutoTransition()
    {
        _isManualControl = false;
        _engine.DurationMs = TransitionDurationMs;
        _engine.TriggerAutoTransition(SelectedTransition);
        IsTransitioning = _engine.IsTransitioning;
    }

    [RelayCommand]
    private void ExecuteDissolve()
    {
        _isManualControl = false;
        SelectedTransition = TransitionType.Dissolve;
        _engine.DurationMs = TransitionDurationMs;
        _engine.TriggerAutoTransition(TransitionType.Dissolve);
        IsTransitioning = _engine.IsTransitioning;
    }

    [RelayCommand]
    private void ExecuteDipToBlack()
    {
        _isManualControl = false;
        SelectedTransition = TransitionType.DipToBlack;
        _engine.DurationMs = TransitionDurationMs;
        _engine.TriggerAutoTransition(TransitionType.DipToBlack);
        IsTransitioning = _engine.IsTransitioning;
    }

    [RelayCommand]
    private void ExecuteKey()
    {
        IsKeyActive = !IsKeyActive;
    }

    /// <summary>
    /// Trigger a cut externally (e.g., from auto-cut or manual button).
    /// Uses the currently selected transition type.
    /// </summary>
    public void TriggerTransition(TransitionType? typeOverride = null)
    {
        _isManualControl = false;
        var type = typeOverride ?? SelectedTransition;
        _engine.DurationMs = TransitionDurationMs;
        if (type == TransitionType.Cut)
            _engine.TriggerCut();
        else
            _engine.TriggerAutoTransition(type);
        IsTransitioning = _engine.IsTransitioning;
    }

    private void OnTransitionCompleted(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsTransitioning = false;

            // T-bar stays at completion position; record new rest
            _tbarRestPosition = _tbarRestPosition >= 1.0 ? 0.0 : 1.0;

            _isManualControl = false;

            // Notify SwitcherService that the transition completed
            // The new program source is now what was previously preview
            int newSourceIndex = _switcherService.ActiveSourceIndex == 0 ? 1 : 0;
            _switcherService.CutToSource(newSourceIndex, "transition");
        });
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double elapsedMs = (now - _lastRenderTime).TotalMilliseconds;
        _lastRenderTime = now;

        // Advance auto-transition
        _engine.Tick(elapsedMs);

        // Update UI binding for transition position during auto-transition
        if (_engine.IsTransitioning && !_isManualControl)
        {
            // Mirror the slider direction when resting at bottom
            TransitionPosition = _tbarRestPosition >= 1.0
                ? 1.0 - _engine.TransitionPosition
                : _engine.TransitionPosition;
        }
        IsTransitioning = _engine.IsTransitioning;

        // Get blended program frame
        var frame = _engine.GetProgramFrame();
        if (frame == null) return;

        // Ensure WriteableBitmap exists at correct size
        EnsureProgramBitmap();

        var bitmap = ProgramImage;
        if (bitmap == null) return;

        try
        {
            bitmap.WritePixels(
                new Int32Rect(0, 0, _programWidth, _programHeight),
                frame,
                _programWidth * 4,
                0);
        }
        catch
        {
            // Swallow rendering errors
        }
    }

    /// <summary>
    /// Called by MainViewModel when source frame dimensions are known.
    /// Initializes the program output WriteableBitmap.
    /// </summary>
    public void SetProgramDimensions(int width, int height)
    {
        if (_programWidth == width && _programHeight == height) return;
        _programWidth = width;
        _programHeight = height;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            ProgramImage = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        });
    }

    /// <summary>
    /// Save overlay drag positions to the OverlayRepository.
    /// </summary>
    public async Task SaveOverlayPositionsAsync()
    {
        try
        {
            // Save logo position
            var logoRecord = await _overlayRepository.GetDefaultAsync("logo_bug");
            if (logoRecord != null)
            {
                var config = logoRecord.DeserializeConfig<LogoBugConfig>() ?? new LogoBugConfig();
                config.Position = LogoPosition.Custom;
                config.CustomX = LogoX;
                config.CustomY = LogoY;
                config.Scale = LogoScale;
                config.Opacity = LogoOpacity;
                logoRecord.SerializeConfig(config);
                await _overlayRepository.SaveAsync(logoRecord);
            }

            // Save lower third position
            var ltRecord = await _overlayRepository.GetDefaultAsync("lower_third");
            if (ltRecord != null)
            {
                var config = ltRecord.DeserializeConfig<LowerThirdConfig>() ?? new LowerThirdConfig();
                config.X = (int)LowerThirdX;
                config.YFromBottom = (int)(1080 - LowerThirdY);
                ltRecord.SerializeConfig(config);
                await _overlayRepository.SaveAsync(ltRecord);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save overlay positions");
        }
    }

    private async Task LoadOverlaySettingsAsync()
    {
        try
        {
            // Load logo bug
            var logoRecord = await _overlayRepository.GetDefaultAsync("logo_bug");
            if (logoRecord != null)
            {
                var config = logoRecord.DeserializeConfig<LogoBugConfig>();
                if (config != null)
                {
                    LogoScale = config.Scale;
                    LogoOpacity = config.Opacity;

                    if (config.Position == LogoPosition.Custom)
                    {
                        LogoX = config.CustomX;
                        LogoY = config.CustomY;
                    }
                    else
                    {
                        // Convert preset position to canvas coordinates
                        var (x, y) = config.Position switch
                        {
                            LogoPosition.TopLeft => ((double)config.Margin, (double)config.Margin),
                            LogoPosition.TopRight => (1920.0 - LogoDisplayWidth - config.Margin, (double)config.Margin),
                            LogoPosition.BottomLeft => ((double)config.Margin, 1080.0 - LogoDisplayHeight - config.Margin),
                            LogoPosition.BottomRight => (1920.0 - LogoDisplayWidth - config.Margin, 1080.0 - LogoDisplayHeight - config.Margin),
                            _ => (1700.0, 20.0)
                        };
                        LogoX = x;
                        LogoY = y;
                    }

                    // Load logo image
                    if (!string.IsNullOrEmpty(config.LogoPath) && System.IO.File.Exists(config.LogoPath))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.UriSource = new Uri(config.LogoPath);
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                LogoImageSource = bitmap;
                                LogoDisplayWidth = bitmap.PixelWidth * LogoScale;
                                LogoDisplayHeight = bitmap.PixelHeight * LogoScale;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to load logo image: {Path}", config.LogoPath);
                            }
                        });
                    }
                }
            }

            // Load lower third
            var ltRecord = await _overlayRepository.GetDefaultAsync("lower_third");
            if (ltRecord != null)
            {
                var config = ltRecord.DeserializeConfig<LowerThirdConfig>();
                if (config != null)
                {
                    LowerThirdX = config.X;
                    LowerThirdY = 1080 - config.YFromBottom;
                    LowerThirdText = ""; // Text set per-session via golfer name
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load overlay settings");
        }
    }

    private void EnsureProgramBitmap()
    {
        if (_programWidth <= 0 || _programHeight <= 0) return;

        var current = ProgramImage;
        if (current == null || current.PixelWidth != _programWidth || current.PixelHeight != _programHeight)
        {
            ProgramImage = new WriteableBitmap(_programWidth, _programHeight, 96, 96, PixelFormats.Bgr32, null);
        }
    }
}
