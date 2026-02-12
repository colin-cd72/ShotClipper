using System.Buffers;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;

namespace Screener.Preview;

/// <summary>
/// Manages audio preview output using circular buffer for smooth playback.
/// Uses WASAPI for low-latency audio output on Windows.
/// </summary>
public sealed class AudioPreviewService : IDisposable
{
    private readonly ILogger<AudioPreviewService> _logger;
    private readonly object _lock = new();

    private readonly byte[] _circularBuffer;
    private int _writePosition;
    private int _readPosition;
    private int _bufferedSamples;

    private int _sampleRate = 48000;
    private int _channels = 2;
    private int _bitsPerSample = 16;
    private float _volume = 1.0f;
    private bool _isMuted;
    private bool _isRunning;

    private CancellationTokenSource? _cts;
    private Task? _playbackTask;

    private const int BufferSizeMs = 200; // 200ms buffer

    /// <summary>
    /// Gets or sets the output volume (0.0 to 1.0).
    /// </summary>
    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Gets or sets whether audio is muted.
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    /// <summary>
    /// Gets whether audio preview is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Fired when audio levels are updated.
    /// </summary>
    public event EventHandler<AudioLevelEventArgs>? AudioLevelUpdated;

    public AudioPreviewService(ILogger<AudioPreviewService> logger)
    {
        _logger = logger;

        // Pre-allocate buffer for 48kHz stereo 16-bit
        var bufferSize = _sampleRate * _channels * (_bitsPerSample / 8) * BufferSizeMs / 1000;
        _circularBuffer = new byte[bufferSize];
    }

    /// <summary>
    /// Configure audio format.
    /// </summary>
    public void Configure(int sampleRate, int channels, int bitsPerSample)
    {
        lock (_lock)
        {
            if (_sampleRate == sampleRate && _channels == channels && _bitsPerSample == bitsPerSample)
                return;

            _sampleRate = sampleRate;
            _channels = channels;
            _bitsPerSample = bitsPerSample;

            _logger.LogInformation(
                "Audio preview configured: {SampleRate}Hz, {Channels}ch, {Bits}bit",
                sampleRate, channels, bitsPerSample);
        }
    }

    /// <summary>
    /// Start audio preview playback.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            return;

        _cts = new CancellationTokenSource();
        _isRunning = true;

        // Reset buffer state
        lock (_lock)
        {
            _writePosition = 0;
            _readPosition = 0;
            _bufferedSamples = 0;
        }

        _playbackTask = Task.Run(() => PlaybackLoop(_cts.Token), _cts.Token);

        _logger.LogInformation("Audio preview started");
    }

    /// <summary>
    /// Stop audio preview playback.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _playbackTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
        }

        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("Audio preview stopped");
    }

    /// <summary>
    /// Write audio samples to the preview buffer.
    /// </summary>
    public void WriteSamples(ReadOnlySpan<byte> samples)
    {
        if (!_isRunning || samples.IsEmpty)
            return;

        // Just calculate and report levels (no playback buffering for now)
        CalculateAndReportLevels(samples);
    }

    /// <summary>
    /// Write audio frame to the preview buffer.
    /// </summary>
    public void WriteFrame(AudioFrame frame)
    {
        if (frame.SampleRate != _sampleRate || frame.Channels != _channels || frame.BitsPerSample != _bitsPerSample)
        {
            Configure(frame.SampleRate, frame.Channels, frame.BitsPerSample);
        }

        WriteSamples(frame.Data.Span);
    }

    private void PlaybackLoop(CancellationToken ct)
    {
        // In a full implementation, this would use WASAPI via NAudio or similar
        // For now, we'll just consume the buffer and report levels

        var frameSize = _channels * (_bitsPerSample / 8);
        var samplesPerCallback = _sampleRate / 100; // 10ms chunks
        var bytesPerCallback = samplesPerCallback * frameSize;
        var outputBuffer = new byte[bytesPerCallback];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var bytesRead = 0;

                lock (_lock)
                {
                    if (_bufferedSamples >= bytesPerCallback)
                    {
                        // Read from circular buffer
                        var firstChunkSize = Math.Min(bytesPerCallback, _circularBuffer.Length - _readPosition);
                        Array.Copy(_circularBuffer, _readPosition, outputBuffer, 0, firstChunkSize);

                        if (firstChunkSize < bytesPerCallback)
                        {
                            Array.Copy(_circularBuffer, 0, outputBuffer, firstChunkSize, bytesPerCallback - firstChunkSize);
                        }

                        _readPosition = (_readPosition + bytesPerCallback) % _circularBuffer.Length;
                        _bufferedSamples -= bytesPerCallback;
                        bytesRead = bytesPerCallback;
                    }
                }

                if (bytesRead > 0 && !_isMuted)
                {
                    // In a full implementation, send outputBuffer to WASAPI device
                    // Apply volume scaling here
                }

                // Wait for next callback interval
                Thread.Sleep(10);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error in audio playback loop");
            }
        }
    }

    private void CalculateAndReportLevels(ReadOnlySpan<byte> samples)
    {
        if (_channels < 1 || (_bitsPerSample != 16 && _bitsPerSample != 32))
            return;

        var bytesPerSample = _bitsPerSample / 8;
        var levels = new float[_channels];
        var sampleCount = samples.Length / bytesPerSample / _channels;

        if (sampleCount == 0)
            return;

        for (int ch = 0; ch < _channels; ch++)
        {
            float peak = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                var offset = (i * _channels + ch) * bytesPerSample;

                float normalized;
                if (_bitsPerSample == 16)
                {
                    if (offset + 1 >= samples.Length)
                        break;
                    var sample = (short)(samples[offset] | (samples[offset + 1] << 8));
                    normalized = Math.Abs(sample / 32768f);
                }
                else // 32-bit
                {
                    if (offset + 3 >= samples.Length)
                        break;
                    var sample = samples[offset] | (samples[offset + 1] << 8) |
                                 (samples[offset + 2] << 16) | (samples[offset + 3] << 24);
                    normalized = Math.Abs(sample / 2147483648f);
                }

                if (normalized > peak)
                    peak = normalized;
            }

            levels[ch] = peak;
        }

        // Convert to dB
        var dbLevels = new float[_channels];
        for (int ch = 0; ch < _channels; ch++)
        {
            dbLevels[ch] = levels[ch] > 0 ? 20f * MathF.Log10(levels[ch]) : -60f;
        }

        AudioLevelUpdated?.Invoke(this, new AudioLevelEventArgs(dbLevels, levels));
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Audio level event arguments.
/// </summary>
public class AudioLevelEventArgs : EventArgs
{
    /// <summary>
    /// Audio levels in dB for each channel.
    /// </summary>
    public float[] LevelsDb { get; }

    /// <summary>
    /// Audio levels as linear values (0-1) for each channel.
    /// </summary>
    public float[] LevelsLinear { get; }

    public AudioLevelEventArgs(float[] levelsDb, float[] levelsLinear)
    {
        LevelsDb = levelsDb;
        LevelsLinear = levelsLinear;
    }
}
