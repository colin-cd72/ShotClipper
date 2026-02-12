using Screener.Abstractions.Capture;

namespace Screener.Abstractions.Output;

/// <summary>
/// State of an output service.
/// </summary>
public enum OutputState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

/// <summary>
/// Configuration for starting an output.
/// </summary>
public record OutputConfiguration(
    string Name,
    int Width,
    int Height,
    double FrameRate,
    Dictionary<string, string>? Parameters = null);

/// <summary>
/// Event args for output state changes.
/// </summary>
public class OutputStateChangedEventArgs : EventArgs
{
    public required OutputState OldState { get; init; }
    public required OutputState NewState { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Interface for NDI/SRT output services (separate from IStreamingService which handles WebSocket streaming).
/// </summary>
public interface IOutputService : IAsyncDisposable
{
    /// <summary>
    /// Unique identifier for this output.
    /// </summary>
    string OutputId { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Current output state.
    /// </summary>
    OutputState State { get; }

    /// <summary>
    /// Current configuration, or null if not started.
    /// </summary>
    OutputConfiguration? CurrentConfig { get; }

    /// <summary>
    /// Fired when the output state changes.
    /// </summary>
    event EventHandler<OutputStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Start the output with the given configuration.
    /// </summary>
    Task StartAsync(OutputConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Stop the output.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Push a video frame to the output.
    /// </summary>
    Task PushFrameAsync(ReadOnlyMemory<byte> frameData, VideoMode mode, TimeSpan timestamp, CancellationToken ct = default);

    /// <summary>
    /// Push audio samples to the output.
    /// </summary>
    Task PushAudioAsync(ReadOnlyMemory<byte> audioData, int sampleRate, int channels, int bitsPerSample, CancellationToken ct = default);
}
