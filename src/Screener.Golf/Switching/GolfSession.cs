using Microsoft.Extensions.Logging;
using Screener.Golf.Models;

namespace Screener.Golf.Switching;

/// <summary>
/// Manages the lifecycle of a golf session (start, active golfer, swing tracking, end).
/// </summary>
public class GolfSession
{
    private readonly ILogger<GolfSession> _logger;
    private GolfSessionInfo? _currentSession;

    public GolfSessionInfo? CurrentSession => _currentSession;
    public bool IsSessionActive => _currentSession?.IsActive == true;
    public int TotalSwings => _currentSession?.TotalSwings ?? 0;

    /// <summary>Fired when a session starts.</summary>
    public event EventHandler<GolfSessionInfo>? SessionStarted;

    /// <summary>Fired when a session ends.</summary>
    public event EventHandler<GolfSessionInfo>? SessionEnded;

    /// <summary>Fired when the swing count changes.</summary>
    public event EventHandler<int>? SwingCountChanged;

    public GolfSession(ILogger<GolfSession> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start a new golf session.
    /// </summary>
    public GolfSessionInfo StartSession(GolferProfile? golfer = null)
    {
        if (_currentSession?.IsActive == true)
        {
            _logger.LogWarning("Ending previous session before starting new one");
            EndSession();
        }

        _currentSession = new GolfSessionInfo
        {
            GolferId = golfer?.Id,
            GolferDisplayName = golfer?.EffectiveDisplayName,
            StartedAt = DateTimeOffset.UtcNow
        };

        _logger.LogInformation("Golf session started: {Id} (Golfer: {Golfer})",
            _currentSession.Id,
            _currentSession.GolferDisplayName ?? "Unknown");

        SessionStarted?.Invoke(this, _currentSession);
        return _currentSession;
    }

    /// <summary>
    /// End the current session.
    /// </summary>
    public GolfSessionInfo? EndSession()
    {
        if (_currentSession == null || !_currentSession.IsActive) return null;

        _currentSession.EndedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation("Golf session ended: {Id}. Total swings: {Swings}",
            _currentSession.Id, _currentSession.TotalSwings);

        SessionEnded?.Invoke(this, _currentSession);
        var session = _currentSession;
        _currentSession = null;
        return session;
    }

    /// <summary>
    /// Increment the swing counter.
    /// </summary>
    public void IncrementSwingCount()
    {
        if (_currentSession == null) return;
        _currentSession.TotalSwings++;
        SwingCountChanged?.Invoke(this, _currentSession.TotalSwings);
    }

    /// <summary>
    /// Decrement the swing counter (e.g., practice swing removal).
    /// </summary>
    public void DecrementSwingCount()
    {
        if (_currentSession == null || _currentSession.TotalSwings <= 0) return;
        _currentSession.TotalSwings--;
        SwingCountChanged?.Invoke(this, _currentSession.TotalSwings);
    }

    /// <summary>
    /// Set recording paths for the session.
    /// </summary>
    public void SetRecordingPaths(string? source1Path, string? source2Path)
    {
        if (_currentSession == null) return;
        _currentSession.Source1RecordingPath = source1Path;
        _currentSession.Source2RecordingPath = source2Path;
    }
}
