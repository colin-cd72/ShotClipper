using Screener.Abstractions.Capture;

namespace Screener.Capture.Virtual;

/// <summary>
/// Virtual capture device that emits a solid color UYVY frame at 30fps.
/// </summary>
public sealed class ColorCaptureDevice : ICaptureDevice
{
    private const int Width = 1920;
    private const int Height = 1080;

    private CancellationTokenSource? _cts;
    private Task? _emitTask;
    private DeviceStatus _status = DeviceStatus.Idle;
    private VideoMode? _currentMode;
    private long _frameCount;
    private bool _disposed;
    private byte[]? _colorFrame;

    public string DeviceId { get; }
    public string DisplayName { get; }
    public DeviceStatus Status => _status;
    public VideoMode? CurrentVideoMode => _currentMode;

    public IReadOnlyList<VideoMode> SupportedVideoModes { get; } = new List<VideoMode>
    {
        new(Width, Height, FrameRate.Fps30, PixelFormat.UYVY, false, "1080p30 (Color)")
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

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public ColorCaptureDevice(string deviceId, string displayName, byte r, byte g, byte b)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        R = r;
        G = g;
        B = b;
    }

    public Task<bool> StartCaptureAsync(VideoMode mode, CancellationToken ct = default)
    {
        if (_status == DeviceStatus.Capturing)
            return Task.FromResult(false);

        _currentMode = SupportedVideoModes[0];
        _frameCount = 0;

        // Convert RGB to UYVY (BT.601) and fill the entire frame
        int y = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
        int u = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
        int v = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;

        byte yb = (byte)Math.Clamp(y, 0, 255);
        byte ub = (byte)Math.Clamp(u, 0, 255);
        byte vb = (byte)Math.Clamp(v, 0, 255);

        var frameSize = Width * Height * 2;
        _colorFrame = new byte[frameSize];
        for (int i = 0; i < frameSize; i += 4)
        {
            _colorFrame[i] = ub;
            _colorFrame[i + 1] = yb;
            _colorFrame[i + 2] = vb;
            _colorFrame[i + 3] = yb;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SetStatus(DeviceStatus.Capturing);
        _emitTask = EmitFramesAsync(_cts.Token);

        return Task.FromResult(true);
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

    private async Task EmitFramesAsync(CancellationToken ct)
    {
        var frameData = _colorFrame!;
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
