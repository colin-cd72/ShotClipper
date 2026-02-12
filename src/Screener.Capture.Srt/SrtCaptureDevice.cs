using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;

namespace Screener.Capture.Srt;

/// <summary>
/// Capture device that receives video via SRT protocol using FFmpeg as a listener.
/// Launches FFmpeg to listen on the configured SRT port, reads raw UYVY frames from stdout,
/// and fires VideoFrameReceived for each complete frame.
/// </summary>
public sealed class SrtCaptureDevice : ICaptureDevice
{
    private readonly ILogger<SrtCaptureDevice> _logger;
    private readonly SrtInputConfig _config;
    private readonly List<VideoMode> _supportedModes;

    private Process? _ffmpegProcess;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _stderrParseTask;
    private DeviceStatus _status = DeviceStatus.Idle;
    private VideoMode? _currentMode;
    private long _frameCount;
    private bool _disposed;

    // Auto-detected resolution from FFmpeg stderr
    private int _detectedWidth;
    private int _detectedHeight;
    private readonly TaskCompletionSource<bool> _resolutionDetected = new();

    public string DeviceId { get; }
    public string DisplayName { get; }
    public DeviceStatus Status => _status;
    public VideoMode? CurrentVideoMode => _currentMode;
    public IReadOnlyList<VideoMode> SupportedVideoModes => _supportedModes;

    public IReadOnlyList<VideoConnector> AvailableConnectors { get; } =
        new List<VideoConnector> { VideoConnector.SRT };

    public VideoConnector SelectedConnector
    {
        get => VideoConnector.SRT;
        set { } // SRT devices only have one connector
    }

    public event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;
    public event EventHandler<AudioSamplesEventArgs>? AudioSamplesReceived;
    public event EventHandler<DeviceStatusChangedEventArgs>? StatusChanged;

    public SrtCaptureDevice(
        string deviceId,
        string displayName,
        SrtInputConfig config,
        ILogger<SrtCaptureDevice> logger)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        _config = config;
        _logger = logger;

