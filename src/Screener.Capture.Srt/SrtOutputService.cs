using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Output;

namespace Screener.Capture.Srt;

/// <summary>
/// SRT output service that sends raw UYVY video frames via FFmpeg to an SRT destination.
/// Supports both caller mode (connect to remote) and listener mode (wait for connections).
/// </summary>
public sealed class SrtOutputService : IOutputService
{
    private readonly ILogger<SrtOutputService> _logger;

    private Process? _ffmpegProcess;
    private Stream? _ffmpegStdin;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private OutputState _state = OutputState.Stopped;
    private OutputConfiguration? _currentConfig;

    public string OutputId => "srt-output";
    public string DisplayName => "SRT Output";
    public OutputState State => _state;
    public OutputConfiguration? CurrentConfig => _currentConfig;

    public event EventHandler<OutputStateChangedEventArgs>? StateChanged;

    public SrtOutputService(ILogger<SrtOutputService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(OutputConfiguration config, CancellationToken ct = default)
    {
        if (_state != OutputState.Stopped)
            throw new InvalidOperationException($"Cannot start SRT output in state {_state}");

        SetState(OutputState.Starting);
        _currentConfig = config;

        try
        {
            // Extract parameters from configuration
            var parameters = config.Parameters ?? new Dictionary<string, string>();
            var mode = parameters.GetValueOrDefault("mode", "caller");
            var address = parameters.GetValueOrDefault("address", "127.0.0.1");
            var port = parameters.GetValueOrDefault("port", "9000");
            var latencyMs = parameters.GetValueOrDefault("latency", "120");
            var bitrateKbps = parameters.GetValueOrDefault("bitrate", "5000");

            // Build SRT URL based on mode
            var latencyUs = int.Parse(latencyMs) * 1000;
            string srtUrl;

            if (mode.Equals("listener", StringComparison.OrdinalIgnoreCase))
            {
                srtUrl = $"srt://0.0.0.0:{port}?mode=listener&latency={latencyUs}";
            }
            else
            {
                srtUrl = $"srt://{address}:{port}?mode=caller&latency={latencyUs}";
            }

            var fps = config.FrameRate > 0 ? config.FrameRate : 30.0;

            // Build FFmpeg arguments:
            // Input: raw UYVY from stdin
            // Output: H.264 ultrafast/zerolatency via MPEG-TS over SRT
            var arguments =
                $"-f rawvideo -video_size {config.Width}x{config.Height} " +
                $"-pixel_format uyvy422 -framerate {fps:F2} -i pipe:0 " +
                $"-c:v libx264 -preset ultrafast -tune zerolatency " +
                $"-b:v {bitrateKbps}k " +
                $"-f mpegts \"{srtUrl}\"";

            _logger.LogInformation("Starting SRT output: mode={Mode}, url={Url}", mode, srtUrl);

            _cts = new CancellationTokenSource();

            _ffmpegProcess = await FfmpegProcessHelper.LaunchAsync(
                arguments,
                redirectStdin: true,
                redirectStdout: false,
                _logger);

            _ffmpegStdin = _ffmpegProcess.StandardInput.BaseStream;

            // Monitor FFmpeg process health
            _monitorTask = MonitorProcessAsync(_cts.Token);

            SetState(OutputState.Running);
            _logger.LogInformation("SRT output started: {Width}x{Height} @ {Fps}fps -> {Url}",
                config.Width, config.Height, fps, srtUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SRT output");
            SetState(OutputState.Error);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_state != OutputState.Running && _state != OutputState.Error)
            return;

        SetState(OutputState.Stopping);
        _logger.LogInformation("Stopping SRT output...");

        try
        {
            // Close stdin to signal end of data
            if (_ffmpegStdin != null)
            {
                try
                {
                    await _ffmpegStdin.FlushAsync(ct);
                    _ffmpegStdin.Close();
                }
                catch
                {
                    // stdin may already be closed
                }
                _ffmpegStdin = null;
            }

            // Stop FFmpeg gracefully
            if (_ffmpegProcess != null)
            {
                await FfmpegProcessHelper.StopGracefully(_ffmpegProcess, TimeSpan.FromSeconds(10));
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }

            // Cancel monitor task
            _cts?.Cancel();
            if (_monitorTask != null)
            {
                try { await _monitorTask; }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping SRT output");
        }
        finally
        {
            _currentConfig = null;
            SetState(OutputState.Stopped);
            _logger.LogInformation("SRT output stopped");
        }
    }

    public async Task PushFrameAsync(
        ReadOnlyMemory<byte> frameData,
        VideoMode mode,
        TimeSpan timestamp,
        CancellationToken ct = default)
    {
        if (_state != OutputState.Running || _ffmpegStdin == null)
            return;

        try
        {
            await _ffmpegStdin.WriteAsync(frameData, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push frame to SRT output");
        }
    }

    public Task PushAudioAsync(
        ReadOnlyMemory<byte> audioData,
        int sampleRate,
        int channels,
        int bitsPerSample,
        CancellationToken ct = default)
    {
        // Audio not supported in initial SRT output implementation
        return Task.CompletedTask;
    }

    private async Task MonitorProcessAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ffmpegProcess?.HasExited == false)
            {
                await Task.Delay(2000, ct);
            }

            if (_ffmpegProcess?.HasExited == true && _state == OutputState.Running)
            {
                var exitCode = _ffmpegProcess.ExitCode;
                _logger.LogWarning("FFmpeg process exited unexpectedly with code {ExitCode}", exitCode);
                SetState(OutputState.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    private void SetState(OutputState newState)
    {
        var oldState = _state;
        _state = newState;

        if (oldState != newState)
        {
            StateChanged?.Invoke(this, new OutputStateChangedEventArgs
            {
                OldState = oldState,
                NewState = newState
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_ffmpegStdin != null)
        {
            try { await _ffmpegStdin.DisposeAsync(); }
            catch { }
        }

        if (_ffmpegProcess != null)
        {
            if (!_ffmpegProcess.HasExited)
            {
                try { _ffmpegProcess.Kill(); }
                catch { }
            }
            _ffmpegProcess.Dispose();
        }

        _cts?.Dispose();
    }
}
