using Screener.Abstractions.Capture;

namespace Screener.Abstractions.Timecode;

/// <summary>
/// Provides time information for timecode generation.
/// </summary>
public interface ITimecodeProvider
{
    /// <summary>
    /// Name of this time provider (e.g., "NTP", "System", "Manual").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether this provider is currently available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The offset from the local system clock (positive = ahead of local).
    /// </summary>
    TimeSpan Offset { get; }

    /// <summary>
    /// Get the current time from this provider.
    /// </summary>
    Task<DateTimeOffset> GetCurrentTimeAsync(CancellationToken ct = default);

    /// <summary>
    /// Generate a SMPTE timecode from the given time.
    /// </summary>
    Smpte12MTimecode GetTimecode(DateTimeOffset time, FrameRate frameRate, bool dropFrame = false);
}

/// <summary>
/// Manages timecode generation with configurable sources.
/// </summary>
public interface ITimecodeService
{
    /// <summary>
    /// The currently active time provider.
    /// </summary>
    ITimecodeProvider CurrentProvider { get; }

    /// <summary>
    /// Available time providers.
    /// </summary>
    IReadOnlyList<ITimecodeProvider> AvailableProviders { get; }

    /// <summary>
    /// Current timezone for time-of-day timecode.
    /// </summary>
    TimeZoneInfo Timezone { get; set; }

    /// <summary>
    /// Whether to use drop-frame timecode for 29.97/59.94 fps.
    /// </summary>
    bool UseDropFrame { get; set; }

    /// <summary>
    /// Set the active time provider.
    /// </summary>
    void SetProvider(string providerName);

    /// <summary>
    /// Get the current timecode.
    /// </summary>
    Task<Smpte12MTimecode> GetCurrentTimecodeAsync(FrameRate frameRate, CancellationToken ct = default);

    /// <summary>
    /// Set a manual timecode offset.
    /// </summary>
    void SetManualOffset(TimeSpan offset);
}

/// <summary>
/// Represents a SMPTE 12M timecode value.
/// </summary>
public readonly record struct Smpte12MTimecode(
    int Hours,
    int Minutes,
    int Seconds,
    int Frames,
    bool DropFrame = false)
{
    public override string ToString()
    {
        var separator = DropFrame ? ';' : ':';
        return $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}{separator}{Frames:D2}";
    }

    /// <summary>
    /// Convert to total frames.
    /// </summary>
    public long ToTotalFrames(FrameRate frameRate)
    {
        var fps = (int)Math.Round(frameRate.Value);
        var totalSeconds = Hours * 3600 + Minutes * 60 + Seconds;
        var totalFrames = totalSeconds * fps + Frames;

        if (DropFrame && (fps == 30 || fps == 60))
        {
            // Drop frame adjustment for 29.97/59.94
            var dropFramesPerMinute = fps == 30 ? 2 : 4;
            var totalMinutes = Hours * 60 + Minutes;
            var droppedFrames = dropFramesPerMinute * (totalMinutes - totalMinutes / 10);
            totalFrames -= droppedFrames;
        }

        return totalFrames;
    }

    /// <summary>
    /// Create from total frames.
    /// </summary>
    public static Smpte12MTimecode FromTotalFrames(long totalFrames, FrameRate frameRate, bool dropFrame = false)
    {
        var fps = (int)Math.Round(frameRate.Value);

        if (dropFrame && (fps == 30 || fps == 60))
        {
            // Drop frame calculation
            var dropFramesPerMinute = fps == 30 ? 2 : 4;
            var framesPerMinute = fps * 60 - dropFramesPerMinute;
            var framesPerTenMinutes = framesPerMinute * 10 + dropFramesPerMinute;

            var tenMinuteBlocks = totalFrames / framesPerTenMinutes;
            var remainingFrames = totalFrames % framesPerTenMinutes;

            var adjustedMinutes = (int)(tenMinuteBlocks * 10);
            if (remainingFrames >= dropFramesPerMinute)
            {
                adjustedMinutes += (int)((remainingFrames - dropFramesPerMinute) / framesPerMinute) + 1;
                remainingFrames = (remainingFrames - dropFramesPerMinute) % framesPerMinute + dropFramesPerMinute;
            }

            var hours = adjustedMinutes / 60;
            var minutes = adjustedMinutes % 60;
            var seconds = (int)(remainingFrames / fps);
            var frames = (int)(remainingFrames % fps);

            return new Smpte12MTimecode(hours, minutes, seconds, frames, true);
        }

        var totalSeconds = totalFrames / fps;
        var h = (int)(totalSeconds / 3600) % 24;
        var m = (int)(totalSeconds / 60) % 60;
        var s = (int)(totalSeconds % 60);
        var f = (int)(totalFrames % fps);

        return new Smpte12MTimecode(h, m, s, f, false);
    }

    /// <summary>
    /// Create from TimeSpan.
    /// </summary>
    public static Smpte12MTimecode FromTimeSpan(TimeSpan time, FrameRate frameRate, bool dropFrame = false)
    {
        var totalFrames = (long)(time.TotalSeconds * frameRate.Value);
        return FromTotalFrames(totalFrames, frameRate, dropFrame);
    }

    /// <summary>
    /// Convert to TimeSpan.
    /// </summary>
    public TimeSpan ToTimeSpan(FrameRate frameRate)
    {
        var totalFrames = ToTotalFrames(frameRate);
        return TimeSpan.FromSeconds(totalFrames / frameRate.Value);
    }

    /// <summary>
    /// Parse from string format "HH:MM:SS:FF" or "HH:MM:SS;FF".
    /// </summary>
    public static Smpte12MTimecode Parse(string timecode)
    {
        var dropFrame = timecode.Contains(';');
        var separator = dropFrame ? ';' : ':';
        var parts = timecode.Replace(';', ':').Split(':');

        if (parts.Length != 4)
            throw new FormatException($"Invalid timecode format: {timecode}");

        return new Smpte12MTimecode(
            int.Parse(parts[0]),
            int.Parse(parts[1]),
            int.Parse(parts[2]),
            int.Parse(parts[3]),
            dropFrame);
    }

    public static bool TryParse(string timecode, out Smpte12MTimecode result)
    {
        try
        {
            result = Parse(timecode);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }
}
