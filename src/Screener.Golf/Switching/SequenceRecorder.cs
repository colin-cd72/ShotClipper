using Microsoft.Extensions.Logging;
using Screener.Golf.Models;

namespace Screener.Golf.Switching;

/// <summary>
/// Listens to switcher cut events and records swing sequences (in/out points).
/// Each swing becomes a SwingSequence that can be exported as a clip.
/// </summary>
public class SequenceRecorder
{
    private readonly ILogger<SequenceRecorder> _logger;
    private readonly SwitcherService _switcherService;
    private readonly List<SwingSequence> _sequences = new();

    private SwingSequence? _activeSequence;
    private int _sequenceCounter;
    private string? _sessionId;

    public IReadOnlyList<SwingSequence> Sequences => _sequences.ToList();
    public SwingSequence? ActiveSequence => _activeSequence;
    public int SequenceCount => _sequences.Count;

    /// <summary>Fired when a new swing sequence is started.</summary>
    public event EventHandler<SwingSequence>? SequenceStarted;

    /// <summary>Fired when a swing sequence is completed (out point set).</summary>
    public event EventHandler<SwingSequence>? SequenceCompleted;

    public SequenceRecorder(ILogger<SequenceRecorder> logger, SwitcherService switcherService)
    {
        _logger = logger;
        _switcherService = switcherService;

        // Subscribe to switcher cut events
        _switcherService.ProgramSourceChanged += OnProgramSourceChanged;
    }

    /// <summary>
    /// Start tracking sequences for a session.
    /// </summary>
    public void StartSession(string sessionId)
    {
        _sessionId = sessionId;
        _sequenceCounter = 0;
        _sequences.Clear();
        _activeSequence = null;
        _logger.LogInformation("Sequence recorder started for session {SessionId}", sessionId);
    }

    /// <summary>
    /// Stop tracking sequences.
    /// </summary>
    public void StopSession()
    {
        // If there's an active sequence without an out point, close it
        if (_activeSequence != null && !_activeSequence.OutPointTicks.HasValue)
        {
            _activeSequence.OutPointTicks = DateTimeOffset.UtcNow.Ticks;
            _activeSequence.ExportStatus = "pending";
            _logger.LogInformation("Closing active sequence #{Num} at session end",
                _activeSequence.SequenceNumber);
            SequenceCompleted?.Invoke(this, _activeSequence);
        }

        _activeSequence = null;
        _sessionId = null;
        _logger.LogInformation("Sequence recorder stopped. Total sequences: {Count}", _sequences.Count);
    }

    private void OnProgramSourceChanged(object? sender, ProgramSourceChangedEventArgs e)
    {
        if (_sessionId == null) return;

        // Cut to Source 2 (simulator): mark in-point (swing start)
        if (e.NewSourceIndex == 1 && e.PreviousSourceIndex == 0)
        {
            _sequenceCounter++;
            _activeSequence = new SwingSequence
            {
                SessionId = _sessionId,
                SequenceNumber = _sequenceCounter,
                InPointTicks = e.Timestamp.Ticks,
                DetectionMethod = e.Reason == "manual" ? "manual" : "auto"
            };
            _sequences.Add(_activeSequence);

            _logger.LogInformation("Swing #{Num} started (reason: {Reason})",
                _sequenceCounter, e.Reason);
            SequenceStarted?.Invoke(this, _activeSequence);
        }
        // Cut to Source 1 (golfer cam): mark out-point (swing end)
        else if (e.NewSourceIndex == 0 && e.PreviousSourceIndex == 1 && _activeSequence != null)
        {
            // Don't mark practice swings as complete
            if (e.Reason == "practice_swing")
            {
                _logger.LogInformation("Practice swing #{Num} discarded", _activeSequence.SequenceNumber);
                _sequences.Remove(_activeSequence);
                _sequenceCounter--;
                _activeSequence = null;
                return;
            }

            _activeSequence.OutPointTicks = e.Timestamp.Ticks;
            _activeSequence.ExportStatus = "pending";

            _logger.LogInformation("Swing #{Num} completed. Duration: {Duration:F1}s (reason: {Reason})",
                _activeSequence.SequenceNumber,
                _activeSequence.Duration?.TotalSeconds ?? 0,
                e.Reason);
            SequenceCompleted?.Invoke(this, _activeSequence);
            _activeSequence = null;
        }
    }

    /// <summary>
    /// Unsubscribe from events.
    /// </summary>
    public void Dispose()
    {
        _switcherService.ProgramSourceChanged -= OnProgramSourceChanged;
    }
}
