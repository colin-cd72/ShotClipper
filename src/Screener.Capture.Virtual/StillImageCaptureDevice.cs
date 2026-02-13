using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screener.Abstractions.Capture;
using PixelFormat = Screener.Abstractions.Capture.PixelFormat;

namespace Screener.Capture.Virtual;

/// <summary>
/// Virtual capture device that loads a PNG/JPEG image, converts it to UYVY,
/// and emits the static frame at 30fps.
/// </summary>
public sealed class StillImageCaptureDevice : ICaptureDevice
{
    private const int Width = 1920;
    private const int Height = 1080;

    private readonly string _imagePath;
    private CancellationTokenSource? _cts;
    private Task? _emitTask;
    private DeviceStatus _status = DeviceStatus.Idle;
    private VideoMode? _currentMode;
    private long _frameCount;
    private bool _disposed;
    private byte[]? _uyvyFrame;

    public string DeviceId { get; }
    public string DisplayName { get; }
    public DeviceStatus Status => _status;
    public VideoMode? CurrentVideoMode => _currentMode;

    public IReadOnlyList<VideoMode> SupportedVideoModes { get; } = new List<VideoMode>
    {
        new(Width, Height, FrameRate.Fps30, PixelFormat.UYVY, false, "1080p30 (Still)")
    };

    public IReadOnlyList<VideoConnector> AvailableConnectors { get; } =
        new List<VideoConnector> { VideoConnector.Virtual };

    public VideoConnector SelectedConnector
    {
        get => VideoConnector.Virtual;
        set { }
    }

    public event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;
    public event EventHandler<AudioSamplesEventArgs>? AudioSamplesReceived;
    public event EventHandler<DeviceStatusChangedEventArgs>? StatusChanged;

    public StillImageCaptureDevice(string deviceId, string displayName, string imagePath)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        _imagePath = imagePath;
    }

    public Task<bool> StartCaptureAsync(VideoMode mode, CancellationToken ct = default)
    {
        if (_status == DeviceStatus.Capturing)
            return Task.FromResult(false);

        try
        {
            SetStatus(DeviceStatus.Initializing);
            _currentMode = SupportedVideoModes[0];
            _frameCount = 0;

            // Load image and convert to UYVY (done once)
            _uyvyFrame = LoadAndConvertImage(_imagePath);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SetStatus(DeviceStatus.Capturing);
            _emitTask = EmitFramesAsync(_cts.Token);

            return Task.FromResult(true);
        }
        catch
        {
            SetStatus(DeviceStatus.Error);
            return Task.FromResult(false);
        }
    }

    public async Task StopCaptureAsync()
    {
        if (_status != DeviceStatus.Capturing)
            return;

        _cts?.Cancel();

        if (_emitTask != null)
        {
            try { await _emitTask; }
            catch (OperationCanceledException) { }
        }

        _currentMode = null;
        SetStatus(DeviceStatus.Idle);
    }

    /// <summary>
    /// Load image, scale to fit 1920x1080 with letterboxing, convert BGRA to UYVY.
    /// </summary>
    private static byte[] LoadAndConvertImage(string imagePath)
    {
        using var original = new Bitmap(imagePath);

        // Calculate letterbox dimensions (fit within 1920x1080, preserving aspect ratio)
        double scaleX = (double)Width / original.Width;
        double scaleY = (double)Height / original.Height;
        double scale = Math.Min(scaleX, scaleY);

        int scaledW = (int)(original.Width * scale);
        int scaledH = (int)(original.Height * scale);
        int offsetX = (Width - scaledW) / 2;
        int offsetY = (Height - scaledH) / 2;

        // Create 1920x1080 BGRA canvas (black background)
        using var canvas = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(original, offsetX, offsetY, scaledW, scaledH);
        }

        // Extract BGRA pixel data
        var bmpData = canvas.LockBits(
            new Rectangle(0, 0, Width, Height),
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var bgraData = new byte[Width * Height * 4];
        Marshal.Copy(bmpData.Scan0, bgraData, 0, bgraData.Length);
        canvas.UnlockBits(bmpData);

        // Convert BGRA to UYVY (BT.601)
        return BgraToUyvy(bgraData, Width, Height);
    }

    /// <summary>
    /// Convert BGRA pixel data to UYVY using BT.601 coefficients.
    /// Processes pixel pairs, averaging chroma across each pair.
    /// </summary>
    private static byte[] BgraToUyvy(byte[] bgra, int width, int height)
    {
        var uyvy = new byte[width * height * 2];
        int srcStride = width * 4;
        int dstStride = width * 2;

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * srcStride;
            int dstRow = y * dstStride;

            for (int x = 0; x < width; x += 2)
            {
                // Pixel 0: BGRA
                int s0 = srcRow + x * 4;
                int b0 = bgra[s0];
                int g0 = bgra[s0 + 1];
                int r0 = bgra[s0 + 2];

                // Pixel 1: BGRA
                int s1 = srcRow + (x + 1) * 4;
                int b1 = bgra[s1];
                int g1 = bgra[s1 + 1];
                int r1 = bgra[s1 + 2];

                // Y for each pixel (BT.601)
                int y0 = ((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16;
                int y1 = ((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16;

                // Average RGB for chroma
                int rAvg = (r0 + r1) >> 1;
                int gAvg = (g0 + g1) >> 1;
                int bAvg = (b0 + b1) >> 1;

                int u = ((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                int v = ((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128;

                // Clamp to [0, 255]
                int d = dstRow + x * 2;
                uyvy[d] = (byte)Math.Clamp(u, 0, 255);
                uyvy[d + 1] = (byte)Math.Clamp(y0, 0, 255);
                uyvy[d + 2] = (byte)Math.Clamp(v, 0, 255);
                uyvy[d + 3] = (byte)Math.Clamp(y1, 0, 255);
            }
        }

        return uyvy;
    }

    private async Task EmitFramesAsync(CancellationToken ct)
    {
        var frameData = _uyvyFrame!;
        var mode = _currentMode!;
        var intervalMs = (int)(1000.0 / mode.FrameRate.Value);

        while (!ct.IsCancellationRequested)
        {
            _frameCount++;

            VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
            {
                FrameData = frameData.AsMemory(),
                Mode = mode,
                Timestamp = TimeSpan.FromSeconds(_frameCount / mode.FrameRate.Value),
                FrameNumber = _frameCount
            });

            try
            {
                await Task.Delay(intervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
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

        _cts?.Cancel();
        _cts?.Dispose();
        SetStatus(DeviceStatus.Disconnected);
    }
}
