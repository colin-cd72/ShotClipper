using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Streaming;
using Screener.Core.Output;

namespace Screener.UI.ViewModels;

/// <summary>
/// Lightweight per-input frame renderer. Subscribes to a capture device's video frames
/// and produces a WriteableBitmap preview at half or quarter resolution.
/// Reuses the static YUV->RGB lookup tables from VideoPreviewViewModel.
/// </summary>
public class InputPreviewRenderer : IDisposable
{
    private ICaptureDevice? _device;
    private WriteableBitmap? _previewBitmap;
    private int _previewWidth;
    private int _previewHeight;

    // Double-buffered RGB conversion
    private byte[]? _rgbBufferA;
    private byte[]? _rgbBufferB;
    private int _currentRgbBuffer;
    private volatile bool _conversionInProgress;

    // CompositionTarget.Rendering: latest converted frame ready for WritePixels
    private volatile byte[]? _readyFrame;
    private int _readyWidth;
    private int _readyHeight;
    private bool _renderingSubscribed;

    // Frame rate limiting
    private long _callbackCount;

    // Resolution divisor (2 = half 960x540, 4 = quarter 480x270)
    private int _resDivisor = 2;

    // Output
    private OutputManager? _outputManager;
    private bool _isSelectedForStreaming;

    // The InputViewModel whose PreviewImage we update
    private readonly InputViewModel _input;

    public InputPreviewRenderer(InputViewModel input)
    {
        _input = input;
    }

    public void SetOutputManager(OutputManager? manager, bool isSelected)
    {
        _outputManager = manager;
        _isSelectedForStreaming = isSelected;
    }

    [Obsolete("Use SetOutputManager instead")]
    public void SetStreamingService(IStreamingService? service, bool isSelected)
    {
        // Backwards compat - noop if OutputManager is already set
        _isSelectedForStreaming = isSelected;
    }

    /// <summary>
    /// The capture device this renderer is attached to (for audio routing).
    /// </summary>
    public ICaptureDevice? Device => _device;

