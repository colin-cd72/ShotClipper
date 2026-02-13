namespace Screener.Golf.Models;

/// <summary>
/// Represents a single swing sequence with in/out points for clip extraction.
/// </summary>
public class SwingSequence
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? SessionId { get; set; }
    public int SequenceNumber { get; set; }

    /// <summary>Ticks when the swing was detected (cut to Source 2).</summary>
    public long InPointTicks { get; set; }

    /// <summary>Ticks when the ball landed (cut back to Source 1).</summary>
    public long? OutPointTicks { get; set; }

    /// <summary>How the swing was detected: 'manual' or 'auto'.</summary>
    public string DetectionMethod { get; set; } = "manual";

    /// <summary>Path to the exported clip file, once extracted.</summary>
    public string? ExportedClipPath { get; set; }

    /// <summary>Export status: pending, extracting, completed, failed.</summary>
    public string ExportStatus { get; set; } = "pending";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public TimeSpan? Duration => OutPointTicks.HasValue
        ? TimeSpan.FromTicks(OutPointTicks.Value - InPointTicks)
        : null;
}
