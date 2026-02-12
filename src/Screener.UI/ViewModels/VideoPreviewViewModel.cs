using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Preview;

namespace Screener.UI.ViewModels;

public partial class VideoPreviewViewModel : ObservableObject
{
    private readonly ILogger<VideoPreviewViewModel> _logger;
    private readonly IDeviceManager _deviceManager;
    private readonly AudioPreviewService _audioPreviewService;
    private ICaptureDevice? _currentDevice;
    private WriteableBitmap? _previewBitmap;
    private int _backBufferStride;

    [ObservableProperty]
    private bool _hasSignal;

    [ObservableProperty]
    private int _width = 1920;

    [ObservableProperty]
    private int _height = 1080;

    [ObservableProperty]
    private double _frameRate = 29.97;

    [ObservableProperty]
    private string _colorSpace = "Rec.709";

    [ObservableProperty]
    private bool _showSafeArea;

    [ObservableProperty]
    private bool _showCenterCross;

    [ObservableProperty]
    private bool _showRuleOfThirds;

    [ObservableProperty]
    private ImageSource? _previewImage;

    public string FormatDescription => $"{Width}x{Height} {FrameRate:F2}fps {ColorSpace}";

    public VideoPreviewViewModel(
        ILogger<VideoPreviewViewModel> logger,
        IDeviceManager deviceManager,
        AudioPreviewService audioPreviewService)
    {
        _logger = logger;
        _deviceManager = deviceManager;
        _audioPreviewService = audioPreviewService;
    }

