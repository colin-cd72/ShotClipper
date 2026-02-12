using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QRCoder;
using Screener.Abstractions.Streaming;

namespace Screener.Streaming;

/// <summary>
/// WebRTC streaming service for local network video preview.
/// </summary>
public sealed class WebRtcStreamingService : IStreamingService
{
    private readonly ILogger<WebRtcStreamingService> _logger;
    private readonly ConcurrentDictionary<string, ViewerSession> _viewers = new();

    private HttpListener? _httpListener;
    private StreamingConfiguration? _config;
    private StreamingState _state = StreamingState.Stopped;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public StreamingState State => _state;
    public IReadOnlyList<StreamingClient> ConnectedClients => _viewers.Values.Select(v => v.ToClient()).ToList();
    public Uri? SignalingUri { get; private set; }

    public event EventHandler<StreamingClientEventArgs>? ClientConnected;
    public event EventHandler<StreamingClientEventArgs>? ClientDisconnected;

    public WebRtcStreamingService(ILogger<WebRtcStreamingService> logger)
    {
        _logger = logger;
    }

    public async Task StartAsync(StreamingConfiguration config, CancellationToken ct = default)
    {
        if (_state != StreamingState.Stopped)
            throw new InvalidOperationException($"Cannot start in state {_state}");

        _state = StreamingState.Starting;
        _config = config;

        try
        {
            var localIp = GetLocalIpAddress();
            string boundHost;

            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://+:{config.SignalingPort}/");

            try
            {
                _httpListener.Start();
                boundHost = localIp;
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5) // Access denied
            {
                // Try binding to specific IP so LAN clients can connect
                _httpListener = new HttpListener();
                try
                {
                    _httpListener.Prefixes.Add($"http://{localIp}:{config.SignalingPort}/");
                    _httpListener.Start();
                    boundHost = localIp;
                }
                catch (HttpListenerException)
                {
                    // Fall back to localhost only
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://localhost:{config.SignalingPort}/");
                    _httpListener.Start();
                    boundHost = "localhost";
                }
            }

            SignalingUri = new Uri($"http://{boundHost}:{config.SignalingPort}/stream");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listenerTask = AcceptConnectionsAsync(_cts.Token);

            _state = StreamingState.Running;

            _logger.LogInformation("WebRTC streaming started at {Uri}", SignalingUri);
        }
        catch (Exception ex)
        {
            _state = StreamingState.Error;
            _logger.LogError(ex, "Failed to start WebRTC streaming");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_state != StreamingState.Running && _state != StreamingState.Error)
            return;

        _state = StreamingState.Stopping;

        _cts?.Cancel();

        // Disconnect all viewers
        foreach (var viewer in _viewers.Values)
        {
            await viewer.DisconnectAsync();
        }
        _viewers.Clear();

        _httpListener?.Stop();
        _httpListener?.Close();

        if (_listenerTask != null)
        {
            try { await _listenerTask; } catch { }
        }

        SignalingUri = null;
        _state = StreamingState.Stopped;

        _logger.LogInformation("WebRTC streaming stopped");
    }

    // UYVY→JPEG encoding state
    private int _streamWidth;
    private int _streamHeight;
    private volatile bool _encoding;