        // Default supported mode; actual resolution is auto-detected from the incoming stream
        _supportedModes = new List<VideoMode>
        {
            new(1920, 1080, FrameRate.Fps30, PixelFormat.YUV422_8bit, false, "1080p30 (SRT)")
        };
    }

    public async Task<bool> StartCaptureAsync(VideoMode mode, CancellationToken ct = default)
    {
        if (_status == DeviceStatus.Capturing)
        {
            _logger.LogWarning("SRT capture already in progress on port {Port}", _config.Port);
            return false;
        }

        _logger.LogInformation("Starting SRT capture on port {Port} (latency={LatencyMs}ms)",
            _config.Port, _config.LatencyMs);

        try
        {
            SetStatus(DeviceStatus.Initializing);
            _currentMode = mode;
            _frameCount = 0;
            _detectedWidth = 0;
            _detectedHeight = 0;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Build FFmpeg arguments for SRT listener mode
            // latency is specified in microseconds in the SRT URL
            var latencyUs = _config.LatencyMs * 1000;
            var srtUrl = $"srt://0.0.0.0:{_config.Port}?mode=listener&latency={latencyUs}";
            var arguments = $"-i \"{srtUrl}\" -f rawvideo -pix_fmt uyvy422 pipe:1";

            _ffmpegProcess = await FfmpegProcessHelper.LaunchAsync(
                arguments,
                redirectStdin: false,
                redirectStdout: true,
                _logger);

            // Start background task to parse stderr for resolution detection
            _stderrParseTask = ParseStderrForResolutionAsync(_cts.Token);

            // Start background task to read raw frames from stdout
            _readTask = ReadFramesAsync(_cts.Token);

            SetStatus(DeviceStatus.Capturing);
            _logger.LogInformation("SRT capture started on port {Port}", _config.Port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SRT capture on port {Port}", _config.Port);
            SetStatus(DeviceStatus.Error);
            return false;
        }
    }

    public async Task StopCaptureAsync()
    {
        if (_status != DeviceStatus.Capturing)
            return;

        _logger.LogInformation("Stopping SRT capture on port {Port}", _config.Port);

        _cts?.Cancel();

        if (_ffmpegProcess != null)
        {
            await FfmpegProcessHelper.StopGracefully(_ffmpegProcess, TimeSpan.FromSeconds(5));
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }

        // Wait for background tasks to finish
        if (_readTask != null)
        {
            try { await _readTask; }
            catch (OperationCanceledException) { }
        }

        if (_stderrParseTask != null)
        {
            try { await _stderrParseTask; }
            catch (OperationCanceledException) { }
        }

        _currentMode = null;
        SetStatus(DeviceStatus.Idle);
    }

    /// <summary>
    /// Monitors FFmpeg stderr to auto-detect the incoming stream resolution.
    /// FFmpeg stderr is already being read line-by-line via BeginErrorReadLine in LaunchAsync,
    /// but we also subscribe to the process events to detect resolution.
    /// </summary>
    private async Task ParseStderrForResolutionAsync(CancellationToken ct)
    {
        if (_ffmpegProcess == null) return;

        // We subscribe to ErrorDataReceived to detect resolution from FFmpeg output
        var tcs = _resolutionDetected;

        void OnStderrLine(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data) || tcs.Task.IsCompleted) return;

            var resolution = FfmpegProcessHelper.ParseResolutionFromStderr(e.Data);
            if (resolution.HasValue)
            {
                _detectedWidth = resolution.Value.Width;
                _detectedHeight = resolution.Value.Height;

                _logger.LogInformation("SRT stream resolution detected: {Width}x{Height}",
                    _detectedWidth, _detectedHeight);

                // Update current mode with detected resolution
                if (_currentMode != null)
                {
                    _currentMode = new VideoMode(
                        _detectedWidth,
                        _detectedHeight,
                        _currentMode.FrameRate,
                        PixelFormat.YUV422_8bit,
                        false,
                        $"{_detectedHeight}p (SRT)");
                }

                tcs.TrySetResult(true);
            }
        }

        _ffmpegProcess.ErrorDataReceived += OnStderrLine;

        try
        {
            // Wait for resolution detection or cancellation (timeout after 30s)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await Task.Delay(Timeout.Infinite, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout - use default resolution
                if (!tcs.Task.IsCompleted)
                {
                    _logger.LogWarning("Resolution detection timed out, using default 1920x1080");
                    _detectedWidth = 1920;
                    _detectedHeight = 1080;
                    tcs.TrySetResult(false);
                }
            }
        }
        finally
        {
            _ffmpegProcess.ErrorDataReceived -= OnStderrLine;
        }
    }

    /// <summary>
    /// Reads fixed-size raw UYVY frames from FFmpeg stdout.
    /// Each frame is width * height * 2 bytes (UYVY = 2 bytes per pixel).
    /// </summary>
    private async Task ReadFramesAsync(CancellationToken ct)
    {
        if (_ffmpegProcess == null) return;

        var stdout = _ffmpegProcess.StandardOutput.BaseStream;

        try
        {
            // Wait for resolution to be detected before reading frames
            await _resolutionDetected.Task;

            var width = _detectedWidth > 0 ? _detectedWidth : 1920;
            var height = _detectedHeight > 0 ? _detectedHeight : 1080;
            var frameSize = width * height * 2; // UYVY: 2 bytes per pixel

            _logger.LogInformation("SRT frame reader started: {Width}x{Height}, frameSize={FrameSize}",
                width, height, frameSize);

            var frameBuffer = new byte[frameSize];
            var frameRate = _currentMode?.FrameRate ?? FrameRate.Fps30;

            while (!ct.IsCancellationRequested)
            {
                // Read exactly one frame worth of data
                var bytesRead = 0;
                while (bytesRead < frameSize)
                {
                    var read = await stdout.ReadAsync(
                        frameBuffer.AsMemory(bytesRead, frameSize - bytesRead), ct);

                    if (read == 0)
                    {
                        // FFmpeg process ended (stream closed or disconnected)
                        _logger.LogWarning("SRT stream ended (FFmpeg stdout closed)");
                        return;
                    }

                    bytesRead += read;
                }

                _frameCount++;
                var timestamp = TimeSpan.FromSeconds(_frameCount / frameRate.Value);

                var mode = new VideoMode(
                    width,
                    height,
                    frameRate,
                    PixelFormat.YUV422_8bit,
                    false,
                    $"{height}p (SRT)");

                VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
                {
                    FrameData = frameBuffer.AsMemory(),
                    Mode = mode,
                    Timestamp = timestamp,
                    FrameNumber = _frameCount
                });

                if (_frameCount <= 5 || _frameCount % 300 == 0)
                {
                    _logger.LogInformation("SRT frame {FrameCount}: {Width}x{Height}, {Bytes} bytes",
                        _frameCount, width, height, frameSize);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SRT frame reader cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading SRT frames");
            SetStatus(DeviceStatus.Error);
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

        if (_ffmpegProcess != null)
        {
            if (!_ffmpegProcess.HasExited)
            {
                try { _ffmpegProcess.Kill(); }
                catch { }
            }
            _ffmpegProcess.Dispose();
        }

        SetStatus(DeviceStatus.Disconnected);
    }
}