    /// <summary>
    /// Start rendering preview from a capture device.
    /// Gets the device, selects a video mode, starts capture, and subscribes to frame events.
    /// </summary>
    public async Task StartAsync(string deviceId, VideoConnector connector, IDeviceManager deviceManager, int resDivisor = 2)
    {
        await StopAsync();
        _resDivisor = resDivisor;
        _callbackCount = 0;

        _device = await deviceManager.GetDeviceAsync(deviceId);
        if (_device == null) return;

        _device.SelectedConnector = connector;
        _device.VideoFrameReceived += OnVideoFrameReceived;
        _device.StatusChanged += OnStatusChanged;

        // Select video mode (same logic as VideoPreviewViewModel)
        var mode = _device.SupportedVideoModes
            .FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && !m.IsInterlaced && m.FrameRate.Value > 59.9 && m.FrameRate.Value < 60.0)
            ?? _device.SupportedVideoModes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && !m.IsInterlaced && m.FrameRate.Value >= 59.0)
            ?? _device.SupportedVideoModes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && !m.IsInterlaced)
            ?? _device.SupportedVideoModes.FirstOrDefault(m => m.Width == 1920)
            ?? _device.SupportedVideoModes.FirstOrDefault();

        if (mode == null) return;

        InitializePreviewBitmap(mode.Width, mode.Height);
        await _device.StartCaptureAsync(mode);
    }

    public async Task StopAsync()
    {
        if (_device != null)
        {
            _device.VideoFrameReceived -= OnVideoFrameReceived;
            _device.StatusChanged -= OnStatusChanged;
            await _device.StopCaptureAsync();
            _device = null;
        }

        if (_renderingSubscribed)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                CompositionTarget.Rendering -= OnCompositionTargetRendering;
            });
            _renderingSubscribed = false;
        }

        _readyFrame = null;
        _input.HasSignal = false;
        _input.PreviewImage = null;
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private void InitializePreviewBitmap(int srcWidth, int srcHeight)
    {
        _previewWidth = srcWidth / _resDivisor;
        _previewHeight = srcHeight / _resDivisor;

        Application.Current.Dispatcher.Invoke(() =>
        {
            _previewBitmap = new WriteableBitmap(_previewWidth, _previewHeight, 96, 96, PixelFormats.Bgr32, null);
            _input.PreviewImage = _previewBitmap;

            if (!_renderingSubscribed)
            {
                CompositionTarget.Rendering += OnCompositionTargetRendering;
                _renderingSubscribed = true;
            }
        });
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        var frame = Interlocked.Exchange(ref _readyFrame, null);
        if (frame == null) return;

        try
        {
            _previewBitmap?.WritePixels(
                new Int32Rect(0, 0, _readyWidth, _readyHeight),
                frame,
                _readyWidth * 4,
                0);
        }
        catch
        {
            // Swallow rendering errors
        }
    }

    private void OnVideoFrameReceived(object? sender, VideoFrameEventArgs e)
    {
        if (_previewBitmap == null) return;

        _callbackCount++;

        // Push frames to all outputs (~15fps = every 4th callback from 60fps source)
        if (_isSelectedForStreaming && _outputManager != null && _callbackCount % 4 == 0)
        {
            _ = _outputManager.PushFrameToAllAsync(e.FrameData, e.Mode, e.Timestamp);
        }

        // Accept every 2nd frame: 59.94fps -> ~30fps
        if (_callbackCount % 2 != 0) return;

        // Skip if previous conversion still running
        if (_conversionInProgress) return;

        try
        {
            _input.HasSignal = true;

            // Update format description
            var fmtDesc = $"{e.Mode.Width}x{e.Mode.Height} {e.Mode.FrameRate.Value:F2}fps";
            if (_input.FormatDescription != fmtDesc)
                _input.FormatDescription = fmtDesc;

            // Reinitialize if format changed
            if (e.Mode.Width / _resDivisor != _previewWidth || e.Mode.Height / _resDivisor != _previewHeight)
            {
                VideoPreviewViewModel.ResetVancDetection();
                InitializePreviewBitmap(e.Mode.Width, e.Mode.Height);
            }

            int srcRowBytes = e.FrameData.Length / e.Mode.Height;
            if (srcRowBytes < e.Mode.Width * 2) return;

            if (!MemoryMarshal.TryGetArray(e.FrameData, out var segment) || segment.Array == null)
                return;
            var frameBytes = segment.Array;

            // Ensure VANC geometry is detected (shared static state)
            VideoPreviewViewModel.EnsureVancDetected(frameBytes, srcRowBytes, e.Mode.Height);

            int prevW = _previewWidth;
            int prevH = _previewHeight;
            int rgbSize = prevW * prevH * 4;

            if (_rgbBufferA == null || _rgbBufferA.Length != rgbSize)
                _rgbBufferA = new byte[rgbSize];
            if (_rgbBufferB == null || _rgbBufferB.Length != rgbSize)
                _rgbBufferB = new byte[rgbSize];

            _conversionInProgress = true;
            var rgbArray = _currentRgbBuffer == 0 ? _rgbBufferA : _rgbBufferB;
            _currentRgbBuffer = 1 - _currentRgbBuffer;

            int frameWidth = e.Mode.Width;
            int frameHeight = e.Mode.Height;
            int localSrcRowBytes = srcRowBytes;
            int localDiv = _resDivisor;

            Task.Run(() =>
            {
                try
                {
                    ConvertYuv422Scaled(frameBytes, rgbArray, frameWidth, frameHeight,
                        prevW, prevH, localSrcRowBytes, localDiv);

                    // Clear bottom rows if effectiveHeight < dstHeight due to VANC
                    int vancRows = Math.Max(0, VideoPreviewViewModel.DetectedVancRows);
                    int effectiveHeight = Math.Min(prevH, (frameHeight - vancRows) / localDiv);
                    if (effectiveHeight < prevH)
                    {
                        int clearStart = effectiveHeight * prevW * 4;
                        int clearEnd = prevH * prevW * 4;
                        if (clearStart < clearEnd)
                            Array.Clear(rgbArray, clearStart, clearEnd - clearStart);
                    }

                    _conversionInProgress = false;

                    // Hand off to CompositionTarget.Rendering
                    _readyWidth = prevW;
                    _readyHeight = prevH;
                    _readyFrame = rgbArray;
                }
                catch
                {
                    _conversionInProgress = false;
                }
            });
        }
        catch
        {
            // Swallow frame processing errors
        }
    }

    private void OnStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        if (e.NewStatus == DeviceStatus.Error || e.NewStatus == DeviceStatus.Disconnected)
            _input.HasSignal = false;
    }

    /// <summary>
    /// Convert UYVY to BGRA at 1/divisor resolution.
    /// divisor=2: half-res (960x540), divisor=4: quarter-res (480x270).
    /// Reuses the static YUV->RGB lookup tables from VideoPreviewViewModel.
    /// </summary>
    internal static void ConvertYuv422Scaled(byte[] yuv, byte[] rgb,
        int srcWidth, int srcHeight, int dstWidth, int dstHeight,
        int srcRowBytes, int divisor)
    {
        int destRowBytes = dstWidth * 4;
        int vancRows = Math.Max(0, VideoPreviewViewModel.DetectedVancRows);
        int hancBytes = VideoPreviewViewModel.DetectedHancBytes;
        int effectiveHeight = Math.Min(dstHeight, (srcHeight - vancRows) / divisor);

        // Bytes per dest pixel pair in source: divisor UYVY groups x 4 bytes each
        int srcBytesPerDstPair = divisor * 4;
        // Y1 sample offset: pick Y from the group at divisor/2
        int y1Offset = (divisor / 2) * 4 + 1;

        int availableSrcBytes = srcRowBytes - hancBytes;
        int maxDstPairs = Math.Min(dstWidth / 2, Math.Max(0, (availableSrcBytes - y1Offset - 1) / srcBytesPerDstPair));

        // Cache table references for inner loop performance
        var ytoc = VideoPreviewViewModel.YtoC;
        var utog = VideoPreviewViewModel.UtoG;
        var utob = VideoPreviewViewModel.UtoB;
        var vtor = VideoPreviewViewModel.VtoR;
        var vtog = VideoPreviewViewModel.VtoG;
        var clamp = VideoPreviewViewModel.ClampTable;
        int clampOff = VideoPreviewViewModel.ClampOffset;

        Parallel.For(0, effectiveHeight, dstRow =>
        {
            int rgbRowStart = dstRow * destRowBytes;
            int srcRow = (dstRow * divisor) + vancRows;
            int yuvRowStart = srcRow * srcRowBytes + hancBytes;
            int rgbIndex = rgbRowStart;

            for (int dstPair = 0; dstPair < maxDstPairs; dstPair++)
            {
                int yuvIndex = yuvRowStart + dstPair * srcBytesPerDstPair;

                // UYVY format: U Y V Y
                int u = yuv[yuvIndex];
                int y0 = yuv[yuvIndex + 1];
                int v = yuv[yuvIndex + 2];
                int y1 = yuv[yuvIndex + y1Offset];

                int c0 = ytoc[y0];
                int c1 = ytoc[y1];
                int ug = utog[u];
                int ub = utob[u];
                int vr = vtor[v];
                int vg = vtog[v];

                // First pixel (BGRA)
                rgb[rgbIndex] = clamp[clampOff + ((c0 + ub + 128) >> 8)];
                rgb[rgbIndex + 1] = clamp[clampOff + ((c0 + ug + vg + 128) >> 8)];
                rgb[rgbIndex + 2] = clamp[clampOff + ((c0 + vr + 128) >> 8)];
                rgb[rgbIndex + 3] = 255;

                // Second pixel
                rgb[rgbIndex + 4] = clamp[clampOff + ((c1 + ub + 128) >> 8)];
                rgb[rgbIndex + 5] = clamp[clampOff + ((c1 + ug + vg + 128) >> 8)];
                rgb[rgbIndex + 6] = clamp[clampOff + ((c1 + vr + 128) >> 8)];
                rgb[rgbIndex + 7] = 255;

                rgbIndex += 8;
            }
        });
    }
}
