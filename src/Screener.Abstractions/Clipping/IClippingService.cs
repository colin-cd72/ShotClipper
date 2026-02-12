using Screener.Abstractions.Encoding;
using Screener.Abstractions.Recording;
using Screener.Abstractions.Timecode;

namespace Screener.Abstractions.Clipping;

/// <summary>
/// Manages live clip marking and extraction during recording.
/// </summary>
public interface IClippingService
{
    /// <summary>
    /// Current markers in the active recording.
    /// </summary>
    IReadOnlyList<ClipMarker> Markers { get; }

    /// <summary>
    /// Pending in-point (not yet paired with out-point).
    /// </summary>
    ClipMarker? PendingInPoint { get; }

    /// <summary>
    /// Fired when a marker is added.
    /// </summary>
    event EventHandler<ClipMarkerEventArgs>? MarkerAdded;

    /// <summary>
    /// Fired when clip extraction progress updates.
    /// </summary>
    event EventHandler<ClipExtractionProgressEventArgs>? ExtractionProgress;

    /// <summary>
    /// Set an in-point at the current position.
    /// </summary>
    ClipMarker SetInPoint(TimeSpan position, Smpte12MTimecode timecode, string? name = null);

    /// <summary>
    /// Set an out-point at the current position.
    /// </summary>
    ClipMarker SetOutPoint(TimeSpan position, Smpte12MTimecode timecode, string? name = null);

    /// <summary>
    /// Add a chapter marker.
    /// </summary>
    ClipMarker AddChapterMarker(TimeSpan position, Smpte12MTimecode timecode, string name);

    /// <summary>
    /// Create a clip definition from in/out points.
    /// </summary>
    Task<ClipDefinition> CreateClipAsync(string name, TimeSpan inPoint, TimeSpan outPoint, CancellationToken ct = default);

    /// <summary>
    /// Extract a clip from a recording file.
    /// </summary>
    Task<string> ExtractClipAsync(ClipDefinition clip, ClipExtractionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Clear all markers (for new recording).
    /// </summary>
    void ClearMarkers();
}

public record ClipDefinition(
    Guid Id,
    string Name,
    TimeSpan InPoint,
    TimeSpan OutPoint,
    string SourceFilePath,
    Dictionary<string, string>? Metadata = null)
{
    public TimeSpan Duration => OutPoint - InPoint;
}

public record ClipExtractionOptions(
    string OutputDirectory,
    string FilenameTemplate,
    EncodingPreset? TranscodePreset = null,  // null = stream copy (fast)
    bool AddTimecodeOverlay = false);

public class ClipMarkerEventArgs : EventArgs
{
    public required ClipMarker Marker { get; init; }
}

public class ClipExtractionProgressEventArgs : EventArgs
{
    public required ClipDefinition Clip { get; init; }
    public required double Progress { get; init; }
    public required ClipExtractionStatus Status { get; init; }
    public string? OutputPath { get; init; }
    public string? ErrorMessage { get; init; }
}

public enum ClipExtractionStatus
{
    Queued,
    Extracting,
    Transcoding,
    Completed,
    Failed
}
