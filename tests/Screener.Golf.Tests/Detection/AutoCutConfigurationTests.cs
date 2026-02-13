using Screener.Golf.Detection;

namespace Screener.Golf.Tests.Detection;

public class AutoCutConfigurationTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var config = AutoCutConfiguration.Default;

        Assert.Equal(120, config.AnalysisWidth);
        Assert.Equal(68, config.AnalysisHeight);
        Assert.Equal(4, config.FrameSkip);
        Assert.Equal(0.05, config.EmaAlpha);
        Assert.Equal(4.0, config.SwingSpikeMultiplier);
        Assert.Equal(500, config.MinimumSpikeThreshold);
        Assert.Equal(2, config.FrameCompareGap);
        Assert.Equal(0.95, config.IdleSimilarityThreshold);
        Assert.Equal(3, config.ConsecutiveIdleFramesRequired);
        Assert.Equal(30, config.MaxSimulatorDurationSeconds);
    }

    [Fact]
    public void Default_HasExpectedAudioValues()
    {
        var config = AutoCutConfiguration.Default;

        Assert.True(config.AudioEnabled);
        Assert.Equal(5.0, config.AudioSpikeMultiplier);
        Assert.Equal(-30.0, config.MinimumAudioThresholdDb);
        Assert.Equal(0.1, config.AudioEmaAlpha);
        Assert.False(config.AudioOnlyMode);
        Assert.Equal(200.0, config.AudioVideoFusionWindowMs);
    }

    [Fact]
    public void HighSensitivity_HasLowerAudioThresholds()
    {
        var config = AutoCutConfiguration.HighSensitivity;

        Assert.Equal(3.5, config.AudioSpikeMultiplier);
        Assert.Equal(-35.0, config.MinimumAudioThresholdDb);
        Assert.Equal(0.15, config.AudioEmaAlpha);
    }

    [Fact]
    public void HighSensitivity_HasLowerThresholds()
    {
        var config = AutoCutConfiguration.HighSensitivity;

        Assert.Equal(3.0, config.SwingSpikeMultiplier);
        Assert.Equal(300, config.MinimumSpikeThreshold);
        Assert.Equal(0.90, config.IdleSimilarityThreshold);
        Assert.Equal(2, config.ConsecutiveIdleFramesRequired);
    }

    [Fact]
    public void LowSensitivity_HasHigherThresholds()
    {
        var config = AutoCutConfiguration.LowSensitivity;

        Assert.Equal(6.0, config.SwingSpikeMultiplier);
        Assert.Equal(800, config.MinimumSpikeThreshold);
        Assert.Equal(0.97, config.IdleSimilarityThreshold);
        Assert.Equal(4, config.ConsecutiveIdleFramesRequired);
    }

    [Fact]
    public void HighSensitivity_IsMoreSensitiveThanDefault()
    {
        var high = AutoCutConfiguration.HighSensitivity;
        var def = AutoCutConfiguration.Default;

        Assert.True(high.SwingSpikeMultiplier < def.SwingSpikeMultiplier);
        Assert.True(high.MinimumSpikeThreshold < def.MinimumSpikeThreshold);
        Assert.True(high.IdleSimilarityThreshold < def.IdleSimilarityThreshold);
    }

    [Fact]
    public void LowSensitivity_IsLessSensitiveThanDefault()
    {
        var low = AutoCutConfiguration.LowSensitivity;
        var def = AutoCutConfiguration.Default;

        Assert.True(low.SwingSpikeMultiplier > def.SwingSpikeMultiplier);
        Assert.True(low.MinimumSpikeThreshold > def.MinimumSpikeThreshold);
        Assert.True(low.IdleSimilarityThreshold > def.IdleSimilarityThreshold);
    }
}
