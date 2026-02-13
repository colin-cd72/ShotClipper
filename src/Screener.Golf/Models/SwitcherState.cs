namespace Screener.Golf.Models;

/// <summary>
/// Current state of the golf switcher.
/// </summary>
public class SwitcherState
{
    /// <summary>Index of the active (program) source: 0 = Golfer Camera, 1 = Simulator.</summary>
    public int ActiveSourceIndex { get; set; }

    /// <summary>Whether golf mode is enabled.</summary>
    public bool IsGolfModeEnabled { get; set; }

    /// <summary>Whether auto-cut detection is active.</summary>
    public bool IsAutoCutEnabled { get; set; }

    /// <summary>Timestamp of the last cut.</summary>
    public DateTimeOffset? LastCutTime { get; set; }
}
