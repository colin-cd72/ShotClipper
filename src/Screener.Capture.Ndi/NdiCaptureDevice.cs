using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Capture.Ndi.Interop;

namespace Screener.Capture.Ndi;

/// <summary>
/// Represents a single NDI source as a capture device.
/// Creates an NDI receiver to capture video/audio frames from the source in UYVY format.
/// </summary>
public sealed class NdiCaptureDevice : ICaptureDevice
{
    private readonly ILogger _logger;
    private readonly string _sourceName;
    private readonly string _sourceUrl;
    private readonly List<VideoMode> _supportedModes;
    private readonly List<VideoConnector> _availableConnectors = [VideoConnector.NDI];

    private IntPtr _recvInstance = IntPtr.Zero;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private DeviceStatus _status = DeviceStatus.Idle;
    private VideoMode? _currentMode;
    private long _frameCount;
    private bool _disposed;

    // Pooled event buffers to reduce GC pressure (same pattern as DeckLinkCaptureDevice)
    private const int EventBufferPoolSize = 3;
    private byte[][]? _eventBufferPool;
    private int _eventBufferIndex;

    public string DeviceId { get; }
    public string DisplayName { get; }
    public DeviceStatus Status => _status;
    public VideoMode? CurrentVideoMode => _currentMode;
    public IReadOnlyList<VideoMode> SupportedVideoModes => _supportedModes;
    public IReadOnlyList<VideoConnector> AvailableConnectors => _availableConnectors;

    public VideoConnector SelectedConnector
    {
        get => VideoConnector.NDI;
        set
        {
            // NDI devices only support the NDI connector
            if (value != VideoConnector.NDI)
            {
                _logger.LogWarning("NDI devices only support VideoConnector.NDI, ignoring {Connector}", value);
            }
        }
    }

    public event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;
    public event EventHandler<AudioSamplesEventArgs>? AudioSamplesReceived;
    public event EventHandler<DeviceStatusChangedEventArgs>? StatusChanged;

    public NdiCaptureDevice(string deviceId, string sourceName, string sourceUrl, ILogger logger)
    {
        DeviceId = deviceId;
        DisplayName = $"{sourceName} (NDI)";
        _sourceName = sourceName;
        _sourceUrl = sourceUrl;
        _logger = logger;

        // Advertise a generic 1080p60 UYVY mode; actual resolution comes from the first received frame
        _supportedModes =
        [
            new VideoMode(1920, 1080, FrameRate.Fps60, PixelFormat.UYVY, false, "1080p60 (NDI Auto)")
        ];
    }

