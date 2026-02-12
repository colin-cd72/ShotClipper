using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Output;
using Screener.Capture.Ndi.Interop;

namespace Screener.Capture.Ndi;

/// <summary>
/// NDI output service that sends video and audio frames over the network using the NDI protocol.
/// Implements <see cref="IOutputService"/> so it can be used alongside other output types (SRT, etc.).
/// </summary>
public sealed class NdiOutputService : IOutputService
{
    private readonly ILogger<NdiOutputService> _logger;
    private readonly NdiRuntime _runtime;

    private IntPtr _sendInstance = IntPtr.Zero;
    private OutputState _state = OutputState.Stopped;
    private OutputConfiguration? _currentConfig;
    private bool _disposed;

    public string OutputId => "ndi-output";
    public string DisplayName => "NDI Output";
    public OutputState State => _state;
    public OutputConfiguration? CurrentConfig => _currentConfig;

    public event EventHandler<OutputStateChangedEventArgs>? StateChanged;

    public NdiOutputService(ILogger<NdiOutputService> logger, NdiRuntime runtime)
    {
        _logger = logger;
        _runtime = runtime;
    }

    public Task StartAsync(OutputConfiguration config, CancellationToken ct = default)
    {
        if (_state == OutputState.Running)
        {
            _logger.LogWarning("NDI output is already running");
            return Task.CompletedTask;
        }

        if (!_runtime.IsAvailable)
        {
            _logger.LogError("Cannot start NDI output: runtime not available ({Status})", _runtime.StatusMessage);
            SetState(OutputState.Error, "NDI runtime not available");
            return Task.CompletedTask;
        }

        if (!_runtime.Initialize())
        {
            _logger.LogError("Cannot start NDI output: initialization failed");
            SetState(OutputState.Error, "NDI initialization failed");
            return Task.CompletedTask;
        }

        SetState(OutputState.Starting);
        _currentConfig = config;

        var sourceName = config.Name ?? "Screener NDI Output";
        var namePtr = Marshal.StringToHGlobalAnsi(sourceName);

        try
        {
            var sendSettings = new NDIlib_send_create_t
            {
                p_ndi_name = namePtr,
                p_groups = IntPtr.Zero,
                clock_video = true,
                clock_audio = true
            };

            _sendInstance = NdiInterop.NDIlib_send_create(ref sendSettings);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }

        if (_sendInstance == IntPtr.Zero)
        {
            _logger.LogError("Failed to create NDI sender for '{SourceName}'", sourceName);
            SetState(OutputState.Error, "Failed to create NDI sender");
            return Task.CompletedTask;
        }

        SetState(OutputState.Running);
        _logger.LogInformation("NDI output started: '{SourceName}' ({Width}x{Height} @ {Fps}fps)",
            sourceName, config.Width, config.Height, config.FrameRate);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (_state != OutputState.Running && _state != OutputState.Error)
            return Task.CompletedTask;

        SetState(OutputState.Stopping);

        if (_sendInstance != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_send_destroy(_sendInstance); } catch { }
            _sendInstance = IntPtr.Zero;
        }

        _currentConfig = null;
        SetState(OutputState.Stopped);
        _logger.LogInformation("NDI output stopped");

        return Task.CompletedTask;
    }

    public Task PushFrameAsync(ReadOnlyMemory<byte> frameData, VideoMode mode, TimeSpan timestamp, CancellationToken ct = default)
    {
        if (_state != OutputState.Running || _sendInstance == IntPtr.Zero)
            return Task.CompletedTask;

        var isUyvy = mode.PixelFormat == PixelFormat.UYVY || mode.PixelFormat == PixelFormat.YUV422_8bit;
        var stride = isUyvy ? mode.Width * 2 : mode.Width * 4;

        // Pin the managed buffer and send via NDI
        unsafe
        {
            fixed (byte* pData = frameData.Span)
            {
                var videoFrame = new NDIlib_video_frame_v2_t
                {
                    xres = mode.Width,
                    yres = mode.Height,
                    FourCC = isUyvy ? NdiConstants.NDIlib_FourCC_type_UYVY : 0x41524742, // BGRA FourCC
                    frame_rate_N = mode.FrameRate.Numerator,
                    frame_rate_D = mode.FrameRate.Denominator,
                    picture_aspect_ratio = 0.0f, // Square pixels
                    frame_format_type = 1, // Progressive
                    timecode = timestamp.Ticks,
                    p_data = (IntPtr)pData,
                    line_stride_in_bytes = stride,
                    p_metadata = IntPtr.Zero,
                    timestamp = 0
                };

                NdiInterop.NDIlib_send_send_video_v2(_sendInstance, ref videoFrame);
            }
        }

        return Task.CompletedTask;
    }

    public Task PushAudioAsync(ReadOnlyMemory<byte> audioData, int sampleRate, int channels, int bitsPerSample, CancellationToken ct = default)
    {
        if (_state != OutputState.Running || _sendInstance == IntPtr.Zero)
            return Task.CompletedTask;

        // NDI expects 32-bit float planar audio
        var sampleCount = audioData.Length / (channels * (bitsPerSample / 8));
        var channelStride = sampleCount * sizeof(float);

        unsafe
        {
            fixed (byte* pData = audioData.Span)
            {
                var audioFrame = new NDIlib_audio_frame_v2_t
                {
                    sample_rate = sampleRate,
                    no_channels = channels,
                    no_samples = sampleCount,
                    timecode = 0, // Let NDI synthesize timecodes
                    p_data = (IntPtr)pData,
                    channel_stride_in_bytes = channelStride,
                    p_metadata = IntPtr.Zero,
                    timestamp = 0
                };

                NdiInterop.NDIlib_send_send_audio_v2(_sendInstance, ref audioFrame);
            }
        }

        return Task.CompletedTask;
    }

    private void SetState(OutputState newState, string? errorMessage = null)
    {
        var oldState = _state;
        _state = newState;

        if (oldState != newState)
        {
            StateChanged?.Invoke(this, new OutputStateChangedEventArgs
            {
                OldState = oldState,
                NewState = newState,
                ErrorMessage = errorMessage
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_state == OutputState.Running)
        {
            await StopAsync();
        }

        if (_sendInstance != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_send_destroy(_sendInstance); } catch { }
            _sendInstance = IntPtr.Zero;
        }
    }
}
