using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Timecode;

namespace Screener.Preview;

/// <summary>
/// WPF control for video preview using DirectX 11 with D3DImage interop.
/// </summary>
public partial class VideoPreviewControl : UserControl
{
    private readonly ILogger<VideoPreviewControl>? _logger;
    private D3DPreviewRenderer? _renderer;
    private D3DImage? _d3dImage;
    private DispatcherTimer? _refreshTimer;
    private bool _isDisposed;

    // Overlay settings
    private bool _showTimecode = true;
    private bool _showRecordingIndicator = true;
    private bool _showFormatInfo = true;
    private bool _showSafeAreas;
    private bool _showCenterCrosshair;

    private string _currentTimecode = "00:00:00:00";
    private bool _isRecording;
    private string _formatInfo = "";
    private bool _hasSignal;

    /// <summary>
    /// Gets or sets whether to show the timecode overlay.
    /// </summary>
    public bool ShowTimecode
    {
        get => _showTimecode;
        set
        {
            _showTimecode = value;
            UpdateOverlayVisibility();
        }
    }

    /// <summary>
    /// Gets or sets whether to show the recording indicator.
    /// </summary>
    public bool ShowRecordingIndicator
    {
        get => _showRecordingIndicator;
        set
        {
            _showRecordingIndicator = value;
            UpdateOverlayVisibility();
        }
    }

    /// <summary>
    /// Gets or sets whether to show format info.
    /// </summary>
    public bool ShowFormatInfo
    {
        get => _showFormatInfo;
        set
        {
            _showFormatInfo = value;
            UpdateOverlayVisibility();
        }
    }

    /// <summary>
    /// Gets or sets whether to show safe area guides.
    /// </summary>
    public bool ShowSafeAreas
    {
        get => _showSafeAreas;
        set
        {
            _showSafeAreas = value;
            UpdateOverlayVisibility();
            UpdateSafeAreaGuides();
        }
    }

    /// <summary>
    /// Gets or sets whether to show center crosshair.
    /// </summary>
    public bool ShowCenterCrosshair
    {
        get => _showCenterCrosshair;
        set
        {
            _showCenterCrosshair = value;
            UpdateOverlayVisibility();
            UpdateCenterCrosshair();
        }
    }

    /// <summary>
    /// Gets or sets the current timecode to display.
    /// </summary>
    public string CurrentTimecode
    {
        get => _currentTimecode;
        set
        {
            _currentTimecode = value;
            Dispatcher.BeginInvoke(() => TimecodeText.Text = value);
        }
    }

