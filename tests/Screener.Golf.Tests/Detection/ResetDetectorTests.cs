using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Detection;

namespace Screener.Golf.Tests.Detection;

public class ResetDetectorTests
{
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly AutoCutConfiguration _config;
    private readonly ResetDetector _detector;

    private const int SrcWidth = 4;
    private const int SrcHeight = 4;

    public ResetDetectorTests()
    {
        _config = new AutoCutConfiguration
        {
            AnalysisWidth = 4,
            AnalysisHeight = 4,
            IdleSimilarityThreshold = 0.95,
            ConsecutiveIdleFramesRequired = 3,
            StaticSceneThreshold = 200
        };
        _detector = new ResetDetector(_logger, _config);
    }

    private static byte[] MakeUyvyFrame(byte lumaValue)
    {
        var frame = new byte[SrcWidth * SrcHeight * 2];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = 128;
            frame[i + 1] = lumaValue;
            frame[i + 2] = 128;
            frame[i + 3] = lumaValue;
        }
        return frame;
    }

    [Fact]
    public void ProcessFrame_NotCalibrated_ReturnsFalse()
    {
        Assert.False(_detector.IsCalibrated);

        var frame = MakeUyvyFrame(128);
        bool result = _detector.ProcessFrame(frame, SrcWidth, SrcHeight);

        Assert.False(result);
    }

    [Fact]
    public void CalibrateIdleReference_SetsIsCalibrated()
    {
        var frame = MakeUyvyFrame(128);
        _detector.CalibrateIdleReference(frame, SrcWidth, SrcHeight);

        Assert.True(_detector.IsCalibrated);
    }

    [Fact]
    public void ProcessFrame_MatchesIdleReference_DetectsResetAfterConsecutiveFrames()
    {
        var idleFrame = MakeUyvyFrame(128);
        _detector.CalibrateIdleReference(idleFrame, SrcWidth, SrcHeight);

        bool detected = false;
        _detector.ResetDetected += (_, _) => detected = true;

        // Need ConsecutiveIdleFramesRequired (3) consecutive idle frames
        for (int i = 0; i < _config.ConsecutiveIdleFramesRequired; i++)
        {
            bool result = _detector.ProcessFrame(idleFrame, SrcWidth, SrcHeight);
            if (i < _config.ConsecutiveIdleFramesRequired - 1)
                Assert.False(result);
            else
                Assert.True(result);
        }

        Assert.True(detected);
    }

    [Fact]
    public void ProcessFrame_DifferentFrame_DoesNotDetectReset()
    {
        var idleFrame = MakeUyvyFrame(128);
        var activeFrame = MakeUyvyFrame(10); // very different
        _detector.CalibrateIdleReference(idleFrame, SrcWidth, SrcHeight);

        // Feed many different frames — should never trigger
        for (int i = 0; i < 10; i++)
        {
            bool result = _detector.ProcessFrame(activeFrame, SrcWidth, SrcHeight);
            Assert.False(result);
        }
    }

    [Fact]
    public void ProcessFrame_InterleavedFrames_ResetsConsecutiveCounter()
    {
        var idleFrame = MakeUyvyFrame(128);
        var activeFrame = MakeUyvyFrame(10);
        _detector.CalibrateIdleReference(idleFrame, SrcWidth, SrcHeight);

        // 2 idle frames then 1 active — resets counter
        _detector.ProcessFrame(idleFrame, SrcWidth, SrcHeight);
        _detector.ProcessFrame(idleFrame, SrcWidth, SrcHeight);
        _detector.ProcessFrame(activeFrame, SrcWidth, SrcHeight);

        // Now need 3 more consecutive idle frames
        bool result = false;
        for (int i = 0; i < _config.ConsecutiveIdleFramesRequired; i++)
        {
            result = _detector.ProcessFrame(idleFrame, SrcWidth, SrcHeight);
        }
        Assert.True(result);
    }

    [Fact]
    public void Reset_ClearsCounters_ButKeepsCalibration()
    {
        var idleFrame = MakeUyvyFrame(128);
        _detector.CalibrateIdleReference(idleFrame, SrcWidth, SrcHeight);

        // Process a few frames
        _detector.ProcessFrame(idleFrame, SrcWidth, SrcHeight);

        _detector.Reset();

        // Calibration preserved
        Assert.True(_detector.IsCalibrated);
        Assert.Equal(0, _detector.LastSimilarity);
        Assert.Equal(0, _detector.LastInterFrameSad);
    }

    [Fact]
    public void ClearCalibration_ClearsEverything()
    {
        var idleFrame = MakeUyvyFrame(128);
        _detector.CalibrateIdleReference(idleFrame, SrcWidth, SrcHeight);
        Assert.True(_detector.IsCalibrated);

        _detector.ClearCalibration();

        Assert.False(_detector.IsCalibrated);
    }

    [Fact]
    public void ProcessFrame_UpdatesSimilarityAndSad()
    {
        var idleFrame = MakeUyvyFrame(128);
        var differentFrame = MakeUyvyFrame(200);
        _detector.CalibrateIdleReference(idleFrame, SrcWidth, SrcHeight);

        _detector.ProcessFrame(differentFrame, SrcWidth, SrcHeight);

        Assert.True(_detector.LastSimilarity < 1.0);
        // InterFrameSAD may be 0 on first frame since previousLuma was zeroed
    }
}
