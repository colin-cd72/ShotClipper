using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Detection;
using Screener.Golf.Models;
using Screener.Golf.Switching;

namespace Screener.Golf.Tests.Integration;

public class FullSwingCycleTests
{
    private readonly AutoCutConfiguration _config;
    private readonly AutoCutService _autoCutService;
    private readonly SwitcherService _switcherService;
    private readonly SequenceRecorder _sequenceRecorder;
    private readonly GolfSession _golfSession;

    private const int W = 4;
    private const int H = 4;

    public FullSwingCycleTests()
    {
        _config = new AutoCutConfiguration
        {
            AnalysisWidth = W,
            AnalysisHeight = H,
            FrameSkip = 1,
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
            PracticeSwingTimeoutSeconds = 0.1,
            PostLandingDelaySeconds = 0,
            CooldownDurationSeconds = 0
        };

        _autoCutService = new AutoCutService(NullLogger<AutoCutService>.Instance, _config);
        _switcherService = new SwitcherService(NullLogger<SwitcherService>.Instance);
        _sequenceRecorder = new SequenceRecorder(
            NullLogger<SequenceRecorder>.Instance, _switcherService);
        _golfSession = new GolfSession(NullLogger<GolfSession>.Instance);

        // Wire auto-cut → switcher (simulates what MainViewModel does)
        _autoCutService.CutTriggered += (_, e) =>
            _switcherService.CutToSource(e.TargetSourceIndex, e.Reason);
    }

