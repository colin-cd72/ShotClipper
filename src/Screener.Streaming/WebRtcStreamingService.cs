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
using Screener.Abstractions.Scheduling;
using Screener.Abstractions.Streaming;
using Screener.Core.Persistence;
using Screener.Golf.Models;
using Screener.Golf.Persistence;

namespace Screener.Streaming;

/// <summary>
/// WebRTC streaming service for local network video preview.
/// </summary>
public sealed class WebRtcStreamingService : IStreamingService
{
    private readonly ILogger<WebRtcStreamingService> _logger;
    private readonly ConcurrentDictionary<string, ViewerSession> _viewers = new();

    // Optional services for REST API (injected via SetApiServices)
    private GolferRepository? _golferRepository;
    private OverlayRepository? _overlayRepository;
    private ISchedulingService? _schedulingService;
    private SettingsRepository? _settingsRepository;
    private Func<ApiStatusInfo>? _statusProvider;
    private string? _exportDirectory;

    // Lower third callback (set by desktop app to push text to SwitcherViewModel)
    private Action<string>? _setLowerThirdCallback;

    // Remote control callbacks (set by desktop app for web panel control)
    private Action<bool>? _toggleStreamCallback;
    private Func<string?, Task>? _toggleRecordingCallback;
    private Func<bool>? _isRecordingProvider;
    private Func<bool>? _isStreamingProvider;

    private HttpListener? _httpListener;
    private StreamingConfiguration? _config;
    private StreamingState _state = StreamingState.Stopped;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private DateTimeOffset _startedAt;

    public StreamingState State => _state;
    public IReadOnlyList<StreamingClient> ConnectedClients => _viewers.Values.Select(v => v.ToClient()).ToList();
    public Uri? SignalingUri { get; private set; }

    public event EventHandler<StreamingClientEventArgs>? ClientConnected;
    public event EventHandler<StreamingClientEventArgs>? ClientDisconnected;