    /// <summary>
    /// Gets or sets whether recording is active.
    /// </summary>
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            _isRecording = value;
            UpdateOverlayVisibility();
        }
    }

    /// <summary>
    /// Gets or sets the format info text.
    /// </summary>
    public string FormatInfo
    {
        get => _formatInfo;
        set
        {
            _formatInfo = value;
            Dispatcher.BeginInvoke(() => FormatText.Text = value);
        }
    }

    /// <summary>
    /// Gets or sets whether there is an active video signal.
    /// </summary>
    public bool HasSignal
    {
        get => _hasSignal;
        set
        {
            _hasSignal = value;
            UpdateSignalOverlay();
        }
    }

    public VideoPreviewControl()
    {
        InitializeComponent();
    }

    public VideoPreviewControl(ILogger<VideoPreviewControl> logger) : this()
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the preview control with a renderer.
    /// </summary>
    public void Initialize(D3DPreviewRenderer renderer)
    {
        _renderer = renderer;
        _renderer.FrameReady += OnFrameReady;

        InitializeD3DImage();
    }

    private void InitializeD3DImage()
    {
        if (_renderer == null || !_renderer.IsInitialized)
            return;

        try
        {
            _d3dImage = new D3DImage();
            _d3dImage.IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;

            var brush = new ImageBrush(_d3dImage)
            {
                Stretch = Stretch.Uniform
            };

            PreviewImage.Source = _d3dImage;

            // Set the back buffer from the renderer's shared texture
            if (_renderer.SharedHandle != IntPtr.Zero)
            {
                _d3dImage.Lock();
                _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _renderer.SharedHandle);
                _d3dImage.Unlock();
            }

            // Start refresh timer for D3DImage updates
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60fps
            };
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();

            _logger?.LogDebug("D3DImage initialized for preview");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize D3DImage");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SizeChanged += OnSizeChanged;
        UpdateOverlayVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSafeAreaGuides();
        UpdateCenterCrosshair();
    }

    private void OnFrameReady(object? sender, EventArgs e)
    {
        // Frame ready notification from renderer
        // D3DImage will be invalidated by the refresh timer
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_d3dImage == null || !_d3dImage.IsFrontBufferAvailable)
            return;

        try
        {
            _d3dImage.Lock();

            // Invalidate the entire image to force redraw
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _renderer?.Width ?? 1, _renderer?.Height ?? 1));

            _d3dImage.Unlock();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during D3DImage refresh");
        }
    }

    private void OnIsFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_d3dImage?.IsFrontBufferAvailable == true && _renderer?.SharedHandle != IntPtr.Zero)
        {
            // Re-initialize the back buffer
            _d3dImage.Lock();
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _renderer.SharedHandle);
            _d3dImage.Unlock();
        }
    }

    /// <summary>
    /// Render a video frame to the preview.
    /// </summary>
    public void RenderFrame(VideoFrame frame)
    {
        if (_isDisposed || _renderer == null)
            return;

        _renderer.RenderFrame(frame);

        // Update signal status
        if (!_hasSignal)
        {
            HasSignal = true;
            FormatInfo = $"{frame.Width}x{frame.Height} {GetFrameRateString(frame.FrameRate ?? 0)}";
        }
    }

    private static string GetFrameRateString(double frameRate)
    {
        return frameRate switch
        {
            23.976 => "23.98p",
            24 => "24p",
            25 => "25p",
            29.97 => "29.97p",
            30 => "30p",
            50 => "50p",
            59.94 => "59.94p",
            60 => "60p",
            _ => $"{frameRate:F2}p"
        };
    }

    private void UpdateOverlayVisibility()
    {
        Dispatcher.BeginInvoke(() =>
        {
            TimecodeOverlay.Visibility = _showTimecode && _hasSignal ? Visibility.Visible : Visibility.Collapsed;
            RecordingIndicator.Visibility = _showRecordingIndicator && _isRecording ? Visibility.Visible : Visibility.Collapsed;
            FormatOverlay.Visibility = _showFormatInfo && _hasSignal ? Visibility.Visible : Visibility.Collapsed;
            ActionSafeGuide.Visibility = _showSafeAreas && _hasSignal ? Visibility.Visible : Visibility.Collapsed;
            TitleSafeGuide.Visibility = _showSafeAreas && _hasSignal ? Visibility.Visible : Visibility.Collapsed;
            CenterCrosshair.Visibility = _showCenterCrosshair && _hasSignal ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void UpdateSignalOverlay()
    {
        Dispatcher.BeginInvoke(() =>
        {
            NoSignalOverlay.Visibility = _hasSignal ? Visibility.Collapsed : Visibility.Visible;
            UpdateOverlayVisibility();
        });
    }

    private void UpdateSafeAreaGuides()
    {
        if (!_showSafeAreas)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            var width = OverlayCanvas.ActualWidth;
            var height = OverlayCanvas.ActualHeight;

            if (width <= 0 || height <= 0)
                return;

            // Action safe: 93% of frame (3.5% on each edge)
            var actionMarginX = width * 0.035;
            var actionMarginY = height * 0.035;
            Canvas.SetLeft(ActionSafeGuide, actionMarginX);
            Canvas.SetTop(ActionSafeGuide, actionMarginY);
            ActionSafeGuide.Width = width - actionMarginX * 2;
            ActionSafeGuide.Height = height - actionMarginY * 2;

            // Title safe: 90% of frame (5% on each edge)
            var titleMarginX = width * 0.05;
            var titleMarginY = height * 0.05;
            Canvas.SetLeft(TitleSafeGuide, titleMarginX);
            Canvas.SetTop(TitleSafeGuide, titleMarginY);
            TitleSafeGuide.Width = width - titleMarginX * 2;
            TitleSafeGuide.Height = height - titleMarginY * 2;
        });
    }

    private void UpdateCenterCrosshair()
    {
        if (!_showCenterCrosshair)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            var width = OverlayCanvas.ActualWidth;
            var height = OverlayCanvas.ActualHeight;

            if (width <= 0 || height <= 0)
                return;

            var centerX = width / 2;
            var centerY = height / 2;
            var size = Math.Min(width, height) * 0.05;

            var geometry = new GeometryGroup();
            geometry.Children.Add(new LineGeometry(
                new Point(centerX - size, centerY),
                new Point(centerX + size, centerY)));
            geometry.Children.Add(new LineGeometry(
                new Point(centerX, centerY - size),
                new Point(centerX, centerY + size)));

            CenterCrosshair.Data = geometry;
        });
    }

    /// <summary>
    /// Set the device status message when no signal.
    /// </summary>
    public void SetDeviceStatus(string status)
    {
        Dispatcher.BeginInvoke(() => DeviceStatusText.Text = status);
    }

    /// <summary>
    /// Clear the preview to black.
    /// </summary>
    public void Clear()
    {
        _renderer?.Clear();
        HasSignal = false;
        SetDeviceStatus("No signal");
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _refreshTimer?.Stop();
        _refreshTimer = null;

        if (_renderer != null)
        {
            _renderer.FrameReady -= OnFrameReady;
        }

        SizeChanged -= OnSizeChanged;

        if (_d3dImage != null)
        {
            _d3dImage.IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
            _d3dImage = null;
        }
    }
}
