using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Models;
using Screener.Golf.Switching;

namespace Screener.Golf.Tests.Switching;

public class SequenceRecorderTests
{
    private readonly SwitcherService _switcherService;
    private readonly SequenceRecorder _recorder;

    public SequenceRecorderTests()
    {
        _switcherService = new SwitcherService(NullLogger<SwitcherService>.Instance);
        _recorder = new SequenceRecorder(NullLogger<SequenceRecorder>.Instance, _switcherService);
    }

    [Fact]
    public void StartSession_InitializesState()
    {
        _recorder.StartSession("session-1");

        Assert.Equal(0, _recorder.SequenceCount);
        Assert.Null(_recorder.ActiveSequence);
    }

    [Fact]
    public void CutToSource2_StartsSequence()
    {
        _recorder.StartSession("session-1");

        SwingSequence? started = null;
        _recorder.SequenceStarted += (_, seq) => started = seq;

        _switcherService.CutToSource(1, "swing_detected");

        Assert.NotNull(started);
        Assert.Equal(1, started.SequenceNumber);
        Assert.Equal("session-1", started.SessionId);
        Assert.Equal("auto", started.DetectionMethod);
        Assert.NotNull(_recorder.ActiveSequence);
    }

    [Fact]
    public void CutToSource2_Manual_SetsManualDetectionMethod()
    {
        _recorder.StartSession("session-1");

        SwingSequence? started = null;
        _recorder.SequenceStarted += (_, seq) => started = seq;

        _switcherService.CutToSource(1, "manual");

        Assert.NotNull(started);
        Assert.Equal("manual", started.DetectionMethod);
    }

    [Fact]
    public void CutBackToSource1_CompletesSequence()
    {
        _recorder.StartSession("session-1");

        SwingSequence? completed = null;
        _recorder.SequenceCompleted += (_, seq) => completed = seq;

        // Swing start (cut to sim)
        _switcherService.CutToSource(1, "swing_detected");
        // Swing end (cut back to golfer)
        _switcherService.CutToSource(0, "ball_landed");

        Assert.NotNull(completed);
        Assert.Equal(1, completed.SequenceNumber);
        Assert.True(completed.OutPointTicks.HasValue);
        Assert.Equal("pending", completed.ExportStatus);
        Assert.Null(_recorder.ActiveSequence);
    }

    [Fact]
    public void PracticeSwing_DiscardsSequence()
    {
        _recorder.StartSession("session-1");

        SwingSequence? completed = null;
        _recorder.SequenceCompleted += (_, seq) => completed = seq;

        // Swing start
        _switcherService.CutToSource(1, "swing_detected");
        Assert.Equal(1, _recorder.SequenceCount);

        // Practice swing cutback
        _switcherService.CutToSource(0, "practice_swing");

        Assert.Null(completed); // Should NOT fire SequenceCompleted
        Assert.Equal(0, _recorder.SequenceCount); // Sequence removed
        Assert.Null(_recorder.ActiveSequence);
    }

    [Fact]
    public void MultipleSwings_IncrementsSequenceNumber()
    {
        _recorder.StartSession("session-1");
        var completedSequences = new List<SwingSequence>();
        _recorder.SequenceCompleted += (_, seq) => completedSequences.Add(seq);

        // Swing 1
        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "ball_landed");

        // Swing 2
        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "ball_landed");

        Assert.Equal(2, completedSequences.Count);
        Assert.Equal(1, completedSequences[0].SequenceNumber);
        Assert.Equal(2, completedSequences[1].SequenceNumber);
    }

    [Fact]
    public void NoSession_IgnoresCuts()
    {
        // Don't call StartSession
        bool anyEvent = false;
        _recorder.SequenceStarted += (_, _) => anyEvent = true;

        _switcherService.CutToSource(1, "swing_detected");

        Assert.False(anyEvent);
    }

    [Fact]
    public void StopSession_ClosesActiveSequence()
    {
        _recorder.StartSession("session-1");

        SwingSequence? completed = null;
        _recorder.SequenceCompleted += (_, seq) => completed = seq;

        // Start swing but don't complete it
        _switcherService.CutToSource(1, "swing_detected");
        Assert.NotNull(_recorder.ActiveSequence);

        // Stop session should close the active sequence
        _recorder.StopSession();

        Assert.NotNull(completed);
        Assert.True(completed.OutPointTicks.HasValue);
    }

    [Fact]
    public void StopSession_NoActiveSequence_DoesNotFire()
    {
        _recorder.StartSession("session-1");

        SwingSequence? completed = null;
        _recorder.SequenceCompleted += (_, seq) => completed = seq;

        _recorder.StopSession();

        Assert.Null(completed);
    }

    [Fact]
    public void Sequences_ReturnsAllCompletedSequences()
    {
        _recorder.StartSession("session-1");

        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "ball_landed");
        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "ball_landed");

        var sequences = _recorder.Sequences;
        Assert.Equal(2, sequences.Count);
    }

    [Fact]
    public void StartSession_ClearsPreviousSequences()
    {
        _recorder.StartSession("session-1");
        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "ball_landed");
        Assert.Equal(1, _recorder.SequenceCount);

        _recorder.StartSession("session-2");
        Assert.Equal(0, _recorder.SequenceCount);
    }

    [Fact]
    public void PracticeSwing_AfterRealSwing_CounterCorrect()
    {
        _recorder.StartSession("session-1");

        // Real swing
        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "ball_landed");

        // Practice swing (gets discarded)
        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "practice_swing");

        // Next real swing should be #2 (not #3)
        _switcherService.CutToSource(1, "swing_detected");
        _switcherService.CutToSource(0, "ball_landed");

        Assert.Equal(2, _recorder.SequenceCount);
        var sequences = _recorder.Sequences;
        Assert.Equal(1, sequences[0].SequenceNumber);
        Assert.Equal(2, sequences[1].SequenceNumber);
    }
}