    public async Task PushFrameAsync(ReadOnlyMemory<byte> frameData, TimeSpan timestamp, CancellationToken ct = default)
    {
        if (_state != StreamingState.Running || _viewers.IsEmpty || _config == null)
            return;

        // Skip if still encoding the previous frame
        if (_encoding)
            return;
        _encoding = true;

        try
        {
            var quality = _config.Quality;
            // Source is 1920x1080 UYVY; compute target size
            int srcWidth = 1920;
            int srcHeight = 1080;
            int srcRowBytes = frameData.Length / srcHeight;
            if (srcRowBytes < srcWidth * 2)
            {
                _encoding = false;
                return;
            }

            int dstWidth = quality.MaxWidth;
            int dstHeight = quality.MaxHeight;
            int divisor = Math.Max(1, srcWidth / dstWidth);
            dstWidth = srcWidth / divisor;
            dstHeight = srcHeight / divisor;

            byte[]? jpegBytes = null;

            await Task.Run(() =>
            {
                var srcBytes = frameData.Span;
                using var bmp = new Bitmap(dstWidth, dstHeight, PixelFormat.Format24bppRgb);
                var bmpData = bmp.LockBits(new Rectangle(0, 0, dstWidth, dstHeight),
                    ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

                try
                {
                    int dstStride = bmpData.Stride;
                    unsafe
                    {
                        byte* dstPtr = (byte*)bmpData.Scan0;

                        for (int dstRow = 0; dstRow < dstHeight; dstRow++)
                        {
                            int srcRow = dstRow * divisor;
                            int srcRowStart = srcRow * srcRowBytes;
                            int dstRowStart = dstRow * dstStride;

                            for (int dstCol = 0; dstCol < dstWidth - 1; dstCol += 2)
                            {
                                int srcCol = dstCol * divisor;
                                int uyvyIndex = srcRowStart + srcCol * 2;

                                if (uyvyIndex + 3 >= srcBytes.Length) break;

                                int u = srcBytes[uyvyIndex];
                                int y0 = srcBytes[uyvyIndex + 1];
                                int v = srcBytes[uyvyIndex + 2];
                                int y1 = srcBytes[uyvyIndex + 3];

                                // YUV→RGB (BT.601)
                                int c0 = 298 * (y0 - 16);
                                int c1 = 298 * (y1 - 16);
                                int d = u - 128;
                                int e = v - 128;

                                int r0 = (c0 + 409 * e + 128) >> 8;
                                int g0 = (c0 - 100 * d - 208 * e + 128) >> 8;
                                int b0 = (c0 + 516 * d + 128) >> 8;

                                int r1 = (c1 + 409 * e + 128) >> 8;
                                int g1 = (c1 - 100 * d - 208 * e + 128) >> 8;
                                int b1 = (c1 + 516 * d + 128) >> 8;

                                int idx0 = dstRowStart + dstCol * 3;
                                dstPtr[idx0] = (byte)Math.Clamp(b0, 0, 255);
                                dstPtr[idx0 + 1] = (byte)Math.Clamp(g0, 0, 255);
                                dstPtr[idx0 + 2] = (byte)Math.Clamp(r0, 0, 255);

                                int idx1 = idx0 + 3;
                                dstPtr[idx1] = (byte)Math.Clamp(b1, 0, 255);
                                dstPtr[idx1 + 1] = (byte)Math.Clamp(g1, 0, 255);
                                dstPtr[idx1 + 2] = (byte)Math.Clamp(r1, 0, 255);
                            }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bmpData);
                }

                // Encode to JPEG
                using var ms = new MemoryStream();
                var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 60L);
                bmp.Save(ms, jpegEncoder, encoderParams);
                jpegBytes = ms.ToArray();
            }, ct);

            if (jpegBytes == null || jpegBytes.Length == 0)
            {
                _encoding = false;
                return;
            }

            // Send to all connected viewers
            var sendTasks = new List<Task>();
            foreach (var viewer in _viewers.Values)
            {
                if (viewer.WebSocket.State == WebSocketState.Open)
                {
                    sendTasks.Add(viewer.WebSocket.SendAsync(
                        jpegBytes, WebSocketMessageType.Binary, true, ct));
                    viewer.FrameCount++;
                }
            }

            if (sendTasks.Count > 0)
                await Task.WhenAll(sendTasks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error encoding/sending frame");
        }
        finally
        {
            _encoding = false;
        }
    }

    public byte[] GenerateConnectionQrCode()
    {
        if (SignalingUri == null)
            return Array.Empty<byte>();

        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(SignalingUri.ToString(), QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);

        return qrCode.GetGraphic(10);
    }

    public async Task DisconnectClientAsync(string clientId)
    {
        if (_viewers.TryRemove(clientId, out var viewer))
        {
            await viewer.DisconnectAsync();

            ClientDisconnected?.Invoke(this, new StreamingClientEventArgs
            {
                Client = viewer.ToClient()
            });
        }
    }

    public Task UpdateQualityAsync(StreamQualitySettings quality, CancellationToken ct = default)
    {
        if (_config != null)
        {
            _config = _config with { Quality = quality };
        }

        return Task.CompletedTask;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener?.IsListening == true)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();

                _ = HandleRequestAsync(context, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accepting connection");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        try
        {
            if (path == "/stream" || path == "/")
            {
                // Serve viewer HTML page
                await ServeViewerPageAsync(context);
            }
            else if (path == "/ws" && context.Request.IsWebSocketRequest)
            {
                // Validate access token if configured
                if (!ValidateAccessToken(context))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    return;
                }

                // Check max clients
                if (_viewers.Count >= _config?.MaxClients)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }

                // Accept WebSocket connection
                var wsContext = await context.AcceptWebSocketAsync(null);
                await HandleViewerAsync(wsContext.WebSocket, context.Request.RemoteEndPoint?.ToString() ?? "unknown", ct);
            }
            else if (path == "/qr")
            {
                // Serve QR code image
                var qrBytes = GenerateConnectionQrCode();
                context.Response.ContentType = "image/png";
                context.Response.ContentLength64 = qrBytes.Length;
                await context.Response.OutputStream.WriteAsync(qrBytes, ct);
                context.Response.Close();
            }
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling request");
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleViewerAsync(WebSocket webSocket, string remoteAddress, CancellationToken ct)
    {
        var viewerId = Guid.NewGuid().ToString("N")[..8];
        var viewer = new ViewerSession(viewerId, remoteAddress, webSocket);
        _viewers[viewerId] = viewer;

        _logger.LogInformation("Viewer connected: {ViewerId} from {Address}", viewerId, remoteAddress);

        ClientConnected?.Invoke(this, new StreamingClientEventArgs { Client = viewer.ToClient() });

        try
        {
            // In a real implementation, this would:
            // 1. Create RTCPeerConnection
            // 2. Add video/audio tracks
            // 3. Handle SDP offer/answer exchange
            // 4. Handle ICE candidate exchange

            // For now, send a simple handshake
            await SendJsonAsync(webSocket, new { type = "hello", viewerId, maxBitrate = _config?.Quality.MaxBitrateKbps }, ct);

            // Listen for messages
            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleSignalingMessageAsync(viewer, message, ct);
                }
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _viewers.TryRemove(viewerId, out _);

            ClientDisconnected?.Invoke(this, new StreamingClientEventArgs { Client = viewer.ToClient() });

            _logger.LogInformation("Viewer disconnected: {ViewerId}", viewerId);
        }
    }

    private async Task HandleSignalingMessageAsync(ViewerSession viewer, string message, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "offer":
                    // In a real implementation, set remote description and create answer
                    _logger.LogDebug("Received offer from {ViewerId}", viewer.Id);
                    break;

                case "answer":
                    _logger.LogDebug("Received answer from {ViewerId}", viewer.Id);
                    break;

                case "ice-candidate":
                    _logger.LogDebug("Received ICE candidate from {ViewerId}", viewer.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse signaling message");
        }
    }

    private async Task SendJsonAsync(WebSocket webSocket, object message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private bool ValidateAccessToken(HttpListenerContext context)
    {
        if (string.IsNullOrEmpty(_config?.AccessToken))
            return true;

        var token = context.Request.QueryString["token"];
        return token == _config.AccessToken;
    }

    private async Task ServeViewerPageAsync(HttpListenerContext context)
    {
        var html = GetViewerHtml();
        var bytes = Encoding.UTF8.GetBytes(html);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private string GetViewerHtml() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Screener - Live Stream</title>
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body {
                    background: radial-gradient(ellipse at center, #222 0%, #0a0a0a 100%);
                    color: #fff;
                    font-family: Inter, 'SF Pro Display', system-ui, -apple-system, sans-serif;
                    overflow: hidden;
                    width: 100vw;
                    height: 100vh;
                    cursor: default;
                }
                body.hide-ui { cursor: none; }

                #frame {
                    width: 100vw;
                    height: 100vh;
                    object-fit: contain;
                    background: transparent;
                    display: block;
                }

                .overlay {
                    position: fixed;
                    transition: opacity 0.5s ease;
                    z-index: 10;
                }
                body.hide-ui .overlay { opacity: 0; pointer-events: none; }

                /* Status badge — top-left */
                #status {
                    top: 20px;
                    left: 20px;
                    padding: 8px 16px;
                    background: rgba(20, 20, 20, 0.6);
                    backdrop-filter: blur(8px);
                    -webkit-backdrop-filter: blur(8px);
                    border: 1px solid rgba(255,255,255,0.08);
                    border-radius: 20px;
                    font-size: 13px;
                    font-weight: 500;
                    display: flex;
                    align-items: center;
                    gap: 8px;
                    letter-spacing: 0.02em;
                }

                .status-dot {
                    width: 8px;
                    height: 8px;
                    border-radius: 50%;
                    background: #666;
                    flex-shrink: 0;
                }
                .status-dot.live {
                    background: #4CAF50;
                    animation: pulse 2s ease-in-out infinite;
                }
                .status-dot.error { background: #F44336; }
                .status-dot.connecting {
                    background: #FFC107;
                    animation: pulse 1s ease-in-out infinite;
                }

                @keyframes pulse {
                    0%, 100% { opacity: 1; box-shadow: 0 0 0 0 currentColor; }
                    50% { opacity: 0.6; box-shadow: 0 0 8px 2px currentColor; }
                }

                /* Info overlay — bottom-left */
                #info {
                    bottom: 20px;
                    left: 20px;
                    padding: 10px 16px;
                    background: rgba(20, 20, 20, 0.6);
                    backdrop-filter: blur(8px);
                    -webkit-backdrop-filter: blur(8px);
                    border: 1px solid rgba(255,255,255,0.08);
                    border-radius: 10px;
                    font-size: 12px;
                    color: #aaa;
                    display: flex;
                    flex-direction: column;
                    gap: 3px;
                    min-width: 180px;
                }
                #info .label { color: #666; font-size: 10px; text-transform: uppercase; letter-spacing: 0.05em; }
                #info .value { color: #ddd; font-variant-numeric: tabular-nums; }

                /* Fullscreen hint */
                #hint {
                    bottom: 20px;
                    right: 20px;
                    padding: 6px 12px;
                    background: rgba(20, 20, 20, 0.5);
                    backdrop-filter: blur(8px);
                    border: 1px solid rgba(255,255,255,0.06);
                    border-radius: 6px;
                    font-size: 11px;
                    color: #555;
                }
            </style>
        </head>
        <body>
            <div id="status" class="overlay">
                <span class="status-dot connecting" id="dot"></span>
                <span id="statusText">Connecting...</span>
            </div>

            <img id="frame" alt="Stream"/>

            <div id="info" class="overlay">
                <div><span class="label">Resolution </span><span class="value" id="res">—</span></div>
                <div><span class="label">FPS </span><span class="value" id="fps">—</span></div>
                <div><span class="label">Viewer </span><span class="value" id="viewer">—</span></div>
                <div><span class="label">Connected </span><span class="value" id="elapsed">—</span></div>
            </div>

            <div id="hint" class="overlay">Press <b>F</b> or click for fullscreen</div>

            <script>
                const frame = document.getElementById('frame');
                const dot = document.getElementById('dot');
                const statusText = document.getElementById('statusText');
                const resEl = document.getElementById('res');
                const fpsEl = document.getElementById('fps');
                const viewerEl = document.getElementById('viewer');
                const elapsedEl = document.getElementById('elapsed');
                const hintEl = document.getElementById('hint');

                const wsUrl = `ws://${location.host}/ws${location.search}`;
                let ws, reconnectTimer, objectUrl = null;
                let frameCount = 0, lastFpsTime = performance.now(), currentFps = 0;
                let connectedTime = null, viewerId = null;
                let hideTimer = null;

                // Auto-hide UI after 3s of inactivity
                function resetHideTimer() {
                    document.body.classList.remove('hide-ui');
                    clearTimeout(hideTimer);
                    hideTimer = setTimeout(() => document.body.classList.add('hide-ui'), 3000);
                }
                document.addEventListener('mousemove', resetHideTimer);
                document.addEventListener('mousedown', resetHideTimer);
                resetHideTimer();

                // Fullscreen toggle
                function toggleFullscreen() {
                    if (!document.fullscreenElement) {
                        document.documentElement.requestFullscreen().catch(() => {});
                    } else {
                        document.exitFullscreen();
                    }
                }
                frame.addEventListener('click', toggleFullscreen);
                document.addEventListener('keydown', (e) => {
                    if (e.key === 'f' || e.key === 'F') toggleFullscreen();
                });

                // Connection elapsed time
                function formatElapsed(ms) {
                    const s = Math.floor(ms / 1000);
                    const m = Math.floor(s / 60);
                    const h = Math.floor(m / 60);
                    if (h > 0) return `${h}h ${m%60}m ${s%60}s`;
                    if (m > 0) return `${m}m ${s%60}s`;
                    return `${s}s`;
                }

                setInterval(() => {
                    if (connectedTime) {
                        elapsedEl.textContent = formatElapsed(Date.now() - connectedTime);
                    }
                }, 1000);

                // Hide hint after 5s
                setTimeout(() => { hintEl.style.opacity = '0'; }, 5000);

                function connect() {
                    ws = new WebSocket(wsUrl);
                    ws.binaryType = 'blob';

                    ws.onopen = () => {
                        statusText.textContent = 'Connected';
                        dot.className = 'status-dot connecting';
                        connectedTime = Date.now();
                    };

                    ws.onclose = () => {
                        statusText.textContent = 'Reconnecting...';
                        dot.className = 'status-dot connecting';
                        reconnectTimer = setTimeout(connect, 3000);
                    };

                    ws.onerror = () => {
                        statusText.textContent = 'Connection Error';
                        dot.className = 'status-dot error';
                    };

                    ws.onmessage = (e) => {
                        if (e.data instanceof Blob) {
                            if (objectUrl) URL.revokeObjectURL(objectUrl);
                            objectUrl = URL.createObjectURL(e.data);
                            frame.src = objectUrl;

                            if (dot.className !== 'status-dot live') {
                                statusText.textContent = 'LIVE';
                                dot.className = 'status-dot live';
                            }

                            frameCount++;
                            const now = performance.now();
                            if (now - lastFpsTime >= 1000) {
                                currentFps = (frameCount * 1000 / (now - lastFpsTime)).toFixed(1);
                                fpsEl.textContent = currentFps;
                                frameCount = 0;
                                lastFpsTime = now;
                            }

                            // Update resolution from image natural size
                            if (frame.naturalWidth > 0) {
                                resEl.textContent = `${frame.naturalWidth} x ${frame.naturalHeight}`;
                            }
                        } else {
                            try {
                                const msg = JSON.parse(e.data);
                                if (msg.type === 'hello') {
                                    viewerId = msg.viewerId;
                                    viewerEl.textContent = msg.viewerId;
                                }
                            } catch {}
                        }
                    };
                }

                connect();
            </script>
        </body>
        </html>
        """;

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);

            socket.Connect("8.8.8.8", 65530);
            var endpoint = socket.LocalEndPoint as IPEndPoint;
            return endpoint?.Address.ToString() ?? "localhost";
        }
        catch
        {
            return "localhost";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

internal class ViewerSession
{
    public string Id { get; }
    public string RemoteAddress { get; }
    public WebSocket WebSocket { get; }
    public DateTimeOffset ConnectedAt { get; }
    public long FrameCount { get; set; }

    public ViewerSession(string id, string remoteAddress, WebSocket webSocket)
    {
        Id = id;
        RemoteAddress = remoteAddress;
        WebSocket = webSocket;
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    public StreamingClient ToClient() => new(
        Id,
        RemoteAddress,
        ConnectedAt,
        new StreamQualitySettings(1920, 1080, 30, 4000));

    public async Task DisconnectAsync()
    {
        if (WebSocket.State == WebSocketState.Open)
        {
            await WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutdown", CancellationToken.None);
        }
    }
}
