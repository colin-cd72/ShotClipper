namespace Screener.Golf.Models;

/// <summary>
/// A golfer profile for overlay and session tracking.
/// </summary>
public class GolferProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? PhotoPath { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Display name or "FirstName LastName".</summary>
    public string EffectiveDisplayName => DisplayName ?? $"{FirstName} {LastName}".Trim();
}