    public WebRtcStreamingService(ILogger<WebRtcStreamingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Configure optional services used by the REST API endpoints.
    /// </summary>
    public void SetApiServices(
        GolferRepository? golferRepository = null,
        OverlayRepository? overlayRepository = null,
        ISchedulingService? schedulingService = null,
        SettingsRepository? settingsRepository = null,
        Func<ApiStatusInfo>? statusProvider = null,
        string? exportDirectory = null)
    {
        _golferRepository = golferRepository;
        _overlayRepository = overlayRepository;
        _schedulingService = schedulingService;
        _settingsRepository = settingsRepository;
        _statusProvider = statusProvider;
        _exportDirectory = exportDirectory;
    }

    /// <summary>
    /// Set callback for lower third text updates from the web panel.
    /// </summary>
    public void SetLowerThirdCallback(Action<string> callback)
    {
        _setLowerThirdCallback = callback;
    }

    /// <summary>
    /// Set callbacks for remote stream and recording control from the web panel.
    /// </summary>
    public void SetRemoteControlCallbacks(
        Action<bool>? toggleStream = null,
        Func<string?, Task>? toggleRecording = null,
        Func<bool>? isRecordingProvider = null,
        Func<bool>? isStreamingProvider = null)
    {
        _toggleStreamCallback = toggleStream;
        _toggleRecordingCallback = toggleRecording;
        _isRecordingProvider = isRecordingProvider;
        _isStreamingProvider = isStreamingProvider;
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
            _startedAt = DateTimeOffset.UtcNow;

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

    // Panel relay (outbound push to cloud panel)
    private PanelRelayService? _panelRelay;

    /// <summary>
    /// Set the panel relay service for pushing frames to the cloud panel.
    /// </summary>
    public void SetPanelRelay(PanelRelayService relay)
    {
        _panelRelay = relay;
    }

    // UYVY→JPEG encoding state
    private int _streamWidth;
    private int _streamHeight;
    private volatile bool _encoding;

    public async Task PushFrameAsync(ReadOnlyMemory<byte> frameData, TimeSpan timestamp, CancellationToken ct = default)
    {
        bool hasLocalViewers = _state == StreamingState.Running && !_viewers.IsEmpty && _config != null;
        bool hasPanelRelay = _panelRelay?.IsConnected == true;

        if (!hasLocalViewers && !hasPanelRelay)
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

            // Send to panel relay (cloud push)
            if (_panelRelay?.IsConnected == true)
            {
                _ = _panelRelay.SendFrameAsync(jpegBytes);
            }

            // Send to all connected local viewers
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
            if (path.StartsWith("/api/"))
            {
                await HandleApiRequestAsync(context, path, ct);
            }
            else if (path == "/stream" || path == "/")
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

    // ========== REST API ==========

    private async Task HandleApiRequestAsync(HttpListenerContext context, string path, CancellationToken ct)
    {
        var method = context.Request.HttpMethod;

        // Validate API key
        if (!ValidateApiKey(context))
        {
            await WriteJsonResponse(context, 401, new { error = "Invalid or missing API key" });
            return;
        }

        // Add CORS headers
        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-API-Key");

        if (method == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }

        try
        {
            // Route matching
            if (path == "/api/status" && method == "GET")
                await HandleGetStatus(context);
            else if (path == "/api/golfers" && method == "GET")
                await HandleGetGolfers(context);
            else if (path == "/api/golfers" && method == "POST")
                await HandleCreateGolfer(context);
            else if (path == "/api/golfers/import" && method == "POST")
                await HandleImportGolfers(context);
            else if (path.StartsWith("/api/golfers/") && method == "PUT")
                await HandleUpdateGolfer(context, path);
            else if (path.StartsWith("/api/golfers/") && method == "DELETE")
                await HandleDeleteGolfer(context, path);
            else if (path == "/api/overlays" && method == "GET")
                await HandleGetOverlays(context);
            else if (path.StartsWith("/api/overlays/logo") && method == "POST")
                await HandleUploadLogo(context);
            else if (path.StartsWith("/api/overlays/") && method == "PUT")
                await HandleUpdateOverlay(context, path);
            else if (path == "/api/clips" && method == "GET")
                await HandleGetClips(context);
            else if (path.StartsWith("/api/clips/") && path.EndsWith("/download") && method == "GET")
                await HandleDownloadClip(context, path);
            else if (path == "/api/schedules" && method == "GET")
                await HandleGetSchedules(context);
            else if (path == "/api/schedules" && method == "POST")
                await HandleCreateSchedule(context);
            else if (path.StartsWith("/api/schedules/") && method == "DELETE")
                await HandleDeleteSchedule(context, path);
            else if (path.StartsWith("/api/settings/") && method == "GET")
                await HandleGetSetting(context, path);
            else if (path.StartsWith("/api/settings/") && method == "PUT")
                await HandleSetSetting(context, path);
            else if (path == "/api/lowerthird" && method == "POST")
                await HandleSetLowerThird(context);
            else if (path == "/api/lowerthird" && method == "DELETE")
                await HandleClearLowerThird(context);
            else if (path == "/api/stream" && method == "POST")
                await HandleToggleStream(context);
            else if (path == "/api/recording" && method == "POST")
                await HandleToggleRecording(context);
            else
                await WriteJsonResponse(context, 404, new { error = "Not found" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API error handling {Method} {Path}", method, path);
            await WriteJsonResponse(context, 500, new { error = ex.Message });
        }
    }

    private bool ValidateApiKey(HttpListenerContext context)
    {
        if (_settingsRepository == null)
            return true; // No settings repo = no auth required

        var apiKey = context.Request.Headers["X-API-Key"];
        if (string.IsNullOrEmpty(apiKey))
            return false;

        // Synchronously get the stored key (acceptable for auth check)
        var storedKey = _settingsRepository.GetAsync<string>(SettingsKeys.ApiKey).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(storedKey))
            return true; // No key configured = allow all

        return apiKey == storedKey;
    }

    private async Task HandleGetStatus(HttpListenerContext context)
    {
        var status = _statusProvider?.Invoke() ?? new ApiStatusInfo();
        status.Uptime = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
        status.StreamingState = _state.ToString();
        status.ConnectedViewers = _viewers.Count;

        await WriteJsonResponse(context, 200, status);
    }

    private async Task HandleGetGolfers(HttpListenerContext context)
    {
        if (_golferRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Golfer service not available" });
            return;
        }

        var golfers = await _golferRepository.GetAllAsync();
        await WriteJsonResponse(context, 200, golfers);
    }

    private async Task HandleCreateGolfer(HttpListenerContext context)
    {
        if (_golferRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Golfer service not available" });
            return;
        }

        var body = await ReadJsonBody<GolferProfile>(context);
        if (body == null) return;

        body.Id = Guid.NewGuid().ToString();
        body.CreatedAt = DateTimeOffset.UtcNow;
        body.UpdatedAt = DateTimeOffset.UtcNow;
        await _golferRepository.CreateAsync(body);
        await WriteJsonResponse(context, 201, body);
    }

    private async Task HandleUpdateGolfer(HttpListenerContext context, string path)
    {
        if (_golferRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Golfer service not available" });
            return;
        }

        var id = path.Replace("/api/golfers/", "");
        var existing = await _golferRepository.GetByIdAsync(id);
        if (existing == null)
        {
            await WriteJsonResponse(context, 404, new { error = "Golfer not found" });
            return;
        }

        var body = await ReadJsonBody<GolferProfile>(context);
        if (body == null) return;

        body.Id = id;
        body.UpdatedAt = DateTimeOffset.UtcNow;
        await _golferRepository.UpdateAsync(body);
        await WriteJsonResponse(context, 200, body);
    }

    private async Task HandleDeleteGolfer(HttpListenerContext context, string path)
    {
        if (_golferRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Golfer service not available" });
            return;
        }

        var id = path.Replace("/api/golfers/", "");
        await _golferRepository.DeleteAsync(id);
        await WriteJsonResponse(context, 200, new { deleted = true });
    }

    private async Task HandleImportGolfers(HttpListenerContext context)
    {
        if (_golferRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Golfer service not available" });
            return;
        }

        var body = await ReadJsonBody<GolferImportRequest>(context);
        if (body?.Names == null || body.Names.Count == 0)
        {
            await WriteJsonResponse(context, 400, new { error = "No names provided" });
            return;
        }

        var created = new List<GolferProfile>();
        foreach (var name in body.Names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var trimmed = name.Trim();
            var spaceIdx = trimmed.IndexOf(' ');
            string firstName, lastName;
            if (spaceIdx > 0)
            {
                firstName = trimmed[..spaceIdx];
                lastName = trimmed[(spaceIdx + 1)..];
            }
            else
            {
                firstName = trimmed;
                lastName = "";
            }

            var golfer = new GolferProfile
            {
                Id = Guid.NewGuid().ToString(),
                FirstName = firstName,
                LastName = lastName,
                DisplayName = trimmed,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _golferRepository.CreateAsync(golfer);
            created.Add(golfer);
        }

        await WriteJsonResponse(context, 201, new { imported = created.Count, golfers = created });
    }

    private async Task HandleSetLowerThird(HttpListenerContext context)
    {
        var body = await ReadJsonBody<LowerThirdRequest>(context);
        if (body == null) return;

        var text = body.Text ?? "";
        _setLowerThirdCallback?.Invoke(text);
        await WriteJsonResponse(context, 200, new { text });
    }

    private async Task HandleClearLowerThird(HttpListenerContext context)
    {
        _setLowerThirdCallback?.Invoke("");
        await WriteJsonResponse(context, 200, new { text = "" });
    }

    private async Task HandleToggleStream(HttpListenerContext context)
    {
        var body = await ReadJsonBody<StreamToggleRequest>(context);
        if (body == null) return;

        if (_toggleStreamCallback == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Stream control not available" });
            return;
        }

        _toggleStreamCallback(body.Enabled);
        await WriteJsonResponse(context, 200, new { enabled = body.Enabled });
    }

    private async Task HandleToggleRecording(HttpListenerContext context)
    {
        var body = await ReadJsonBody<RecordingToggleRequest>(context);
        if (body == null) return;

        if (_toggleRecordingCallback == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Recording control not available" });
            return;
        }

        await _toggleRecordingCallback(body.Enabled ? body.Name : null);
        var isRecording = _isRecordingProvider?.Invoke() ?? false;
        await WriteJsonResponse(context, 200, new { recording = isRecording });
    }

    private async Task HandleGetOverlays(HttpListenerContext context)
    {
        if (_overlayRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Overlay service not available" });
            return;
        }

        var overlays = await _overlayRepository.GetAllAsync();
        await WriteJsonResponse(context, 200, overlays);
    }

    private async Task HandleUpdateOverlay(HttpListenerContext context, string path)
    {
        if (_overlayRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Overlay service not available" });
            return;
        }

        var type = path.Replace("/api/overlays/", "");
        var body = await ReadJsonBody<OverlayConfigRecord>(context);
        if (body == null) return;

        body.Type = type;
        body.UpdatedAt = DateTimeOffset.UtcNow;
        await _overlayRepository.SaveAsync(body);
        await WriteJsonResponse(context, 200, body);
    }

    private async Task HandleUploadLogo(HttpListenerContext context)
    {
        if (string.IsNullOrEmpty(_exportDirectory))
        {
            await WriteJsonResponse(context, 503, new { error = "Export directory not configured" });
            return;
        }

        var logoDir = Path.Combine(_exportDirectory, "logos");
        Directory.CreateDirectory(logoDir);

        // Read the multipart body as raw bytes
        using var ms = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(ms);
        var fileName = $"logo_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.png";
        var filePath = Path.Combine(logoDir, fileName);
        await File.WriteAllBytesAsync(filePath, ms.ToArray());

        await WriteJsonResponse(context, 200, new { path = filePath, fileName });
    }

    private async Task HandleGetClips(HttpListenerContext context)
    {
        if (string.IsNullOrEmpty(_exportDirectory) || !Directory.Exists(_exportDirectory))
        {
            await WriteJsonResponse(context, 200, Array.Empty<object>());
            return;
        }

        var clips = Directory.GetFiles(_exportDirectory, "*.mp4", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new
            {
                name = f.Name,
                path = f.FullName,
                size = f.Length,
                created = f.CreationTimeUtc,
                modified = f.LastWriteTimeUtc
            })
            .ToList();

        await WriteJsonResponse(context, 200, clips);
    }

    private async Task HandleDownloadClip(HttpListenerContext context, string path)
    {
        // Extract clip name: /api/clips/{name}/download
        var segments = path.Split('/');
        if (segments.Length < 4)
        {
            await WriteJsonResponse(context, 400, new { error = "Invalid clip path" });
            return;
        }

        var clipName = Uri.UnescapeDataString(segments[3]);
        if (string.IsNullOrEmpty(_exportDirectory))
        {
            await WriteJsonResponse(context, 503, new { error = "Export directory not configured" });
            return;
        }

        // Sanitize to prevent path traversal
        clipName = Path.GetFileName(clipName);
        var filePath = Path.Combine(_exportDirectory, clipName);

        if (!File.Exists(filePath))
        {
            // Search subdirectories
            var found = Directory.GetFiles(_exportDirectory, clipName, SearchOption.AllDirectories).FirstOrDefault();
            if (found == null)
            {
                await WriteJsonResponse(context, 404, new { error = "Clip not found" });
                return;
            }
            filePath = found;
        }

        context.Response.ContentType = "video/mp4";
        context.Response.ContentLength64 = new FileInfo(filePath).Length;
        context.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{clipName}\"");

        using var fs = File.OpenRead(filePath);
        await fs.CopyToAsync(context.Response.OutputStream);
        context.Response.Close();
    }

    private async Task HandleGetSchedules(HttpListenerContext context)
    {
        if (_schedulingService == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Scheduling service not available" });
            return;
        }

        await WriteJsonResponse(context, 200, _schedulingService.Schedules);
    }

    private async Task HandleCreateSchedule(HttpListenerContext context)
    {
        if (_schedulingService == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Scheduling service not available" });
            return;
        }

        var body = await ReadJsonBody<ScheduledRecordingRequest>(context);
        if (body == null) return;

        var schedule = await _schedulingService.CreateScheduleAsync(body);
        await WriteJsonResponse(context, 201, schedule);
    }

    private async Task HandleDeleteSchedule(HttpListenerContext context, string path)
    {
        if (_schedulingService == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Scheduling service not available" });
            return;
        }

        var idStr = path.Replace("/api/schedules/", "");
        if (!Guid.TryParse(idStr, out var id))
        {
            await WriteJsonResponse(context, 400, new { error = "Invalid schedule ID" });
            return;
        }

        await _schedulingService.DeleteScheduleAsync(id);
        await WriteJsonResponse(context, 200, new { deleted = true });
    }

    private async Task HandleGetSetting(HttpListenerContext context, string path)
    {
        if (_settingsRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Settings service not available" });
            return;
        }

        var key = path.Replace("/api/settings/", "");
        var value = await _settingsRepository.GetAsync<object>(key);
        await WriteJsonResponse(context, 200, new { key, value });
    }

    private async Task HandleSetSetting(HttpListenerContext context, string path)
    {
        if (_settingsRepository == null)
        {
            await WriteJsonResponse(context, 503, new { error = "Settings service not available" });
            return;
        }

        var key = path.Replace("/api/settings/", "");

        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var bodyStr = await reader.ReadToEndAsync();

        string value;
        try
        {
            using var doc = JsonDocument.Parse(bodyStr);
            value = doc.RootElement.TryGetProperty("value", out var val) ? val.ToString() : bodyStr;
        }
        catch
        {
            value = bodyStr;
        }

        await _settingsRepository.SetAsync(key, value);
        await WriteJsonResponse(context, 200, new { key, value });
    }

    private static async Task WriteJsonResponse(HttpListenerContext context, int statusCode, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task<T?> ReadJsonBody<T>(HttpListenerContext context) where T : class
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            await WriteJsonResponse(context, 400, new { error = "Invalid JSON body" });
            return null;
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

/// <summary>
/// Status information provided by the desktop app for the /api/status endpoint.
/// </summary>
public class ApiStatusInfo
{
    public string StreamingState { get; set; } = "Stopped";
    public int ConnectedViewers { get; set; }
    public double Uptime { get; set; }
    public int SwingCount { get; set; }
    public bool IsRecording { get; set; }
    public bool IsSessionActive { get; set; }
    public string? GolferName { get; set; }
    public string? AutoCutState { get; set; }
    public int PracticeSwingCount { get; set; }
}

/// <summary>
/// Request body for POST /api/golfers/import.
/// </summary>
public class GolferImportRequest
{
    public List<string> Names { get; set; } = new();
}

/// <summary>
/// Request body for POST /api/lowerthird.
/// </summary>
public class LowerThirdRequest
{
    public string? Text { get; set; }
}

/// <summary>
/// Request body for POST /api/stream.
/// </summary>
public class StreamToggleRequest
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Request body for POST /api/recording.
/// </summary>
public class RecordingToggleRequest
{
    public bool Enabled { get; set; }
    public string? Name { get; set; }
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
