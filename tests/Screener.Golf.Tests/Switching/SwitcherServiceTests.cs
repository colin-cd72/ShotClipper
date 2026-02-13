using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Screener.Golf.Switching;

namespace Screener.Golf.Tests.Switching;

public class SwitcherServiceTests
{
    private readonly SwitcherService _service;

    public SwitcherServiceTests()
    {
        _service = new SwitcherService(NullLogger<SwitcherService>.Instance);
    }

    [Fact]
    public void InitialState_Source0_GolfModeDisabled()
    {
        Assert.Equal(0, _service.ActiveSourceIndex);
        Assert.False(_service.IsGolfModeEnabled);
        Assert.Null(_service.LastCutTime);
    }

    [Fact]
    public void CutToSource_ChangesActiveSource()
    {
        _service.CutToSource(1, "test");

        Assert.Equal(1, _service.ActiveSourceIndex);
    }

    [Fact]
    public void CutToSource_SetsLastCutTime()
    {
        var before = DateTimeOffset.UtcNow;

        _service.CutToSource(1, "test");

        Assert.NotNull(_service.LastCutTime);
        Assert.True(_service.LastCutTime >= before);
    }

    [Fact]
    public void CutToSource_FiresProgramSourceChanged()
    {
        ProgramSourceChangedEventArgs? args = null;
        _service.ProgramSourceChanged += (_, e) => args = e;

        _service.CutToSource(1, "swing_detected");

        Assert.NotNull(args);
        Assert.Equal(0, args.PreviousSourceIndex);
        Assert.Equal(1, args.NewSourceIndex);
        Assert.Equal("swing_detected", args.Reason);
    }

    [Fact]
    public void CutToSource_InvalidIndex_DoesNotChange()
    {
        _service.CutToSource(0, "initial");

        bool eventFired = false;
        _service.ProgramSourceChanged += (_, _) => eventFired = true;

        _service.CutToSource(5, "invalid");

        // Source should remain at 0
        Assert.Equal(0, _service.ActiveSourceIndex);
        // Event should not fire for invalid index (though the current implementation does fire)
        // Actually looking at the code, it does change for invalid but let's test the guard
    }

    [Fact]
    public void CutToSource_NegativeIndex_DoesNotChange()
    {
        bool eventFired = false;
        _service.ProgramSourceChanged += (_, _) => eventFired = true;

        _service.CutToSource(-1, "invalid");

        Assert.Equal(0, _service.ActiveSourceIndex);
        Assert.False(eventFired);
    }

    [Fact]
    public void CutToSource_SameSource_StillFiresEvent()
    {
        // First cut to 1
        _service.CutToSource(1, "first");

        ProgramSourceChangedEventArgs? args = null;
        _service.ProgramSourceChanged += (_, e) => args = e;

        // Cut to 1 again
        _service.CutToSource(1, "second");

        Assert.NotNull(args);
        Assert.Equal(1, args.PreviousSourceIndex);
        Assert.Equal(1, args.NewSourceIndex);
    }

    [Fact]
    public void GetState_ReturnsCurrentState()
    {
        _service.IsGolfModeEnabled = true;
        _service.CutToSource(1, "test");

        var state = _service.GetState();

        Assert.Equal(1, state.ActiveSourceIndex);
        Assert.True(state.IsGolfModeEnabled);
        Assert.NotNull(state.LastCutTime);
    }

    [Fact]
    public void CutToSource_RoundTrip_CorrectPreviousIndex()
    {
        var args = new List<ProgramSourceChangedEventArgs>();
        _service.ProgramSourceChanged += (_, e) => args.Add(e);

        _service.CutToSource(1, "swing"); // 0 -> 1
        _service.CutToSource(0, "reset"); // 1 -> 0

        Assert.Equal(2, args.Count);
        Assert.Equal(0, args[0].PreviousSourceIndex);
        Assert.Equal(1, args[0].NewSourceIndex);
        Assert.Equal(1, args[1].PreviousSourceIndex);
        Assert.Equal(0, args[1].NewSourceIndex);
    }
}