    public async Task StartPreviewAsync(string deviceId, VideoConnector connector)
    {
        try
        {
            // Stop any existing preview
            await StopPreviewAsync();

            // Get the device
            _currentDevice = await _deviceManager.GetDeviceAsync(deviceId);
            if (_currentDevice == null)
            {
                _logger.LogWarning("Device not found: {DeviceId}", deviceId);
                return;
            }

            // Set the connector
            _currentDevice.SelectedConnector = connector;

            // Subscribe to frame events
            _currentDevice.VideoFrameReceived += OnVideoFrameReceived;
            _currentDevice.AudioSamplesReceived += OnAudioSamplesReceived;
            _currentDevice.StatusChanged += OnStatusChanged;

            // Start audio preview
            _audioPreviewService.Start();

            // Prefer 1080p59.94 mode (progressive) - exact match for common broadcast/streaming
            // The SDK will auto-detect the actual format when capture starts
            var mode = _currentDevice.SupportedVideoModes
                .FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && !m.IsInterlaced && m.FrameRate.Value > 59.9 && m.FrameRate.Value < 60.0)  // 59.94
                ?? _currentDevice.SupportedVideoModes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && !m.IsInterlaced && m.FrameRate.Value >= 59.0)
                ?? _currentDevice.SupportedVideoModes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080 && !m.IsInterlaced)
                ?? _currentDevice.SupportedVideoModes.FirstOrDefault(m => m.Width == 1920)
                ?? _currentDevice.SupportedVideoModes.FirstOrDefault();

            if (mode == null)
            {
                _logger.LogWarning("No supported video modes for device {DeviceId}", deviceId);
                return;
            }

            _logger.LogInformation("Selected initial capture mode: {Mode}", mode.DisplayName);

            // Initialize the preview bitmap
            Width = mode.Width;
            Height = mode.Height;
            FrameRate = mode.FrameRate.Value;
            InitializePreviewBitmap(mode.Width, mode.Height);

            // Start capture
            var success = await _currentDevice.StartCaptureAsync(mode);
            if (success)
            {
                _logger.LogInformation("Preview started on {DeviceName} using {Connector}",
                    _currentDevice.DisplayName, connector);
            }
            else
            {
                _logger.LogError("Failed to start capture on {DeviceName}", _currentDevice.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting preview");
        }
    }

    public async Task StopPreviewAsync()
    {
        _audioPreviewService.Stop();

        if (_currentDevice != null)
        {
            _currentDevice.VideoFrameReceived -= OnVideoFrameReceived;
            _currentDevice.AudioSamplesReceived -= OnAudioSamplesReceived;
            _currentDevice.StatusChanged -= OnStatusChanged;
            await _currentDevice.StopCaptureAsync();
            _currentDevice = null;
        }

        if (_renderingSubscribed)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                CompositionTarget.Rendering -= OnCompositionTargetRendering;
            });
            _renderingSubscribed = false;
        }

        _readyFrame = null;
        HasSignal = false;
    }

    // Preview at half resolution for performance (WPF scales up automatically)
    private int _previewWidth;
    private int _previewHeight;

    private void InitializePreviewBitmap(int width, int height)
    {
        _previewWidth = width / 2;
        _previewHeight = height / 2;
        Application.Current.Dispatcher.Invoke(() =>
        {
            _previewBitmap = new WriteableBitmap(_previewWidth, _previewHeight, 96, 96, PixelFormats.Bgr32, null);
            _backBufferStride = _previewBitmap.BackBufferStride;
            PreviewImage = _previewBitmap;
            _logger.LogInformation("Created WriteableBitmap: {Width}x{Height} (preview half-res from {SrcW}x{SrcH}), BackBufferStride={Stride}",
                _previewWidth, _previewHeight, width, height, _backBufferStride);

            // Subscribe to CompositionTarget.Rendering (fires at vsync, ~60fps)
            // This ensures WritePixels happens in sync with WPF's render pass
            if (!_renderingSubscribed)
            {
                CompositionTarget.Rendering += OnCompositionTargetRendering;
                _renderingSubscribed = true;
            }
        });
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        // Atomically grab and clear the ready frame
        var frame = Interlocked.Exchange(ref _readyFrame, null);
        if (frame == null) return;

        try
        {
            _previewBitmap?.WritePixels(
                new Int32Rect(0, 0, _readyWidth, _readyHeight),
                frame,
                _readyWidth * 4,
                0);

            // Measure UI frame rate
            _uiFrameCount++;
            long now2 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_uiFpsTimerStart == 0)
                _uiFpsTimerStart = now2;
            long elapsed = now2 - _uiFpsTimerStart;
            if (elapsed >= System.Diagnostics.Stopwatch.Frequency * 5)
            {
                double secs = (double)elapsed / System.Diagnostics.Stopwatch.Frequency;
                double fps = _uiFrameCount / secs;
                _logger.LogInformation(
                    "UI: {Fps:F1}fps ({UiFrames} rendered) | Callbacks={Callbacks} RateDrop={RateDrop} BusyDrop={BusyDrop} ConvStart={ConvStart} in {Secs:F1}s",
                    fps, _uiFrameCount, _diagCallbackCount, _diagRateLimitRejects, _diagConversionBusyRejects, _diagConversionsStarted, secs);
                _uiFrameCount = 0;
                _uiFpsTimerStart = now2;
                _diagCallbackCount = 0;
                _diagRateLimitRejects = 0;
                _diagConversionBusyRejects = 0;
                _diagConversionsStarted = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating preview bitmap");
        }
    }

    // UI frame rate measurement
    private long _uiFrameCount;
    private long _uiFpsTimerStart;

    // Double-buffered: two RGB buffers to decouple conversion from UI rendering
    private byte[]? _rgbBufferA;
    private byte[]? _rgbBufferB;
    private int _currentRgbBuffer; // 0 = A, 1 = B
    private volatile bool _conversionInProgress;

    // CompositionTarget.Rendering: latest converted frame ready for WritePixels
    // _readyFrame is volatile to ensure memory ordering: when the UI thread reads a non-null
    // value, the prior writes to _readyWidth/_readyHeight are guaranteed visible (release/acquire)
    private volatile byte[]? _readyFrame;
    private int _readyWidth;
    private int _readyHeight;
    private bool _renderingSubscribed;

    // Diagnostic counters (reset every 5s with FPS counter)
    private long _diagCallbackCount;
    private long _diagRateLimitRejects;
    private long _diagConversionBusyRejects;
    private long _diagConversionsStarted;

    private void OnVideoFrameReceived(object? sender, VideoFrameEventArgs e)
    {
        if (_previewBitmap == null) return;

        _diagCallbackCount++;

        // Accept every 2nd frame: 59.94fps -> 29.97fps
        // Frame counting is immune to GC jitter (unlike timestamp-based limiting)
        if (_diagCallbackCount % 2 != 0)
        {
            _diagRateLimitRejects++;
            return;
        }

        // Skip if previous conversion still running
        if (_conversionInProgress)
        {
            _diagConversionBusyRejects++;
            return;
        }

        _diagConversionsStarted++;

        try
        {
            HasSignal = true;

            // Update format info from the frame's mode
            if (e.Mode.Width != Width || e.Mode.Height != Height || Math.Abs(e.Mode.FrameRate.Value - FrameRate) > 0.1)
            {
                Width = e.Mode.Width;
                Height = e.Mode.Height;
                FrameRate = e.Mode.FrameRate.Value;
                OnPropertyChanged(nameof(FormatDescription));
                _logger.LogInformation("Updated format display: {Format}", FormatDescription);

                // Reset VANC/HANC detection for new format
                _detectedVancRows = -1;
                _detectedHancBytes = 0;

                // Reinitialize bitmap if size changed
                InitializePreviewBitmap(e.Mode.Width, e.Mode.Height);
            }

            // Calculate source row bytes from frame data (rowBytes = totalBytes / height)
            // For UYVY: minimum is width * 2, but DeckLink may have padding
            int srcRowBytes = e.FrameData.Length / e.Mode.Height;
            int minRowBytes = e.Mode.Width * 2;

            if (srcRowBytes < minRowBytes)
            {
                _logger.LogWarning("Frame data size {Actual} implies rowBytes {SrcRow} < minimum {MinRow}",
                    e.FrameData.Length, srcRowBytes, minRowBytes);
                return;
            }

            int frameWidth = e.Mode.Width;
            int frameHeight = e.Mode.Height;
            int prevW = _previewWidth;
            int prevH = _previewHeight;
            int rgbSize = prevW * prevH * 4;

            // Get backing array directly from event data (already a copy from DeckLink ring buffer)
            // This avoids a redundant 4MB copy per frame
            if (!MemoryMarshal.TryGetArray(e.FrameData, out var segment) || segment.Array == null)
                return;
            var frameBytes = segment.Array;

            // Auto-detect VANC rows and HANC offset on first valid frame
            if (_detectedVancRows < 0)
            {
                lock (_vancDetectionLock)
                {
                    if (_detectedVancRows < 0)
                    {
                        DetectFrameGeometry(frameBytes, srcRowBytes, frameHeight);
                    }
                }
            }

            // Reuse RGB buffers - double-buffered to decouple conversion from UI rendering
            if (_rgbBufferA == null || _rgbBufferA.Length != rgbSize)
                _rgbBufferA = new byte[rgbSize];
            if (_rgbBufferB == null || _rgbBufferB.Length != rgbSize)
                _rgbBufferB = new byte[rgbSize];

            // Mark conversion as in progress
            _conversionInProgress = true;

            // Pick the next RGB buffer (alternate between A and B)
            var rgbArray = _currentRgbBuffer == 0 ? _rgbBufferA : _rgbBufferB;
            _currentRgbBuffer = 1 - _currentRgbBuffer;

            int localSrcRowBytes = srcRowBytes;

            // Convert YUV to BGRA at half resolution on thread pool
            Task.Run(() =>
            {
                try
                {
                    long tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    ConvertYuv422ToRgbHalfRes(frameBytes, rgbArray, frameWidth, frameHeight, prevW, prevH, localSrcRowBytes);

                    // Clear bottom rows that weren't written (effectiveHeight may be < dstHeight due to VANC)
                    int vancRows = Math.Max(0, _detectedVancRows);
                    int effectiveHeight = Math.Min(prevH, (frameHeight - vancRows) / 2);
                    if (effectiveHeight < prevH)
                    {
                        int clearStart = effectiveHeight * prevW * 4;
                        int clearEnd = prevH * prevW * 4;
                        Array.Clear(rgbArray, clearStart, clearEnd - clearStart);
                    }

                    long tc1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    double convMs = (tc1 - tc0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

                    // Release conversion lock immediately so next frame can start
                    _conversionInProgress = false;

                    if (_diagConversionsStarted <= 5)
                        _logger.LogInformation("Perf: convert={ConvMs:F1}ms", convMs);

                    // Hand off to CompositionTarget.Rendering (fires at vsync on UI thread)
                    // No Dispatcher.BeginInvoke needed - the rendering handler picks this up
                    _readyWidth = prevW;
                    _readyHeight = prevH;
                    _readyFrame = rgbArray;  // volatile-like: set last so dimensions are visible
                }
                catch (Exception ex)
                {
                    _conversionInProgress = false;
                    _logger.LogError(ex, "Error converting frame");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing video frame");
        }
    }

    // Auto-detection of VANC rows (vertical) and HANC offset (horizontal).
    // Detected once on the first valid frame, reset on format change.
    private static int _detectedVancRows = -1; // -1 = not yet detected
    private static int _detectedHancBytes = 0;  // horizontal byte offset per row
    private static readonly object _vancDetectionLock = new();

    internal static int DetectedVancRows => _detectedVancRows;
    internal static int DetectedHancBytes => _detectedHancBytes;

    /// <summary>
    /// Ensures VANC geometry has been detected. Called by InputPreviewRenderer on first frame.
    /// </summary>
    internal static void EnsureVancDetected(byte[] frameData, int srcRowBytes, int height)
    {
        if (_detectedVancRows >= 0) return;
        lock (_vancDetectionLock)
        {
            if (_detectedVancRows >= 0) return;
            int maxScan = Math.Min(120, height);
            int firstVideoRow = 0;
            for (int row = 0; row < maxScan; row++)
            {
                int rowStart = row * srcRowBytes;
                if (rowStart + 4 >= frameData.Length) break;
                byte b0 = frameData[rowStart];
                byte b1 = frameData[rowStart + 1];
                byte b2 = frameData[rowStart + 2];
                byte b3 = frameData[rowStart + 3];
                bool isAnc = (b0 == 0x00 && b1 == 0x02 && b2 == 0x01 && b3 == 0x20);
                bool isZero = (b0 == 0x00 && b1 == 0x00 && b2 == 0x00 && b3 == 0x00);
                if (!isAnc && !isZero) { firstVideoRow = row; break; }
                firstVideoRow = row + 1;
            }
            _detectedVancRows = firstVideoRow;
        }
    }

    internal static void ResetVancDetection()
    {
        lock (_vancDetectionLock)
        {
            _detectedVancRows = -1;
            _detectedHancBytes = 0;
        }
    }

    /// <summary>
    /// Detect both VANC rows (vertical) and HANC bytes (horizontal) in one pass.
    /// Sets _detectedVancRows and _detectedHancBytes.
    /// </summary>
    private void DetectFrameGeometry(byte[] frameData, int rowBytes, int height)
    {
        // --- Step 1: Detect VANC rows (vertical ancillary at top of buffer) ---
        int maxScan = Math.Min(120, height);
        int firstVideoRow = 0;

        for (int row = 0; row < maxScan; row++)
        {
            int rowStart = row * rowBytes;
            if (rowStart + 4 >= frameData.Length) break;

            byte b0 = frameData[rowStart];
            byte b1 = frameData[rowStart + 1];
            byte b2 = frameData[rowStart + 2];
            byte b3 = frameData[rowStart + 3];

            // ANC rows start with 00 02 01 20 (SMPTE 291 DID/SDID framing)
            // or all-zeros (unused VBI lines)
            bool isAncStart = (b0 == 0x00 && b1 == 0x02 && b2 == 0x01 && b3 == 0x20);
            bool isZeroRow = (b0 == 0x00 && b1 == 0x00 && b2 == 0x00 && b3 == 0x00);

            if (!isAncStart && !isZeroRow)
            {
                firstVideoRow = row;
                break;
            }
            firstVideoRow = row + 1;
        }

        _detectedVancRows = firstVideoRow;
        _logger.LogInformation("VANC detection: first video row = {Row} (scanned {Max} rows)", firstVideoRow, maxScan);

        // --- Step 2: Detect HANC offset (horizontal ancillary at start of each row) ---
        // Sample several video rows in the middle of the frame to find a consistent left edge.
        // The left edge is where data transitions from non-video (zeros, ANC pattern, black)
        // to actual varying video content.
        int hancOffset = DetectHancOffset(frameData, rowBytes, height, firstVideoRow);
        _detectedHancBytes = hancOffset;

        _logger.LogInformation("HANC detection: horizontal byte offset = {Offset} ({Pixels} pixels)",
            hancOffset, hancOffset / 2);

        // --- Step 3: Detailed dump of a middle video row ---
        // This reveals the exact horizontal byte structure across the full row
        int diagRow = firstVideoRow + (height - firstVideoRow) / 2; // middle of video area
        int diagStart = diagRow * rowBytes;
        if (diagStart + rowBytes <= frameData.Length)
        {
            _logger.LogInformation("Detailed dump of video row {Row} (rowBytes={RowBytes}):", diagRow, rowBytes);

            // Dump in 128-byte chunks across the full row to show where content transitions
            for (int chunk = 0; chunk < rowBytes; chunk += 128)
            {
                int pos = diagStart + chunk;
                int end = Math.Min(chunk + 128, rowBytes);

                // Summarize this chunk: count black(80 10), zero(00 00), and non-trivial bytes
                int blackCount = 0, zeroCount = 0, otherCount = 0;
                byte firstU = frameData[pos], firstY = frameData[pos + 1];

                for (int b = chunk; b < end; b += 4)
                {
                    int p = diagStart + b;
                    if (frameData[p] == 0x80 && frameData[p + 1] == 0x10 && frameData[p + 2] == 0x80 && frameData[p + 3] == 0x10)
                        blackCount++;
                    else if (frameData[p] == 0 && frameData[p + 1] == 0 && frameData[p + 2] == 0 && frameData[p + 3] == 0)
                        zeroCount++;
                    else
                        otherCount++;
                }

                // Only log transitions and first/last chunks to keep output manageable
                if (chunk == 0 || chunk + 128 >= rowBytes || otherCount > 0 || (chunk > 0 && blackCount == 0))
                {
                    _logger.LogInformation(
                        "  @{Offset,4}: {B0:X2}{B1:X2}{B2:X2}{B3:X2} {B4:X2}{B5:X2}{B6:X2}{B7:X2} ... blk={Blk} zero={Zero} vid={Other}",
                        chunk,
                        frameData[pos], frameData[pos + 1], frameData[pos + 2], frameData[pos + 3],
                        frameData[pos + 4], frameData[pos + 5], frameData[pos + 6], frameData[pos + 7],
                        blackCount, zeroCount, otherCount);
                }
            }
        }

        // Also dump a few complete UYVY groups from the very start of a bright video row
        // to verify UYVY byte ordering is correct
        int brightRow = -1;
        for (int r = firstVideoRow; r < Math.Min(height, firstVideoRow + 500); r++)
        {
            int pos = r * rowBytes;
            // Look for a row where Y > 64 (not black)
            if (frameData[pos + 1] > 64 || frameData[pos + 3] > 64)
            {
                brightRow = r;
                break;
            }
        }

        if (brightRow >= 0)
        {
            int pos = brightRow * rowBytes;
            _logger.LogInformation(
                "First bright row {Row}: first 32 bytes = {D}",
                brightRow,
                BitConverter.ToString(frameData, pos, 32));
        }
    }

    /// <summary>
    /// Scan video rows horizontally to find where active video content begins.
    /// The DMA buffer may include HANC (horizontal ancillary) data at the start of each row.
    /// We detect this by scanning byte-by-byte looking for the transition from
    /// non-video patterns to actual UYVY video content.
    /// </summary>
    private int DetectHancOffset(byte[] frameData, int rowBytes, int height, int vancRows)
    {
        // Sample multiple video rows spread across the frame
        int[] sampleRows = {
            vancRows + (height - vancRows) / 4,
            vancRows + (height - vancRows) / 3,
            vancRows + (height - vancRows) / 2,
            vancRows + 2 * (height - vancRows) / 3,
        };

        // For each sample row, scan from byte 0 in 4-byte steps (UYVY alignment)
        // to find where non-video data ends and real video starts.
        // Non-video patterns at row start: ANC (00 02 01 20), zeros, black (80 10 80 10),
        // or SAV/EAV timing references (FF 00 00 XX).
        int[] detectedOffsets = new int[sampleRows.Length];

        for (int s = 0; s < sampleRows.Length; s++)
        {
            int row = sampleRows[s];
            if (row >= height) { detectedOffsets[s] = 0; continue; }

            int rowStart = row * rowBytes;
            if (rowStart + rowBytes > frameData.Length) { detectedOffsets[s] = 0; continue; }

            int offset = 0;

            // Walk 4 bytes at a time (UYVY pixel pair alignment)
            for (int byteOff = 0; byteOff < rowBytes - 4; byteOff += 4)
            {
                int pos = rowStart + byteOff;
                byte b0 = frameData[pos];
                byte b1 = frameData[pos + 1];
                byte b2 = frameData[pos + 2];
                byte b3 = frameData[pos + 3];

                bool isAnc = (b0 == 0x00 && b1 == 0x02 && b2 == 0x01 && b3 == 0x20);
                bool isZero = (b0 == 0x00 && b1 == 0x00 && b2 == 0x00 && b3 == 0x00);
                bool isTimingRef = (b0 == 0xFF && b1 == 0x00 && b2 == 0x00); // EAV/SAV

                if (isAnc || isZero || isTimingRef)
                {
                    offset = byteOff + 4; // Skip past this non-video data
                    continue;
                }

                // Found non-blanking data. But we need to confirm it's real video,
                // not a single stray value. Check that the next 16 bytes also look like video
                // (have varying values, not a repeating pattern).
                if (byteOff + 20 < rowBytes)
                {
                    bool looksLikeVideo = false;
                    byte firstU = b0;
                    byte firstY = b1;

                    // Check if subsequent UYVY groups have varying luma (real video content)
                    for (int check = 4; check < 20; check += 4)
                    {
                        byte checkY = frameData[pos + check + 1];
                        if (checkY != firstY)
                        {
                            looksLikeVideo = true;
                            break;
                        }
                    }

                    if (!looksLikeVideo)
                    {
                        // Uniform values — could still be blanking
                        offset = byteOff + 4;
                        continue;
                    }
                }

                // This position looks like real video content
                offset = byteOff;
                break;
            }

            detectedOffsets[s] = offset;
        }

        // Log what each sample row detected
        for (int s = 0; s < sampleRows.Length; s++)
        {
            int row = sampleRows[s];
            int rowStart = row * rowBytes;
            if (rowStart + detectedOffsets[s] + 8 <= frameData.Length)
            {
                int pos = rowStart + detectedOffsets[s];
                _logger.LogInformation(
                    "  HANC scan row {Row}: offset={Offset} bytes ({Pixels}px), data={B0:X2} {B1:X2} {B2:X2} {B3:X2} {B4:X2} {B5:X2} {B6:X2} {B7:X2}",
                    row, detectedOffsets[s], detectedOffsets[s] / 2,
                    frameData[pos], frameData[pos + 1], frameData[pos + 2], frameData[pos + 3],
                    frameData[pos + 4], frameData[pos + 5], frameData[pos + 6], frameData[pos + 7]);
            }
        }

        // Use the median offset (robust against outliers from noise or unusual row content)
        Array.Sort(detectedOffsets);
        int medianOffset = detectedOffsets[detectedOffsets.Length / 2];

        // Must be aligned to 4 bytes (UYVY pixel pair boundary)
        return medianOffset & ~3;
    }

    // Pre-computed lookup tables for YUV->RGB conversion (internal for use by InputPreviewRenderer)
    internal static readonly int[] YtoC = new int[256];   // 298 * (Y - 16), clamped
    internal static readonly int[] UtoG = new int[256];   // -100 * (U - 128)
    internal static readonly int[] UtoB = new int[256];   // 516 * (U - 128)
    internal static readonly int[] VtoR = new int[256];   // 409 * (V - 128)
    internal static readonly int[] VtoG = new int[256];   // -208 * (V - 128)
    internal const int ClampOffset = 512;
    internal static readonly byte[] ClampTable = new byte[ClampOffset + 768]; // Clamp table for -512..767 range

    static VideoPreviewViewModel()
    {
        // Initialize YUV->RGB lookup tables
        for (int i = 0; i < 256; i++)
        {
            int y = i - 16;
            YtoC[i] = y < 0 ? 0 : 298 * y;
            UtoG[i] = -100 * (i - 128);
            UtoB[i] = 516 * (i - 128);
            VtoR[i] = 409 * (i - 128);
            VtoG[i] = -208 * (i - 128);
        }
        // Initialize clamp table (offset by ClampOffset to handle negative values)
        for (int i = 0; i < ClampTable.Length; i++)
        {
            int v = i - ClampOffset;
            ClampTable[i] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
    }

    private static void ConvertYuv422ToRgbArray(byte[] yuv, byte[] rgb, int width, int height, int srcRowBytes)
    {
        // DeckLink 8-bit YUV is UYVY format: U0 Y0 V0 Y1 (4 bytes for 2 pixels)
        // Use parallel processing with pre-computed lookup tables
        int destRowBytes = width * 4;
        int vancRows = Math.Max(0, _detectedVancRows);
        int hancBytes = _detectedHancBytes;
        int effectiveHeight = height - vancRows;
        int pixelPairsPerRow = width / 2;

        Parallel.For(0, effectiveHeight, row =>
        {
            int rgbRowStart = row * destRowBytes;
            int srcRow = row + vancRows;
            int yuvRowStart = srcRow * srcRowBytes + hancBytes;
            int rgbIndex = rgbRowStart;

            for (int pair = 0; pair < pixelPairsPerRow; pair++)
            {
                int yuvIndex = yuvRowStart + (pair << 2); // pair * 4

                // UYVY format: U Y V Y
                int u = yuv[yuvIndex];
                int y0 = yuv[yuvIndex + 1];
                int v = yuv[yuvIndex + 2];
                int y1 = yuv[yuvIndex + 3];

                // Lookup pre-computed values
                int c0 = YtoC[y0];
                int c1 = YtoC[y1];
                int ug = UtoG[u];
                int ub = UtoB[u];
                int vr = VtoR[v];
                int vg = VtoG[v];

                // First pixel - use clamp table (offset ClampOffset for negative handling)
                rgb[rgbIndex] = ClampTable[ClampOffset + ((c0 + ub + 128) >> 8)];
                rgb[rgbIndex + 1] = ClampTable[ClampOffset + ((c0 + ug + vg + 128) >> 8)];
                rgb[rgbIndex + 2] = ClampTable[ClampOffset + ((c0 + vr + 128) >> 8)];
                rgb[rgbIndex + 3] = 255;

                // Second pixel
                rgb[rgbIndex + 4] = ClampTable[ClampOffset + ((c1 + ub + 128) >> 8)];
                rgb[rgbIndex + 5] = ClampTable[ClampOffset + ((c1 + ug + vg + 128) >> 8)];
                rgb[rgbIndex + 6] = ClampTable[ClampOffset + ((c1 + vr + 128) >> 8)];
                rgb[rgbIndex + 7] = 255;

                rgbIndex += 8;
            }
        });
    }

    private static void ConvertYuv422ToRgbHalfRes(byte[] yuv, byte[] rgb, int srcWidth, int srcHeight, int dstWidth, int dstHeight, int srcRowBytes)
    {
        // Convert UYVY to BGRA at half resolution (skip every other row and pixel pair)
        int destRowBytes = dstWidth * 4;
        int vancRows = Math.Max(0, _detectedVancRows);
        int hancBytes = _detectedHancBytes;
        int effectiveHeight = Math.Min(dstHeight, (srcHeight - vancRows) / 2);

        // Cap pixel pairs to not read past end of row after HANC offset
        // Each half-res dest pair reads 8 source bytes (2 UYVY groups), plus needs +5 for y1
        int availableSrcBytes = srcRowBytes - hancBytes;
        int maxDstPairs = Math.Min(dstWidth / 2, (availableSrcBytes - 6) / 8); // -6 for the +5 access in last pair

        Parallel.For(0, effectiveHeight, dstRow =>
        {
            int rgbRowStart = dstRow * destRowBytes;
            int srcRow = (dstRow * 2) + vancRows;
            int yuvRowStart = srcRow * srcRowBytes + hancBytes;
            int rgbIndex = rgbRowStart;

            for (int dstPair = 0; dstPair < maxDstPairs; dstPair++)
            {
                // Sample every other pixel pair from the source (skip 2 source pixel pairs per dest pair)
                int yuvIndex = yuvRowStart + (dstPair << 3); // dstPair * 8 (skip every other UYVY group)

                // UYVY format: U Y V Y - take first pixel from first pair, first from second pair
                int u = yuv[yuvIndex];
                int y0 = yuv[yuvIndex + 1];
                int v = yuv[yuvIndex + 2];
                int y1 = yuv[yuvIndex + 5]; // Y from next pixel pair

                int c0 = YtoC[y0];
                int c1 = YtoC[y1];
                int ug = UtoG[u];
                int ub = UtoB[u];
                int vr = VtoR[v];
                int vg = VtoG[v];

                rgb[rgbIndex] = ClampTable[ClampOffset + ((c0 + ub + 128) >> 8)];
                rgb[rgbIndex + 1] = ClampTable[ClampOffset + ((c0 + ug + vg + 128) >> 8)];
                rgb[rgbIndex + 2] = ClampTable[ClampOffset + ((c0 + vr + 128) >> 8)];
                rgb[rgbIndex + 3] = 255;

                rgb[rgbIndex + 4] = ClampTable[ClampOffset + ((c1 + ub + 128) >> 8)];
                rgb[rgbIndex + 5] = ClampTable[ClampOffset + ((c1 + ug + vg + 128) >> 8)];
                rgb[rgbIndex + 6] = ClampTable[ClampOffset + ((c1 + vr + 128) >> 8)];
                rgb[rgbIndex + 7] = 255;

                rgbIndex += 8;
            }
        });
    }

    private void OnStatusChanged(object? sender, DeviceStatusChangedEventArgs e)
    {
        _logger.LogInformation("Device status changed: {OldStatus} -> {NewStatus}", e.OldStatus, e.NewStatus);

        if (e.NewStatus == DeviceStatus.Error || e.NewStatus == DeviceStatus.Disconnected)
        {
            HasSignal = false;
        }
    }

    private void OnAudioSamplesReceived(object? sender, AudioSamplesEventArgs e)
    {
        // Configure audio format if needed
        _audioPreviewService.Configure(e.SampleRate, e.Channels, e.BitsPerSample);

        // Feed audio samples to the preview service for level metering
        _audioPreviewService.WriteSamples(e.SampleData.Span);
    }
}
