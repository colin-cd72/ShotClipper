using Microsoft.Extensions.Logging;

namespace Screener.Golf.Detection;

/// <summary>
/// Detects golf club impact sounds using RMS energy spike detection against an EMA baseline.
/// Mirrors the SwingDetector pattern but operates on raw PCM audio samples.
/// </summary>
public class AudioSwingDetector
{
    private readonly ILogger _logger;
    private readonly AutoCutConfiguration _config;

    private double _emaBaseline;
    private bool _emaInitialized;

    /// <summary>Last computed RMS level in dB.</summary>
    public double LastRmsDb { get; private set; } = -60.0;

    /// <summary>Current EMA baseline (linear RMS).</summary>
    public double CurrentEma => _emaBaseline;

    /// <summary>Fired when an audio spike is detected.</summary>
    public event EventHandler? SpikeDetected;

    public AudioSwingDetector(ILogger logger, AutoCutConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Process raw PCM audio samples and detect impact transients.
    /// </summary>
    /// <param name="sampleData">Raw PCM sample bytes (interleaved channels).</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Number of audio channels.</param>
    /// <param name="bitsPerSample">Bits per sample (16 or 32).</param>
    /// <returns>True if an audio spike was detected.</returns>
    public bool ProcessAudio(ReadOnlySpan<byte> sampleData, int sampleRate, int channels, int bitsPerSample)
    {
        if (!_config.AudioEnabled)
            return false;

        double rms = ComputeRms(sampleData, channels, bitsPerSample);
        double rmsDb = rms > 0 ? 20.0 * Math.Log10(rms) : -60.0;
        LastRmsDb = rmsDb;

        bool spikeDetected = false;

        // Only check for spikes if above noise floor
        if (rmsDb >= _config.MinimumAudioThresholdDb && _emaInitialized)
        {
            double threshold = _emaBaseline * _config.AudioSpikeMultiplier;
            if (rms > threshold && threshold > 0)
            {
                _logger.LogInformation(
                    "Audio spike detected: RMS={Rms:F4} ({Db:F1}dB), EMA={Ema:F4}, Threshold={Threshold:F4}",
                    rms, rmsDb, _emaBaseline, threshold);
                SpikeDetected?.Invoke(this, EventArgs.Empty);
                spikeDetected = true;
            }
        }

        // Update EMA after threshold check
        if (!_emaInitialized)
        {
            _emaBaseline = rms;
            _emaInitialized = true;
        }
        else
        {
            _emaBaseline = (_config.AudioEmaAlpha * rms) + ((1.0 - _config.AudioEmaAlpha) * _emaBaseline);
        }

        return spikeDetected;
    }

    /// <summary>
    /// Compute the RMS energy of interleaved PCM samples, mixing all channels to mono.
    /// </summary>
    public static double ComputeRms(ReadOnlySpan<byte> sampleData, int channels, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;
        int frameSize = bytesPerSample * channels;

        if (frameSize == 0 || sampleData.Length < frameSize)
            return 0.0;

        int totalFrames = sampleData.Length / frameSize;
        double sumSquares = 0.0;
        int sampleCount = 0;

        for (int frame = 0; frame < totalFrames; frame++)
        {
            int frameOffset = frame * frameSize;

            for (int ch = 0; ch < channels; ch++)
            {
                int offset = frameOffset + ch * bytesPerSample;
                if (offset + bytesPerSample > sampleData.Length)
                    break;

                double normalizedSample;

                if (bitsPerSample == 16)
                {
                    short sample = (short)(sampleData[offset] | (sampleData[offset + 1] << 8));
                    normalizedSample = sample / 32768.0;
                }
                else if (bitsPerSample == 32)
                {
                    int sample = sampleData[offset]
                               | (sampleData[offset + 1] << 8)
                               | (sampleData[offset + 2] << 16)
                               | (sampleData[offset + 3] << 24);
                    normalizedSample = sample / 2147483648.0;
                }
                else
                {
                    continue;
                }

                sumSquares += normalizedSample * normalizedSample;
                sampleCount++;
            }
        }

        if (sampleCount == 0)
            return 0.0;

        return Math.Sqrt(sumSquares / sampleCount);
    }

    /// <summary>
    /// Reset detector state (EMA baseline, diagnostics).
    /// </summary>
    public void Reset()
    {
        _emaBaseline = 0;
        _emaInitialized = false;
        LastRmsDb = -60.0;
    }
}
