using Microsoft.Extensions.Logging;
using Screener.Golf.Models;

namespace Screener.Golf.Switching;

/// <summary>
/// Tracks the active program source and exposes cut operations.
/// Integrates with the existing InputConfigurationViewModel.SelectInput() mechanism.
/// </summary>
public class SwitcherService
{
    private readonly ILogger<SwitcherService> _logger;
    private int _activeSourceIndex;

    /// <summary>Index of the current program source (0 = golfer camera, 1 = simulator).</summary>
    public int ActiveSourceIndex => _activeSourceIndex;

    /// <summary>Whether golf mode is enabled.</summary>
    public bool IsGolfModeEnabled { get; set; }

    /// <summary>Timestamp of the last source cut.</summary>
    public DateTimeOffset? LastCutTime { get; private set; }

    /// <summary>
    /// Fired when the program source changes.
    /// EventArgs int = new active source index.
    /// </summary>
    public event EventHandler<ProgramSourceChangedEventArgs>? ProgramSourceChanged;

    public SwitcherService(ILogger<SwitcherService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Cut to a specific source. Fires ProgramSourceChanged if the source actually changes.
    /// </summary>
    /// <param name="sourceIndex">0 = golfer camera, 1 = simulator output.</param>
    /// <param name="reason">Why the cut happened (manual, swing_detected, ball_landed, etc.).</param>
    public void CutToSource(int sourceIndex, string reason = "manual")
    {
        if (sourceIndex < 0 || sourceIndex > 1)
        {
            _logger.LogWarning("Invalid source index: {Index}", sourceIndex);
            return;
        }

        int previousIndex = _activeSourceIndex;
        _activeSourceIndex = sourceIndex;
        LastCutTime = DateTimeOffset.UtcNow;

        _logger.LogInformation("Cut to Source {Index} (reason: {Reason})", sourceIndex + 1, reason);

        ProgramSourceChanged?.Invoke(this, new ProgramSourceChangedEventArgs(
            previousIndex, sourceIndex, reason));
    }

    /// <summary>
    /// Get the current switcher state snapshot.
    /// </summary>
    public SwitcherState GetState() => new()
    {
        ActiveSourceIndex = _activeSourceIndex,
        IsGolfModeEnabled = IsGolfModeEnabled,
        LastCutTime = LastCutTime
    };
}

/// <summary>
/// Event args for program source changes.
/// </summary>
public class ProgramSourceChangedEventArgs : EventArgs
{
    public int PreviousSourceIndex { get; }
    public int NewSourceIndex { get; }
    public string Reason { get; }
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    public ProgramSourceChangedEventArgs(int previousSourceIndex, int newSourceIndex, string reason)
    {
        PreviousSourceIndex = previousSourceIndex;
        NewSourceIndex = newSourceIndex;
        Reason = reason;
    }
}
