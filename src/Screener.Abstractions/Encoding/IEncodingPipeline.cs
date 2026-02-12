using Screener.Abstractions.Capture;

namespace Screener.Abstractions.Encoding;

/// <summary>
/// Manages video/audio encoding to file.
/// </summary>
public interface IEncodingPipeline : IAsyncDisposable
{
    /// <summary>
    /// Current state of the encoding pipeline.
    /// </summary>
    EncodingState State { get; }

    /// <summary>
    /// Currently active encoding preset.
    /// </summary>
    EncodingPreset CurrentPreset { get; }

    /// <summary>
    /// Statistics about the current encoding session.
    /// </summary>
    EncodingStatistics Statistics { get; }

    /// <summary>
    /// Fired when encoding progress is made.
    /// </summary>
    event EventHandler<EncodingProgressEventArgs>? Progress;

    /// <summary>
    /// Fired when an encoding error occurs.
    /// </summary>
    event EventHandler<EncodingErrorEventArgs>? Error;

    /// <summary>
    /// Initialize the pipeline with the given configuration.
    /// </summary>
    Task InitializeAsync(EncodingConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Write a video frame to the encoder.
    /// </summary>
    Task<bool> WriteVideoFrameAsync(ReadOnlyMemory<byte> frame, TimeSpan pts, CancellationToken ct = default);

    /// <summary>
    /// Write audio samples to the encoder.
    /// </summary>
    Task<bool> WriteAudioSamplesAsync(ReadOnlyMemory<byte> samples, TimeSpan pts, CancellationToken ct = default);

    /// <summary>
    /// Finalize the encoding and close the output file.
    /// </summary>
    Task FinalizeAsync(CancellationToken ct = default);
}

public enum EncodingState
{
    Idle,
    Initializing,
    Encoding,
    Finalizing,
    Completed,
    Error
}

public record EncodingConfiguration(
    string OutputPath,
    VideoMode VideoMode,
    AudioFormat AudioFormat,
    EncodingPreset Preset,
    HardwareAcceleration HwAccel = HardwareAcceleration.Auto,
    bool UseFragmentedMp4 = true);

public record AudioFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample);

public record EncodingPreset(
    string Name,
    VideoCodec VideoCodec,
    int VideoBitrateMbps,
    int CrfValue,
    string Profile,
    AudioCodec AudioCodec,
    int AudioBitrateKbps)
{
    public static EncodingPreset Proxy => new("Proxy", VideoCodec.H264, 5, 28, "main", AudioCodec.AAC, 128);
    public static EncodingPreset Medium => new("Medium", VideoCodec.H264, 15, 23, "high", AudioCodec.AAC, 192);
    public static EncodingPreset High => new("High", VideoCodec.H265, 35, 20, "main", AudioCodec.AAC, 256);
    public static EncodingPreset Master => new("Master", VideoCodec.H265, 80, 18, "main10", AudioCodec.AAC, 320);

    public static IReadOnlyList<EncodingPreset> AllPresets => [Proxy, Medium, High, Master];
}

public enum VideoCodec
{
    H264,
    H265,
    ProRes,
    DNxHD
}

public enum AudioCodec
{
    AAC,
    PCM,
    FLAC
}

public enum HardwareAcceleration
{
    Auto,
    Nvenc,
    Qsv,
    Amf,
    Software
}

public record EncodingStatistics
{
    public long FramesEncoded { get; init; }
    public long BytesWritten { get; init; }
    public TimeSpan Duration { get; init; }
    public double AverageFrameRate { get; init; }
    public double AverageBitrateMbps { get; init; }
    public int DroppedFrames { get; init; }
}

public class EncodingProgressEventArgs : EventArgs
{
    public required long FrameNumber { get; init; }
    public required TimeSpan EncodedDuration { get; init; }
    public required double CurrentBitrateMbps { get; init; }
    public required long FileSizeBytes { get; init; }
}

public class EncodingErrorEventArgs : EventArgs
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
    public bool IsFatal { get; init; }
}
