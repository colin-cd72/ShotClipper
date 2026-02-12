namespace Screener.Abstractions.Streaming;

/// <summary>
/// Manages WebRTC streaming for local network preview.
/// </summary>
public interface IStreamingService : IAsyncDisposable
{
    /// <summary>
    /// Current streaming state.
    /// </summary>
    StreamingState State { get; }

    /// <summary>
    /// Currently connected viewers.
    /// </summary>
    IReadOnlyList<StreamingClient> ConnectedClients { get; }

    /// <summary>
    /// The signaling server URI for viewer connections.
    /// </summary>
    Uri? SignalingUri { get; }

    /// <summary>
    /// Fired when a client connects.
    /// </summary>
    event EventHandler<StreamingClientEventArgs>? ClientConnected;

    /// <summary>
    /// Fired when a client disconnects.
    /// </summary>
    event EventHandler<StreamingClientEventArgs>? ClientDisconnected;

    /// <summary>
    /// Start the streaming server.
    /// </summary>
    Task StartAsync(StreamingConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Stop the streaming server.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Push a video frame to all connected clients.
    /// </summary>
    Task PushFrameAsync(ReadOnlyMemory<byte> frameData, TimeSpan timestamp, CancellationToken ct = default);

    /// <summary>
    /// Get a QR code image for the connection URL.
    /// </summary>
    byte[] GenerateConnectionQrCode();

    /// <summary>
    /// Disconnect a specific client.
    /// </summary>
    Task DisconnectClientAsync(string clientId);

    /// <summary>
    /// Update streaming quality settings.
    /// </summary>
    Task UpdateQualityAsync(StreamQualitySettings quality, CancellationToken ct = default);
}

public enum StreamingState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

public record StreamingConfiguration(
    int SignalingPort,
    StreamQualitySettings Quality,
    int MaxClients = 10,
    string? AccessToken = null);

public record StreamQualitySettings(
    int MaxWidth,
    int MaxHeight,
    int MaxFrameRate,
    int MaxBitrateKbps,
    StreamVideoCodec Codec = StreamVideoCodec.VP8);

public enum StreamVideoCodec
{
    VP8,
    VP9,
    H264
}

public record StreamingClient(
    string Id,
    string RemoteAddress,
    DateTimeOffset ConnectedAt,
    StreamQualitySettings NegotiatedQuality);

public class StreamingClientEventArgs : EventArgs
{
    public required StreamingClient Client { get; init; }
}
