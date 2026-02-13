using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Screener.Streaming;

/// <summary>
/// Maintains an outbound WebSocket to the cloud panel, pushing frames and status.
/// Works through any firewall since it's an outbound connection.
/// </summary>
public sealed class PanelRelayService : IDisposable
{
    private readonly ILogger<PanelRelayService> _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _connectTask;
    private Task? _statusTask;
    private volatile bool _authenticated;
    private volatile bool _running;

    private string? _panelUrl;
    private string? _apiKey;
    private Func<object>? _statusProvider;

    public bool IsConnected => _authenticated && _ws?.State == WebSocketState.Open;

    public PanelRelayService(ILogger<PanelRelayService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start the relay to the cloud panel.
    /// </summary>
    public void Start(string panelUrl, string apiKey, Func<object>? statusProvider = null)
    {
        if (_running) Stop();

        _panelUrl = panelUrl.TrimEnd('/');
        _apiKey = apiKey;
        _statusProvider = statusProvider;
        _running = true;
        _cts = new CancellationTokenSource();

        _connectTask = Task.Run(() => ConnectLoop(_cts.Token));
        _statusTask = Task.Run(() => StatusLoop(_cts.Token));

        _logger.LogInformation("Panel relay started → {Url}", _panelUrl);
    }

    /// <summary>
    /// Stop the relay.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _cts?.Cancel();

        try { _ws?.Dispose(); } catch { }
        _ws = null;
        _authenticated = false;

        _logger.LogInformation("Panel relay stopped");
    }

    /// <summary>
    /// Send a JPEG frame to the panel. Fire-and-forget, drops frame if not connected.
    /// </summary>
    public async Task SendFrameAsync(byte[] jpegBytes)
    {
        if (!_authenticated || _ws?.State != WebSocketState.Open)
            return;

        try
        {
            await _ws.SendAsync(jpegBytes, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch
        {
            // Connection lost — reconnect loop will handle it
            _authenticated = false;
        }
    }

    private async Task ConnectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndAuthenticate(ct);

                // Stay connected — read messages (auth_ok, etc.)
                var buffer = new byte[1024];
                while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Panel relay connection error: {Message}", ex.Message);
            }

            _authenticated = false;
            try { _ws?.Dispose(); } catch { }
            _ws = null;

            if (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Panel relay reconnecting in 5s...");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task ConnectAndAuthenticate(CancellationToken ct)
    {
        _ws = new ClientWebSocket();

        // Build WebSocket URL: https://... → wss://.../desktop-push
        var wsUrl = _panelUrl!
            .Replace("https://", "wss://")
            .Replace("http://", "ws://")
            + "/desktop-push";

        _logger.LogInformation("Connecting to panel: {Url}", wsUrl);
        await _ws.ConnectAsync(new Uri(wsUrl), ct);

        // Send auth message
        var hostname = Environment.MachineName;
        var authMsg = JsonSerializer.Serialize(new { type = "auth", apiKey = _apiKey, hostname });
        await _ws.SendAsync(
            Encoding.UTF8.GetBytes(authMsg),
            WebSocketMessageType.Text, true, ct);

        // Wait for auth response
        var buffer = new byte[1024];
        var result = await _ws.ReceiveAsync(buffer, ct);
        var response = Encoding.UTF8.GetString(buffer, 0, result.Count);

        if (response.Contains("auth_ok"))
        {
            _authenticated = true;
            _logger.LogInformation("Panel relay authenticated");
        }
        else
        {
            throw new Exception("Panel auth rejected");
        }
    }

    private async Task StatusLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);

                if (_authenticated && _ws?.State == WebSocketState.Open && _statusProvider != null)
                {
                    var status = _statusProvider();
                    var json = JsonSerializer.Serialize(status);
                    await _ws.SendAsync(
                        Encoding.UTF8.GetBytes(json),
                        WebSocketMessageType.Text, true, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignore — connection errors handled by connect loop
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
