using Screener.Abstractions.Encoding;
using Screener.Abstractions.Timecode;

namespace Screener.Abstractions.Recording;

/// <summary>
/// Manages recording sessions.
/// </summary>
public interface IRecordingService
{
    /// <summary>
    /// Current recording state.
    /// </summary>
    RecordingState State { get; }

    /// <summary>
    /// The currently active recording session, or null if not recording.
    /// </summary>
    RecordingSession? CurrentSession { get; }

    /// <summary>
    /// Fired when the recording state changes.
    /// </summary>
    event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Fired periodically with recording progress.
    /// </summary>
    event EventHandler<RecordingProgressEventArgs>? Progress;

    /// <summary>
    /// Start a new recording session.
    /// </summary>
    Task<RecordingSession> StartRecordingAsync(RecordingOptions options, CancellationToken ct = default);

    /// <summary>
    /// Stop the current recording session.
    /// </summary>
    Task StopRecordingAsync();

    /// <summary>
    /// Pause the current recording session.
    /// </summary>
    Task PauseRecordingAsync();

    /// <summary>
    /// Resume a paused recording session.
    /// </summary>
    Task ResumeRecordingAsync();
}

public enum RecordingState
{
    Stopped,
    Starting,
    Recording,
    Paused,
    Stopping
}

public record RecordingOptions(
    string OutputDirectory,
    string FilenameTemplate,
    EncodingPreset Preset,
    string? Name = null,
    TimeSpan? MaxDuration = null,
    long? MaxFileSizeMb = null,
    List<InputConfiguration>? Inputs = null);

/// <summary>
/// Configuration for a single recording input.
/// </summary>
public record InputConfiguration(
    string DeviceId,
    string DisplayName,
    Capture.VideoConnector Connector,
    int InputIndex,
    bool Enabled = true)
{
    /// <summary>
    /// Gets the suffix to append to filenames for this input.
    /// </summary>
    public string FilenameSuffix => $"_input{InputIndex + 1}";
}

public class RecordingSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string FilePath { get; init; }
    public required DateTime StartTimeUtc { get; init; }
    public DateTime? EndTimeUtc { get; set; }
    public TimeSpan Duration => (EndTimeUtc ?? DateTime.UtcNow) - StartTimeUtc;
    public long FileSizeBytes { get; set; }
    public required EncodingPreset Preset { get; init; }
    public List<ClipMarker> Markers { get; } = [];
    public Smpte12MTimecode? StartTimecode { get; init; }

    /// <summary>
    /// For multi-input recordings, tracks each input's session.
    /// </summary>
    public List<InputRecordingSession> InputSessions { get; } = [];
}

/// <summary>
/// Tracks recording progress for a single input.
/// </summary>
public class InputRecordingSession
{
    public required InputConfiguration Input { get; init; }
    public required string FilePath { get; init; }
    public long FileSizeBytes { get; set; }
    public long FramesRecorded { get; set; }
    public int DroppedFrames { get; set; }
    public bool HasSignal { get; set; } = true;
}

public record ClipMarker(
    MarkerType Type,
    TimeSpan Position,
    Smpte12MTimecode Timecode,
    string? Name = null,
    DateTime CreatedAt = default)
{
    public DateTime CreatedAt { get; init; } = CreatedAt == default ? DateTime.UtcNow : CreatedAt;
}

public enum MarkerType
{
    In,
    Out,
    Chapter,
    Note
}

public class RecordingStateChangedEventArgs : EventArgs
{
    public required RecordingState OldState { get; init; }
    public required RecordingState NewState { get; init; }
    public RecordingSession? Session { get; init; }
}

public class RecordingProgressEventArgs : EventArgs
{
    public required RecordingSession Session { get; init; }
    public required TimeSpan Duration { get; init; }
    public required long FileSizeBytes { get; init; }
    public required long FramesRecorded { get; init; }
    public required int DroppedFrames { get; init; }
    public required double CurrentBitrateMbps { get; init; }
}
