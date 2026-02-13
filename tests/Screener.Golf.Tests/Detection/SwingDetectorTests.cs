using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Detection;

namespace Screener.Golf.Tests.Detection;

public class SwingDetectorTests
{
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly AutoCutConfiguration _config;
    private readonly SwingDetector _detector;

    // Create small test frames (4x4 UYVY = 32 bytes)
    private const int SrcWidth = 4;
    private const int SrcHeight = 4;

    public SwingDetectorTests()
    {
        _config = new AutoCutConfiguration
        {
            AnalysisWidth = 4,
            AnalysisHeight = 4,
            FrameCompareGap = 2,
            EmaAlpha = 0.05,
            SwingSpikeMultiplier = 4.0,
            MinimumSpikeThreshold = 10,
            RoiLeft = 0,
            RoiTop = 0,
            RoiWidth = 1.0,
            RoiHeight = 1.0
        };
        _detector = new SwingDetector(_logger, _config);
    }

    private static byte[] MakeUyvyFrame(byte lumaValue)
    {
        // 4x4 frame: 4 * 4 * 2 = 32 bytes
        var frame = new byte[SrcWidth * SrcHeight * 2];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = 128;     // U
            frame[i + 1] = lumaValue; // Y0
            frame[i + 2] = 128; // V
            frame[i + 3] = lumaValue; // Y1
        }
        return frame;
    }

    [Fact]
    public void ProcessFrame_NeedsMinimumFrames_BeforeDetection()
    {
        var frame = MakeUyvyFrame(128);

        // With FrameCompareGap=2, need 3 frames before comparison
        Assert.False(_detector.ProcessFrame(frame, SrcWidth, SrcHeight));
        Assert.False(_detector.ProcessFrame(frame, SrcWidth, SrcHeight));
        // 3rd frame can compare (frame 0 vs frame 2)
        Assert.False(_detector.ProcessFrame(frame, SrcWidth, SrcHeight));
    }

    [Fact]
    public void ProcessFrame_IdenticalFrames_NoDetection()
    {
        var frame = MakeUyvyFrame(128);

        // Feed many identical frames — should never trigger
        for (int i = 0; i < 20; i++)
        {
            bool result = _detector.ProcessFrame(frame, SrcWidth, SrcHeight);
            Assert.False(result);
        }
    }

    [Fact]
    public void ProcessFrame_LargeChange_DetectsSwing()
    {
        var stableFrame = MakeUyvyFrame(128);
        var spikeFrame = MakeUyvyFrame(10); // drastically different

        // Build up EMA baseline with stable frames
        for (int i = 0; i < 10; i++)
            _detector.ProcessFrame(stableFrame, SrcWidth, SrcHeight);

        // Feed a dramatically different frame
        bool detected = _detector.ProcessFrame(spikeFrame, SrcWidth, SrcHeight);

        Assert.True(detected);
    }

    [Fact]
    public void ProcessFrame_FiresSwingDetectedEvent()
    {
        var stableFrame = MakeUyvyFrame(128);
        var spikeFrame = MakeUyvyFrame(10);

        bool eventFired = false;
        _detector.SwingDetected += (_, _) => eventFired = true;

        for (int i = 0; i < 10; i++)
            _detector.ProcessFrame(stableFrame, SrcWidth, SrcHeight);

        _detector.ProcessFrame(spikeFrame, SrcWidth, SrcHeight);

        Assert.True(eventFired);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var frame = MakeUyvyFrame(128);

        // Process some frames to build state
        for (int i = 0; i < 5; i++)
            _detector.ProcessFrame(frame, SrcWidth, SrcHeight);

        Assert.True(_detector.CurrentEma > 0 || _detector.LastSad >= 0);

        // Reset
        _detector.Reset();

        Assert.Equal(0, _detector.CurrentEma);
        Assert.Equal(0, _detector.LastSad);
    }

    [Fact]
    public void ProcessFrame_UpdatesLastSad()
    {
        var frame1 = MakeUyvyFrame(100);
        var frame2 = MakeUyvyFrame(200);

        _detector.ProcessFrame(frame1, SrcWidth, SrcHeight);
        _detector.ProcessFrame(frame1, SrcWidth, SrcHeight);
        _detector.ProcessFrame(frame2, SrcWidth, SrcHeight);

        // LastSad should be non-zero because frame2 differs from frame1
        Assert.True(_detector.LastSad > 0);
    }

    [Fact]
    public void ProcessFrame_EmaBaseline_UpdatesOverTime()
    {
        var frame = MakeUyvyFrame(128);

        // Feed enough frames to initialize EMA
        for (int i = 0; i < 5; i++)
            _detector.ProcessFrame(frame, SrcWidth, SrcHeight);

        double ema1 = _detector.CurrentEma;

        // Feed same frames — EMA should remain stable (near 0)
        for (int i = 0; i < 5; i++)
            _detector.ProcessFrame(frame, SrcWidth, SrcHeight);

        double ema2 = _detector.CurrentEma;

        // With identical frames, SAD is 0, so EMA converges toward 0
        Assert.True(ema2 <= ema1 || Math.Abs(ema2 - ema1) < 0.01);
    }
}
