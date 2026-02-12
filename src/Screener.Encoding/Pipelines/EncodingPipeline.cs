using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Encoding;
using Screener.Encoding.Codecs;

namespace Screener.Encoding.Pipelines;

/// <summary>
/// FFmpeg-based encoding pipeline with hardware acceleration support.
/// </summary>
public sealed class EncodingPipeline : IEncodingPipeline
{
    private readonly ILogger<EncodingPipeline> _logger;
    private readonly HardwareAccelerator _hwAccel;

    private EncodingConfiguration? _config;
    private Process? _ffmpegProcess;
    private Stream? _videoInputStream;
    private Stream? _audioInputStream;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;

    private EncodingState _state = EncodingState.Idle;
    private EncodingPreset _currentPreset = EncodingPreset.Medium;
    private readonly EncodingStatistics _statistics = new();
    private long _framesEncoded;
    private long _bytesWritten;
    private DateTime _startTime;
    private int _droppedFrames;

    public EncodingState State => _state;
    public EncodingPreset CurrentPreset => _currentPreset;
    public EncodingStatistics Statistics => _statistics with
    {
        FramesEncoded = _framesEncoded,
        BytesWritten = _bytesWritten,
        Duration = _startTime != default ? DateTime.UtcNow - _startTime : TimeSpan.Zero,
        DroppedFrames = _droppedFrames
    };

    public event EventHandler<EncodingProgressEventArgs>? Progress;
    public event EventHandler<EncodingErrorEventArgs>? Error;

    public EncodingPipeline(ILogger<EncodingPipeline> logger, HardwareAccelerator hwAccel)
    {
        _logger = logger;
        _hwAccel = hwAccel;
    }

    public async Task InitializeAsync(EncodingConfiguration config, CancellationToken ct = default)
    {
        if (_state != EncodingState.Idle)
            throw new InvalidOperationException($"Cannot initialize in state {_state}");

        _config = config;
        _currentPreset = config.Preset;
        _state = EncodingState.Initializing;

        try
        {
            var ffmpegPath = FindFfmpegPath();
            var arguments = BuildFfmpegArguments(config);

            _logger.LogInformation("Starting FFmpeg with arguments: {Args}", arguments);

            _ffmpegProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            _ffmpegProcess.ErrorDataReceived += OnFfmpegErrorData;
            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();

            _videoInputStream = _ffmpegProcess.StandardInput.BaseStream;
            _startTime = DateTime.UtcNow;

            _cts = new CancellationTokenSource();
            _monitorTask = MonitorFfmpegAsync(_cts.Token);

            _state = EncodingState.Encoding;
            _logger.LogInformation("Encoding started: {Output}", config.OutputPath);
        }
        catch (Exception ex)
        {
            _state = EncodingState.Error;
            _logger.LogError(ex, "Failed to initialize encoding pipeline");
            Error?.Invoke(this, new EncodingErrorEventArgs
            {
                Message = ex.Message,
                Exception = ex,
                IsFatal = true
            });
            throw;
        }
    }

    public async Task<bool> WriteVideoFrameAsync(ReadOnlyMemory<byte> frame, TimeSpan pts, CancellationToken ct = default)
    {
        if (_state != EncodingState.Encoding || _videoInputStream == null)
            return false;

        try
        {
            await _videoInputStream.WriteAsync(frame, ct);
            _framesEncoded++;

            if (_framesEncoded % 30 == 0)
            {
                UpdateProgress();
            }

            return true;
        }
        catch (Exception ex)
        {
            _droppedFrames++;
            _logger.LogWarning(ex, "Failed to write video frame {FrameNumber}", _framesEncoded);
            return false;
        }
    }

    public async Task<bool> WriteAudioSamplesAsync(ReadOnlyMemory<byte> samples, TimeSpan pts, CancellationToken ct = default)
    {
        // In a real implementation with separate audio pipe
        // For now, we'll assume audio is interleaved or handled differently
        return true;
    }

    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        if (_state != EncodingState.Encoding)
            return;

        _state = EncodingState.Finalizing;
        _logger.LogInformation("Finalizing encoding...");

