namespace Screener.Golf.Models;

/// <summary>
/// States for the auto-cut detection state machine.
/// </summary>
public enum AutoCutState
{
    /// <summary>Watching Source 1 for swing motion.</summary>
    WaitingForSwing,

    /// <summary>Swing spike detected, about to cut to simulator.</summary>
    SwingDetected,

    /// <summary>On Source 2, watching for ball landing / simulator reset.</summary>
    FollowingShot,

    /// <summary>Landing detected, post-landing delay before cutting back.</summary>
    ResetDetected,

    /// <summary>Cooldown period after cutting back to Source 1.</summary>
    Cooldown,

    /// <summary>Auto-cut is disabled.</summary>
    Disabled
}
