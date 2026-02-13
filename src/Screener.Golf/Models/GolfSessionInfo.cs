namespace Screener.Golf.Models;

/// <summary>
/// Information about an active or completed golf session.
/// </summary>
public class GolfSessionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? GolferId { get; set; }
    public string? GolferDisplayName { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public string? Source1RecordingPath { get; set; }
    public string? Source2RecordingPath { get; set; }
    public int TotalSwings { get; set; }
    public bool IsActive => EndedAt == null;
}