    public Task<bool> StartCaptureAsync(VideoMode mode, CancellationToken ct = default)
    {
        if (_status == DeviceStatus.Capturing)
        {
            _logger.LogWarning("NDI capture already in progress on {Device}", DisplayName);
            return Task.FromResult(false);
        }

        _logger.LogInformation("Starting NDI capture from {Source}", _sourceName);

        try
        {
            SetStatus(DeviceStatus.Initializing);
            _currentMode = mode;
            _frameCount = 0;

            // Marshal the source name and URL to native memory
            var namePtr = Marshal.StringToHGlobalAnsi(_sourceName);
            var urlPtr = Marshal.StringToHGlobalAnsi(_sourceUrl);
            var recvNamePtr = Marshal.StringToHGlobalAnsi($"Screener NDI Recv ({_sourceName})");

            try
            {
                var source = new NDIlib_source_t
                {
                    p_ndi_name = namePtr,
                    p_url_address = urlPtr
                };

                var recvSettings = new NDIlib_recv_create_v3_t
                {
                    source_to_connect_to = source,
                    color_format = NdiConstants.NDIlib_recv_color_format_UYVY_BGRA,
                    bandwidth = NdiConstants.NDIlib_recv_bandwidth_highest,
                    allow_video_fields = true,
                    p_ndi_recv_name = recvNamePtr
                };

                _recvInstance = NdiInterop.NDIlib_recv_create_v3(ref recvSettings);
            }
            finally
            {
                Marshal.FreeHGlobal(namePtr);
                Marshal.FreeHGlobal(urlPtr);
                Marshal.FreeHGlobal(recvNamePtr);
            }

            if (_recvInstance == IntPtr.Zero)
            {
                _logger.LogError("Failed to create NDI receiver for {Source}", _sourceName);
                SetStatus(DeviceStatus.Error);
                return Task.FromResult(false);
            }

            // Start the background receive loop
            _captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _captureTask = Task.Run(() => ReceiveLoopAsync(_captureCts.Token), _captureCts.Token);

            SetStatus(DeviceStatus.Capturing);
            _logger.LogInformation("NDI capture started from {Source}", _sourceName);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start NDI capture from {Source}", _sourceName);
            SetStatus(DeviceStatus.Error);
            return Task.FromResult(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("NDI receive loop started for {Source}", _sourceName);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frameType = NdiInterop.NDIlib_recv_capture_v3(
                    _recvInstance,
                    out var videoFrame,
                    out var audioFrame,
                    IntPtr.Zero,
                    100);

                switch (frameType)
                {
                    case NdiConstants.NDIlib_frame_type_video:
                        ProcessVideoFrame(ref videoFrame);
                        NdiInterop.NDIlib_recv_free_video_v2(_recvInstance, ref videoFrame);
                        break;

                    case NdiConstants.NDIlib_frame_type_audio:
                        ProcessAudioFrame(ref audioFrame);
                        NdiInterop.NDIlib_recv_free_audio_v2(_recvInstance, ref audioFrame);
                        break;

                    case NdiConstants.NDIlib_frame_type_none:
                        // No frame within timeout, yield to avoid busy-waiting
                        await Task.Delay(1, ct);
                        break;

                    default:
                        // Other frame types (metadata, status change, etc.) - ignore
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping capture
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NDI receive loop error for {Source}", _sourceName);
            SetStatus(DeviceStatus.Error);
        }

        _logger.LogDebug("NDI receive loop ended for {Source}", _sourceName);
    }

    private void ProcessVideoFrame(ref NDIlib_video_frame_v2_t frame)
    {
        if (frame.p_data == IntPtr.Zero || frame.xres <= 0 || frame.yres <= 0)
            return;

        _frameCount++;

        var width = frame.xres;
        var height = frame.yres;
        var isUyvy = frame.FourCC == NdiConstants.NDIlib_FourCC_type_UYVY;
        var pixelFormat = isUyvy ? PixelFormat.UYVY : PixelFormat.BGRA;

        // Compute stride: use provided stride, or calculate from format
        var stride = frame.line_stride_in_bytes;
        if (stride <= 0)
        {
            stride = isUyvy ? width * 2 : width * 4;
        }

        var frameSize = stride * height;

        // Update the current mode from the actual received frame on first frame or resolution change
        if (_frameCount == 1 || _currentMode == null ||
            _currentMode.Width != width || _currentMode.Height != height)
        {
            var frameRate = frame.frame_rate_D > 0
                ? ConvertToFrameRate(frame.frame_rate_N, frame.frame_rate_D)
                : FrameRate.Fps60;

            _currentMode = new VideoMode(
                width,
                height,
                frameRate,
                pixelFormat,
                false,
                $"{width}x{height}@{frameRate} (NDI)");

            _logger.LogInformation(
                "NDI frame format detected: {Width}x{Height} @ {FrameRate}, FourCC=0x{FourCC:X8}, stride={Stride}",
                width, height, frameRate, frame.FourCC, stride);
        }

        // Log periodically
        if (_frameCount <= 5 || _frameCount % 300 == 0)
        {
            _logger.LogInformation("NDI video frame {Count}: {Width}x{Height}, stride={Stride}, FourCC=0x{FourCC:X8}",
                _frameCount, width, height, stride, frame.FourCC);
        }

        // Allocate or reuse pooled event buffer
        if (_eventBufferPool == null || _eventBufferPool[0].Length != frameSize)
        {
            _eventBufferPool = new byte[EventBufferPoolSize][];
            for (int i = 0; i < EventBufferPoolSize; i++)
            {
                _eventBufferPool[i] = new byte[frameSize];
            }
            _logger.LogDebug("Allocated NDI event buffer pool: {Slots} x {Size} bytes", EventBufferPoolSize, frameSize);
        }

        var buffer = _eventBufferPool[_eventBufferIndex];
        _eventBufferIndex = (_eventBufferIndex + 1) % EventBufferPoolSize;

        // Copy frame data from native memory to managed buffer
        Marshal.Copy(frame.p_data, buffer, 0, frameSize);

        var timestamp = frame.timecode > 0
            ? TimeSpan.FromTicks(frame.timecode)
            : TimeSpan.FromSeconds(_frameCount / (_currentMode.FrameRate.Value > 0 ? _currentMode.FrameRate.Value : 60.0));

        VideoFrameReceived?.Invoke(this, new VideoFrameEventArgs
        {
            FrameData = buffer.AsMemory(),
            Mode = _currentMode,
            Timestamp = timestamp,
            FrameNumber = _frameCount
        });
    }

    private void ProcessAudioFrame(ref NDIlib_audio_frame_v2_t frame)
    {
        if (frame.p_data == IntPtr.Zero || frame.no_samples <= 0 || frame.no_channels <= 0)
            return;

        var channels = frame.no_channels;
        var samples = frame.no_samples;
        var sampleRate = frame.sample_rate;

        // NDI audio is 32-bit float, planar layout
        // channel_stride_in_bytes is the distance between the start of each channel's data
        var channelStride = frame.channel_stride_in_bytes;
        if (channelStride <= 0)
        {
            channelStride = samples * sizeof(float);
        }

        var totalBytes = channels * channelStride;
        var audioBuffer = new byte[totalBytes];

        Marshal.Copy(frame.p_data, audioBuffer, 0, totalBytes);

        var timestamp = frame.timecode > 0
            ? TimeSpan.FromTicks(frame.timecode)
            : TimeSpan.Zero;

        AudioSamplesReceived?.Invoke(this, new AudioSamplesEventArgs
        {
            SampleData = audioBuffer.AsMemory(),
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = 32, // NDI audio is 32-bit float
            Timestamp = timestamp
        });
    }

    private static FrameRate ConvertToFrameRate(int numerator, int denominator)
    {
        if (denominator <= 0) return FrameRate.Fps60;

        var fps = (double)numerator / denominator;

        return fps switch
        {
            >= 59.93 and < 59.95 => FrameRate.Fps59_94,
            >= 59.99 and <= 60.01 => FrameRate.Fps60,
            >= 49.99 and <= 50.01 => FrameRate.Fps50,
            >= 29.96 and < 29.98 => FrameRate.Fps29_97,
            >= 29.99 and <= 30.01 => FrameRate.Fps30,
            >= 24.99 and <= 25.01 => FrameRate.Fps25,
            >= 23.97 and < 23.99 => FrameRate.Fps23_976,
            >= 23.99 and <= 24.01 => FrameRate.Fps24,
            _ => new FrameRate(numerator, denominator)
        };
    }

    public async Task StopCaptureAsync()
    {
        if (_status != DeviceStatus.Capturing)
            return;

        _logger.LogInformation("Stopping NDI capture from {Source}", _sourceName);

        // Signal cancellation and wait for the receive loop to exit
        _captureCts?.Cancel();
        if (_captureTask != null)
        {
            try
            {
                await _captureTask;
            }
            catch (OperationCanceledException) { }
        }

        // Destroy the receiver
        if (_recvInstance != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_recv_destroy(_recvInstance); } catch { }
            _recvInstance = IntPtr.Zero;
        }

        _currentMode = null;
        SetStatus(DeviceStatus.Idle);
        _logger.LogInformation("NDI capture stopped from {Source}", _sourceName);
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

        _captureCts?.Cancel();
        _captureCts?.Dispose();

        if (_recvInstance != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_recv_destroy(_recvInstance); } catch { }
            _recvInstance = IntPtr.Zero;
        }

        SetStatus(DeviceStatus.Disconnected);
    }
}
