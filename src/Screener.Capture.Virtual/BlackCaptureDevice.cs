using Screener.Abstractions.Capture;

namespace Screener.Capture.Virtual;

/// <summary>
/// Virtual capture device that emits solid black UYVY frames at 30fps.
/// </summary>
public sealed class BlackCaptureDevice : ICaptureDevice
{
    private CancellationTokenSource? _cts;
    private Task? _emitTask;
    private DeviceStatus _status = DeviceStatus.Idle;
    private VideoMode? _currentMode;
    private long _frameCount;
    private bool _disposed;
    private byte[]? _blackFrame;

    public string DeviceId => "virtual-black";
    public string DisplayName => "Black";
    public DeviceStatus Status => _status;
    public VideoMode? CurrentVideoMode => _currentMode;

    public IReadOnlyList<VideoMode> SupportedVideoModes { get; } = new List<VideoMode>
    {
        new(1920, 1080, FrameRate.Fps30, PixelFormat.UYVY, false, "1080p30 (Black)")
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

    public Task<bool> StartCaptureAsync(VideoMode mode, CancellationToken ct = default)
    {
        if (_status == DeviceStatus.Capturing)
            return Task.FromResult(false);

        _currentMode = SupportedVideoModes[0];
        _frameCount = 0;

        // Pre-allocate 1920x1080 UYVY buffer filled with black (U=128, Y=16, V=128, Y=16)
        var frameSize = 1920 * 1080 * 2;
        _blackFrame = new byte[frameSize];
        for (int i = 0; i < frameSize; i += 4)
        {
            _blackFrame[i] = 0x80;     // U = 128
            _blackFrame[i + 1] = 0x10; // Y0 = 16
            _blackFrame[i + 2] = 0x80; // V = 128
            _blackFrame[i + 3] = 0x10; // Y1 = 16
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
        var frameData = _blackFrame!;
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
