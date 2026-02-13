using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Detection;
using Screener.Golf.Models;

namespace Screener.Golf.Tests.Detection;

public class AutoCutServiceTests
{
    private readonly AutoCutService _service;
    private readonly AutoCutConfiguration _config;

    private const int SrcWidth = 4;
    private const int SrcHeight = 4;

    public AutoCutServiceTests()
    {
        _config = new AutoCutConfiguration
        {
            AnalysisWidth = 4,
            AnalysisHeight = 4,
            FrameSkip = 1, // analyze every frame for testing
            FrameCompareGap = 2,
            EmaAlpha = 0.05,
            SwingSpikeMultiplier = 4.0,
            MinimumSpikeThreshold = 10,
            RoiLeft = 0,
            RoiTop = 0,
            RoiWidth = 1.0,
            RoiHeight = 1.0,
            IdleSimilarityThreshold = 0.95,
            ConsecutiveIdleFramesRequired = 2,
            MaxSimulatorDurationSeconds = 30,
            PracticeSwingTimeoutSeconds = 3,
            PostLandingDelaySeconds = 0, // no delay for testing
            CooldownDurationSeconds = 0  // no cooldown for testing
        };
        _service = new AutoCutService(NullLogger<AutoCutService>.Instance, _config);
    }

    private static byte[] MakeFrame(byte lumaValue)
    {
        var frame = new byte[SrcWidth * SrcHeight * 2];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = 128; frame[i + 1] = lumaValue;
            frame[i + 2] = 128; frame[i + 3] = lumaValue;
        }
        return frame;
    }

    [Fact]
    public void InitialState_IsDisabled()
    {
        Assert.Equal(AutoCutState.Disabled, _service.State);
    }

    [Fact]
    public void Enable_WithoutCalibration_StaysDisabled()
    {
        _service.Enable();

        // Without calibration, Enable should not transition
        Assert.Equal(AutoCutState.Disabled, _service.State);
    }

    [Fact]
    public void Enable_WithCalibration_TransitionsToWaitingForSwing()
    {
        var frame = MakeFrame(128);
        _service.CalibrateIdleReference(frame, SrcWidth, SrcHeight);

        _service.Enable();

        Assert.Equal(AutoCutState.WaitingForSwing, _service.State);
    }

    [Fact]
    public void Disable_TransitionsToDisabled()
    {
        var frame = MakeFrame(128);
        _service.CalibrateIdleReference(frame, SrcWidth, SrcHeight);
        _service.Enable();

        _service.Disable();

        Assert.Equal(AutoCutState.Disabled, _service.State);
    }

    [Fact]
    public void ProcessSource1Frame_WhenDisabled_NoEffect()
    {
        var frame = MakeFrame(128);
        bool cutTriggered = false;
        _service.CutTriggered += (_, _) => cutTriggered = true;

        _service.ProcessSource1Frame(frame, SrcWidth, SrcHeight);

        Assert.False(cutTriggered);
    }

    [Fact]
    public void ProcessSource1Frame_DetectsSwing_FiresCutToSource2()
    {
        var stableFrame = MakeFrame(128);
        var spikeFrame = MakeFrame(10);

        _service.CalibrateIdleReference(stableFrame, SrcWidth, SrcHeight);
        _service.Enable();

        int? cutTarget = null;
        _service.CutTriggered += (_, e) => cutTarget = e.TargetSourceIndex;

        // Build baseline
        for (int i = 0; i < 10; i++)
            _service.ProcessSource1Frame(stableFrame, SrcWidth, SrcHeight);

        // Spike frame should trigger cut to source 2
        _service.ProcessSource1Frame(spikeFrame, SrcWidth, SrcHeight);

        Assert.Equal(1, cutTarget);
        Assert.Equal(AutoCutState.FollowingShot, _service.State);
    }

    [Fact]
    public void StateChanged_FiresOnTransition()
    {
        var frame = MakeFrame(128);
        _service.CalibrateIdleReference(frame, SrcWidth, SrcHeight);

        var states = new List<AutoCutState>();
        _service.StateChanged += (_, state) => states.Add(state);

        _service.Enable();
        _service.Disable();

        Assert.Contains(AutoCutState.WaitingForSwing, states);
        Assert.Contains(AutoCutState.Disabled, states);
    }

    [Fact]
    public void UpdateConfiguration_ChangesConfig()
    {
        var newConfig = AutoCutConfiguration.HighSensitivity;
        _service.UpdateConfiguration(newConfig);

        Assert.Equal(newConfig, _service.Configuration);
    }

    [Fact]
    public void CalibrateIdleReference_CalibatesResetDetector()
    {
        var frame = MakeFrame(128);

        Assert.False(_service.ResetDetector.IsCalibrated);

        _service.CalibrateIdleReference(frame, SrcWidth, SrcHeight);

        Assert.True(_service.ResetDetector.IsCalibrated);
    }

    [Fact]
    public void ProcessAudioFrame_WhenDisabled_NoEffect()
    {
        // Service is in Disabled state, audio should not trigger anything
        var loudAudio = MakeAudioSamples(0.9);
        bool cutTriggered = false;
        _service.CutTriggered += (_, _) => cutTriggered = true;

        _service.ProcessAudioFrame(loudAudio, 48000, 1, 16);

        Assert.False(cutTriggered);
    }

    [Fact]
    public void ProcessAudioFrame_AudioOnlyMode_TriggersCut()
    {
        var audioOnlyConfig = new AutoCutConfiguration
        {
            AnalysisWidth = 4,
            AnalysisHeight = 4,
            FrameSkip = 1,
            FrameCompareGap = 2,
            MinimumSpikeThreshold = 10,
            RoiLeft = 0, RoiTop = 0, RoiWidth = 1.0, RoiHeight = 1.0,
            IdleSimilarityThreshold = 0.95,
            ConsecutiveIdleFramesRequired = 2,
            PostLandingDelaySeconds = 0,
            CooldownDurationSeconds = 0,
            AudioEnabled = true,
            AudioOnlyMode = true,
            AudioSpikeMultiplier = 5.0,
            MinimumAudioThresholdDb = -30.0,
            AudioEmaAlpha = 0.1
        };
        var service = new AutoCutService(NullLogger<AutoCutService>.Instance, audioOnlyConfig);

        var stableFrame = MakeFrame(128);
        service.CalibrateIdleReference(stableFrame, SrcWidth, SrcHeight);
        service.Enable();

        int? cutTarget = null;
        string? cutReason = null;
        service.CutTriggered += (_, e) => { cutTarget = e.TargetSourceIndex; cutReason = e.Reason; };

        // Build audio baseline with quiet samples
        var quietAudio = MakeAudioSamples(0.01);
        for (int i = 0; i < 30; i++)
            service.ProcessAudioFrame(quietAudio, 48000, 1, 16);

        // Loud spike should trigger cut
        var loudAudio = MakeAudioSamples(0.8);
        service.ProcessAudioFrame(loudAudio, 48000, 1, 16);

        Assert.Equal(1, cutTarget);
        Assert.Equal("audio_swing", cutReason);
    }

    private static byte[] MakeAudioSamples(double amplitude, int sampleCount = 480)
    {
        var data = new byte[sampleCount * 2]; // 16-bit mono
        short sampleValue = (short)(amplitude * 32767);
        for (int i = 0; i < sampleCount; i++)
        {
            int offset = i * 2;
            data[offset] = (byte)(sampleValue & 0xFF);
            data[offset + 1] = (byte)((sampleValue >> 8) & 0xFF);
        }
        return data;
    }

    [Fact]
    public void ProcessSource1Frame_RespectsFrameSkip()
    {
        var configWithSkip = new AutoCutConfiguration
        {
            AnalysisWidth = 4,
            AnalysisHeight = 4,
            FrameSkip = 4, // only analyze every 4th frame
            FrameCompareGap = 2,
            MinimumSpikeThreshold = 10,
            RoiLeft = 0, RoiTop = 0, RoiWidth = 1.0, RoiHeight = 1.0,
        };
        var service = new AutoCutService(NullLogger<AutoCutService>.Instance, configWithSkip);

        var frame = MakeFrame(128);
        service.CalibrateIdleReference(frame, SrcWidth, SrcHeight);
        service.Enable();

        // Only every 4th frame should be analyzed.
        // The SwingDetector won't have enough frames until more than FrameCompareGap * FrameSkip
        // raw frames have been processed.
        for (int i = 0; i < 3; i++)
            service.ProcessSource1Frame(frame, SrcWidth, SrcHeight);

        // The detector should not have detected anything with stable frames
        Assert.Equal(AutoCutState.WaitingForSwing, service.State);
    }
}