    private static byte[] MakeFrame(byte lumaValue)
    {
        var frame = new byte[W * H * 2];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = 128; frame[i + 1] = lumaValue;
            frame[i + 2] = 128; frame[i + 3] = lumaValue;
        }
        return frame;
    }

    private void EstablishBaseline(byte lumaValue = 128)
    {
        var frame = MakeFrame(lumaValue);
        // Feed enough frames to build a stable EMA baseline
        for (int i = 0; i < 20; i++)
        {
            _autoCutService.ProcessSource1Frame(frame, W, H);
        }
    }

    private void TriggerSwing()
    {
        // Create a large spike vs baseline to trigger swing detection
        var spikeFrame = MakeFrame(255);
        _autoCutService.ProcessSource1Frame(spikeFrame, W, H);
    }

    private void SimulateActivity()
    {
        // Feed varying frames to the simulator source (not idle)
        for (int i = 0; i < 5; i++)
        {
            var frame = MakeFrame((byte)(50 + i * 30));
            _autoCutService.ProcessSource2Frame(frame, W, H);
        }
    }

    private void SimulateIdleReset()
    {
        // Feed idle frames matching the calibration reference
        var idleFrame = MakeFrame(128);
        // Need consecutive idle frames to confirm reset
        for (int i = 0; i < 3; i++)
        {
            _autoCutService.ProcessSource2Frame(idleFrame, W, H);
        }
    }

    [Fact]
    public void FullSwingCycle_FromBaselineToCompletion()
    {
        // Arrange: Start session
        var session = _golfSession.StartSession(null);
        _sequenceRecorder.StartSession(session.Id);

        // Calibrate idle reference
        var idleFrame = MakeFrame(128);
        _autoCutService.CalibrateIdleReference(idleFrame, W, H);
        _autoCutService.Enable();

        Assert.Equal(AutoCutState.WaitingForSwing, _autoCutService.State);
        Assert.Equal(0, _switcherService.ActiveSourceIndex);

        // Build baseline
        EstablishBaseline();

        // Track completed sequences
        var completedSequences = new List<SwingSequence>();
        _sequenceRecorder.SequenceCompleted += (_, seq) => completedSequences.Add(seq);

        // Act: Trigger swing
        TriggerSwing();

        // Assert: Should have cut to simulator (source 1)
        Assert.Equal(1, _switcherService.ActiveSourceIndex);
        Assert.Equal(AutoCutState.FollowingShot, _autoCutService.State);
        Assert.Equal(1, _sequenceRecorder.SequenceCount);

        // Simulate activity on simulator (ball flying)
        // Wait enough time to pass practice swing timeout
        Thread.Sleep(150); // > 0.1s PracticeSwingTimeoutSeconds
        SimulateActivity();

        // Simulate reset (ball landed, sim returns to idle)
        SimulateIdleReset();

        // Post-landing delay is 0, so it should immediately trigger cut back
        // Tick to process cooldown
        _autoCutService.Tick();

        // Assert: Should have cut back to golfer (source 0)
        Assert.Equal(0, _switcherService.ActiveSourceIndex);

        // Sequence should be completed
        Assert.Equal(1, completedSequences.Count);
        Assert.Equal(1, completedSequences[0].SequenceNumber);
        Assert.NotNull(completedSequences[0].OutPointTicks);

        // Cooldown should have ended (duration 0)
        _autoCutService.Tick();
        Assert.Equal(AutoCutState.WaitingForSwing, _autoCutService.State);

        // Cleanup
        _sequenceRecorder.StopSession();
        _golfSession.EndSession();
    }

    [Fact]
    public void PracticeSwing_DetectedAndDiscarded()
    {
        // Arrange
        var session = _golfSession.StartSession(null);
        _sequenceRecorder.StartSession(session.Id);

        var idleFrame = MakeFrame(128);
        _autoCutService.CalibrateIdleReference(idleFrame, W, H);
        _autoCutService.Enable();
        EstablishBaseline();

        var completedSequences = new List<SwingSequence>();
        _sequenceRecorder.SequenceCompleted += (_, seq) => completedSequences.Add(seq);

        // Act: Trigger swing
        TriggerSwing();
        Assert.Equal(1, _switcherService.ActiveSourceIndex);

        // Immediately feed idle frames (within practice swing timeout)
        // The sim is idle right away = practice swing
        SimulateIdleReset();

        // Assert: Should cut back with "practice_swing" reason
        Assert.Equal(0, _switcherService.ActiveSourceIndex);

        // Practice swings are removed from the sequence recorder
        Assert.Equal(0, _sequenceRecorder.SequenceCount);
        Assert.Empty(completedSequences);

        // Cooldown → WaitingForSwing
        _autoCutService.Tick();
        Assert.Equal(AutoCutState.WaitingForSwing, _autoCutService.State);

        _sequenceRecorder.StopSession();
        _golfSession.EndSession();
    }

    [Fact]
    public void MultipleConsecutiveSwings_TrackedCorrectly()
    {
        // Arrange
        var session = _golfSession.StartSession(null);
        _sequenceRecorder.StartSession(session.Id);

        var idleFrame = MakeFrame(128);
        _autoCutService.CalibrateIdleReference(idleFrame, W, H);
        _autoCutService.Enable();
        EstablishBaseline();

        var completedSequences = new List<SwingSequence>();
        _sequenceRecorder.SequenceCompleted += (_, seq) => completedSequences.Add(seq);

        // Swing 1
        TriggerSwing();
        Assert.Equal(1, _switcherService.ActiveSourceIndex);
        Thread.Sleep(150);
        SimulateActivity();
        SimulateIdleReset();
        _autoCutService.Tick();
        Assert.Equal(0, _switcherService.ActiveSourceIndex);
        _autoCutService.Tick();

        // Re-establish baseline for next swing
        EstablishBaseline();

        // Swing 2
        TriggerSwing();
        Assert.Equal(1, _switcherService.ActiveSourceIndex);
        Thread.Sleep(150);
        SimulateActivity();
        SimulateIdleReset();
        _autoCutService.Tick();
        Assert.Equal(0, _switcherService.ActiveSourceIndex);
        _autoCutService.Tick();

        EstablishBaseline();

        // Swing 3
        TriggerSwing();
        Assert.Equal(1, _switcherService.ActiveSourceIndex);
        Thread.Sleep(150);
        SimulateActivity();
        SimulateIdleReset();
        _autoCutService.Tick();
        Assert.Equal(0, _switcherService.ActiveSourceIndex);
        _autoCutService.Tick();

        // Assert: 3 swings tracked with correct sequence numbers
        Assert.Equal(3, completedSequences.Count);
        Assert.Equal(1, completedSequences[0].SequenceNumber);
        Assert.Equal(2, completedSequences[1].SequenceNumber);
        Assert.Equal(3, completedSequences[2].SequenceNumber);
        Assert.Equal(3, _sequenceRecorder.SequenceCount);

        _sequenceRecorder.StopSession();
        _golfSession.EndSession();
    }

    [Fact]
    public void SessionLifecycle_SwingCountTracked()
    {
        var session = _golfSession.StartSession(null);
        _sequenceRecorder.StartSession(session.Id);

        Assert.True(_golfSession.IsSessionActive);
        Assert.Equal(0, _golfSession.TotalSwings);

        // Manually increment swings (simulates what MainViewModel does)
        _golfSession.IncrementSwingCount();
        Assert.Equal(1, _golfSession.TotalSwings);

        _golfSession.IncrementSwingCount();
        Assert.Equal(2, _golfSession.TotalSwings);

        // Decrement (practice swing discarded)
        _golfSession.DecrementSwingCount();
        Assert.Equal(1, _golfSession.TotalSwings);

        _sequenceRecorder.StopSession();
        var endedSession = _golfSession.EndSession();

        Assert.NotNull(endedSession);
        Assert.False(_golfSession.IsSessionActive);
    }
}
