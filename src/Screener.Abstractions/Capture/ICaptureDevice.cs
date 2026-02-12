namespace Screener.Abstractions.Capture;

/// <summary>
/// Video input connector types.
/// </summary>
public enum VideoConnector
{
    Unknown = 0,
    SDI = 1,
    HDMI = 2,
    OpticalSDI = 4,
    Component = 8,
    Composite = 16,
    SVideo = 32,
    NDI = 64,
    SRT = 128
}

/// <summary>
/// Represents a video capture device (e.g., Blackmagic DeckLink card).
/// </summary>
public interface ICaptureDevice : IDisposable
{
    /// <summary>
    /// Unique identifier for this device.
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Current device status.
    /// </summary>
    DeviceStatus Status { get; }

    /// <summary>
    /// List of video modes supported by this device.
    /// </summary>
    IReadOnlyList<VideoMode> SupportedVideoModes { get; }

    /// <summary>
    /// Currently active video mode, or null if not capturing.
    /// </summary>
    VideoMode? CurrentVideoMode { get; }

    /// <summary>
    /// List of available video input connectors on this device.
    /// </summary>
    IReadOnlyList<VideoConnector> AvailableConnectors { get; }

    /// <summary>
    /// Currently selected video input connector.
    /// </summary>
    VideoConnector SelectedConnector { get; set; }

    /// <summary>
    /// Fired when a video frame is received.
    /// </summary>
    event EventHandler<VideoFrameEventArgs>? VideoFrameReceived;

    /// <summary>
    /// Fired when audio samples are received.
    /// </summary>
    event EventHandler<AudioSamplesEventArgs>? AudioSamplesReceived;

    /// <summary>
    /// Fired when the device status changes.
    /// </summary>
    event EventHandler<DeviceStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// Start capturing video and audio.
    /// </summary>
    /// <param name="mode">The video mode to capture.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if capture started successfully.</returns>
    Task<bool> StartCaptureAsync(VideoMode mode, CancellationToken ct = default);

    /// <summary>
    /// Stop capturing.
    /// </summary>
    Task StopCaptureAsync();
}

/// <summary>
/// Manages discovery and lifecycle of capture devices.
/// </summary>
public interface IDeviceManager : IDisposable
{
    /// <summary>
    /// List of currently available capture devices.
    /// </summary>
    IReadOnlyList<ICaptureDevice> AvailableDevices { get; }

    /// <summary>
    /// Fired when a new device is detected.
    /// </summary>
    event EventHandler<DeviceEventArgs>? DeviceArrived;

    /// <summary>
    /// Fired when a device is removed.
    /// </summary>
    event EventHandler<DeviceEventArgs>? DeviceRemoved;

    /// <summary>
    /// Refresh the list of available devices.
    /// </summary>
    void RefreshDevices();

    /// <summary>
    /// Get a specific device by ID.
    /// </summary>
    Task<ICaptureDevice?> GetDeviceAsync(string deviceId);
}

public enum DeviceStatus
{
    Idle,
    Initializing,
    Capturing,
    Error,
    Disconnected
}

public record VideoMode(
    int Width,
    int Height,
    FrameRate FrameRate,
    PixelFormat PixelFormat,
    bool IsInterlaced,
    string DisplayName);

public readonly record struct FrameRate(int Numerator, int Denominator)
{
    public double Value => (double)Numerator / Denominator;
    public override string ToString() => $"{Value:F2}fps";

    public static FrameRate Fps23_976 => new(24000, 1001);
    public static FrameRate Fps24 => new(24, 1);
    public static FrameRate Fps25 => new(25, 1);
    public static FrameRate Fps29_97 => new(30000, 1001);
    public static FrameRate Fps30 => new(30, 1);
    public static FrameRate Fps50 => new(50, 1);
    public static FrameRate Fps59_94 => new(60000, 1001);
    public static FrameRate Fps60 => new(60, 1);
}

public enum PixelFormat
{
    BGRA,
    BGRA8,
    RGBA8,
    UYVY,
    YUV422_8bit,
    YUV422_10bit,
    RGB10,
    ARGB8
}

public class VideoFrameEventArgs : EventArgs
{
    public required ReadOnlyMemory<byte> FrameData { get; init; }
    public required VideoMode Mode { get; init; }
    public required TimeSpan Timestamp { get; init; }
    public required long FrameNumber { get; init; }
}

public class AudioSamplesEventArgs : EventArgs
{
    public required ReadOnlyMemory<byte> SampleData { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
    public required TimeSpan Timestamp { get; init; }
}

public class DeviceStatusChangedEventArgs : EventArgs
{
    public required DeviceStatus OldStatus { get; init; }
    public required DeviceStatus NewStatus { get; init; }
    public string? ErrorMessage { get; init; }
}

public class DeviceEventArgs : EventArgs
{
    public required ICaptureDevice Device { get; init; }
}

/// <summary>
/// Represents a video frame for preview rendering.
/// </summary>
public record VideoFrame(
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int RowBytes,
    PixelFormat PixelFormat,
    double? FrameRate = null);

/// <summary>
/// Represents an audio frame for preview playback.
/// </summary>
public record AudioFrame(
    ReadOnlyMemory<byte> Data,
    int SampleRate,
    int Channels,
    int BitsPerSample);