        try
        {
            // Close input stream to signal end of data
            if (_videoInputStream != null)
            {
                await _videoInputStream.FlushAsync(ct);
                _videoInputStream.Close();
            }

            // Wait for FFmpeg to finish
            if (_ffmpegProcess != null)
            {
                var completed = await Task.Run(() => _ffmpegProcess.WaitForExit(30000), ct);

                if (!completed)
                {
                    _logger.LogWarning("FFmpeg did not exit gracefully, killing process");
                    _ffmpegProcess.Kill();
                }
            }

            _cts?.Cancel();
            if (_monitorTask != null)
            {
                try { await _monitorTask; } catch { }
            }

            // Get final file size
            if (File.Exists(_config?.OutputPath))
            {
                _bytesWritten = new FileInfo(_config!.OutputPath).Length;
            }

            _state = EncodingState.Completed;
            _logger.LogInformation("Encoding completed: {Frames} frames, {Size} bytes",
                _framesEncoded, _bytesWritten);
        }
        catch (Exception ex)
        {
            _state = EncodingState.Error;
            _logger.LogError(ex, "Error finalizing encoding");
            throw;
        }
    }

    private string BuildFfmpegArguments(EncodingConfiguration config)
    {
        var mode = config.VideoMode;
        var preset = config.Preset;
        var encoder = _hwAccel.GetEncoderName(preset.VideoCodec, config.HwAccel);

        var args = new List<string>
        {
            "-y", // Overwrite output
            "-f rawvideo",
            $"-pix_fmt uyvy422", // DeckLink format
            $"-s {mode.Width}x{mode.Height}",
            $"-r {mode.FrameRate.Value:F2}",
            "-i pipe:0", // Video from stdin
        };

        // Video encoding settings
        // Convert 4:2:2 input to 4:2:0 for h264/h265 compatibility (high profile doesn't support 4:2:2)
        if (preset.VideoCodec == VideoCodec.H264 || preset.VideoCodec == VideoCodec.H265)
        {
            args.Add("-pix_fmt yuv420p");
        }

        args.Add($"-c:v {encoder}");

        if (preset.VideoCodec == VideoCodec.H264 || preset.VideoCodec == VideoCodec.H265)
        {
            if (encoder.Contains("nvenc"))
            {
                args.Add($"-preset p4"); // NVENC preset
                args.Add($"-rc vbr");
                args.Add($"-b:v {preset.VideoBitrateMbps}M");
                args.Add($"-maxrate {preset.VideoBitrateMbps * 1.5}M");
                args.Add($"-bufsize {preset.VideoBitrateMbps * 2}M");
            }
            else if (encoder.Contains("qsv"))
            {
                args.Add($"-preset medium");
                args.Add($"-b:v {preset.VideoBitrateMbps}M");
            }
            else
            {
                // Software encoding
                args.Add($"-preset medium");
                args.Add($"-crf {preset.CrfValue}");
                args.Add($"-maxrate {preset.VideoBitrateMbps}M");
                args.Add($"-bufsize {preset.VideoBitrateMbps * 2}M");
            }

            args.Add($"-profile:v {preset.Profile}");
        }

        // Audio encoding
        args.Add($"-c:a {(preset.AudioCodec == AudioCodec.AAC ? "aac" : "pcm_s24le")}");
        args.Add($"-b:a {preset.AudioBitrateKbps}k");

        // Output format
        if (config.UseFragmentedMp4)
        {
            args.Add("-movflags +frag_keyframe+empty_moov+default_base_moof");
        }
        else
        {
            args.Add("-movflags +faststart");
        }

        args.Add($"\"{config.OutputPath}\"");

        return string.Join(" ", args);
    }

    private void OnFfmpegErrorData(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;

        // Parse FFmpeg progress output
        if (e.Data.Contains("frame="))
        {
            _logger.LogDebug("FFmpeg: {Data}", e.Data);
        }
        else if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("FFmpeg error: {Data}", e.Data);
            Error?.Invoke(this, new EncodingErrorEventArgs
            {
                Message = e.Data,
                IsFatal = false
            });
        }
    }

    private async Task MonitorFfmpegAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ffmpegProcess?.HasExited == false)
            {
                await Task.Delay(1000, ct);

                if (File.Exists(_config?.OutputPath))
                {
                    _bytesWritten = new FileInfo(_config!.OutputPath).Length;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateProgress()
    {
        var duration = DateTime.UtcNow - _startTime;
        var bitrate = duration.TotalSeconds > 0 ? _bytesWritten * 8 / duration.TotalSeconds / 1_000_000 : 0;

        Progress?.Invoke(this, new EncodingProgressEventArgs
        {
            FrameNumber = _framesEncoded,
            EncodedDuration = TimeSpan.FromSeconds(_framesEncoded / _config!.VideoMode.FrameRate.Value),
            CurrentBitrateMbps = bitrate,
            FileSizeBytes = _bytesWritten
        });
    }

    private static string FindFfmpegPath()
    {
        // Check common locations
        var paths = new[]
        {
            "ffmpeg",
            "ffmpeg.exe",
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe"
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
                return path;

            // Check if in PATH
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                        return path;
                }
            }
            catch
            {
            }
        }

        throw new FileNotFoundException("FFmpeg not found. Please install FFmpeg and add it to PATH.");
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_videoInputStream != null)
        {
            try
            {
                await _videoInputStream.DisposeAsync();
            }
            catch { }
        }

        if (_ffmpegProcess != null)
        {
            if (!_ffmpegProcess.HasExited)
            {
                try
                {
                    _ffmpegProcess.Kill();
                }
                catch { }
            }
            _ffmpegProcess.Dispose();
        }

        _cts?.Dispose();
    }
}
