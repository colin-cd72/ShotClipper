using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Detection;

namespace Screener.Golf.Tests.Detection;

public class AudioSwingDetectorTests
{
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly AutoCutConfiguration _config;
    private readonly AudioSwingDetector _detector;

    public AudioSwingDetectorTests()
    {
        _config = new AutoCutConfiguration
        {
            AudioEnabled = true,
            AudioSpikeMultiplier = 5.0,
            MinimumAudioThresholdDb = -30.0,
            AudioEmaAlpha = 0.1
        };
        _detector = new AudioSwingDetector(_logger, _config);
    }

    /// <summary>
    /// Create 16-bit PCM samples at a given amplitude (0.0 - 1.0).
    /// </summary>
    private static byte[] MakePcm16(double amplitude, int sampleCount = 480, int channels = 1)
    {
        var data = new byte[sampleCount * channels * 2];
        short sampleValue = (short)(amplitude * 32767);
        for (int i = 0; i < sampleCount * channels; i++)
        {
            int offset = i * 2;
            data[offset] = (byte)(sampleValue & 0xFF);
            data[offset + 1] = (byte)((sampleValue >> 8) & 0xFF);
        }
        return data;
    }

    [Fact]
    public void ProcessAudio_Silence_NoDetection()
    {
        var silence = MakePcm16(0.0);

        for (int i = 0; i < 20; i++)
        {
            bool result = _detector.ProcessAudio(silence, 48000, 1, 16);
            Assert.False(result);
        }
    }

    [Fact]
    public void ProcessAudio_SteadyLevel_NoDetection()
    {
        // Steady moderate level should not trigger after baseline is established
        var steadyAudio = MakePcm16(0.05);

        for (int i = 0; i < 50; i++)
        {
            bool result = _detector.ProcessAudio(steadyAudio, 48000, 1, 16);
            Assert.False(result);
        }
    }

    [Fact]
    public void ProcessAudio_SuddenSpike_DetectsTransient()
    {
        var quietAudio = MakePcm16(0.01);
        var loudSpike = MakePcm16(0.8);

        // Build baseline with quiet audio
        for (int i = 0; i < 30; i++)
            _detector.ProcessAudio(quietAudio, 48000, 1, 16);

        // Loud spike should trigger detection
        bool detected = _detector.ProcessAudio(loudSpike, 48000, 1, 16);
        Assert.True(detected);
    }

    [Fact]
    public void ProcessAudio_FiresSpikeDetectedEvent()
    {
        var quietAudio = MakePcm16(0.01);
        var loudSpike = MakePcm16(0.8);

        bool eventFired = false;
        _detector.SpikeDetected += (_, _) => eventFired = true;

        for (int i = 0; i < 30; i++)
            _detector.ProcessAudio(quietAudio, 48000, 1, 16);

        _detector.ProcessAudio(loudSpike, 48000, 1, 16);

        Assert.True(eventFired);
    }

    [Fact]
    public void ProcessAudio_UpdatesLastRmsDb()
    {
        var audio = MakePcm16(0.1);

        _detector.ProcessAudio(audio, 48000, 1, 16);

        // RMS of constant 0.1 amplitude signal should be around -20dB
        Assert.True(_detector.LastRmsDb > -60.0);
        Assert.True(_detector.LastRmsDb < 0.0);
    }

    [Fact]
    public void ProcessAudio_AudioDisabled_AlwaysReturnsFalse()
    {
        var disabledConfig = new AutoCutConfiguration { AudioEnabled = false };
        var detector = new AudioSwingDetector(_logger, disabledConfig);

        var loudAudio = MakePcm16(0.9);

        for (int i = 0; i < 30; i++)
        {
            bool result = detector.ProcessAudio(loudAudio, 48000, 1, 16);
            Assert.False(result);
        }
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var audio = MakePcm16(0.1);

        // Build up some state
        for (int i = 0; i < 10; i++)
            _detector.ProcessAudio(audio, 48000, 1, 16);

        Assert.True(_detector.CurrentEma > 0);
        Assert.True(_detector.LastRmsDb > -60.0);

        _detector.Reset();

        Assert.Equal(0, _detector.CurrentEma);
        Assert.Equal(-60.0, _detector.LastRmsDb);
    }

    [Fact]
    public void ProcessAudio_BelowNoiseFloor_StillUpdatesEma()
    {
        // Very quiet audio — below noise floor threshold
        var veryQuiet = MakePcm16(0.0001);

        for (int i = 0; i < 10; i++)
            _detector.ProcessAudio(veryQuiet, 48000, 1, 16);

        // EMA should still have been updated even though below noise floor
        Assert.True(_detector.CurrentEma > 0);
    }
}
