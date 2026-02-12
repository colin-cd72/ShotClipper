using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Timecode;

namespace Screener.Timecode.Providers;

/// <summary>
/// Time provider using NTP (Network Time Protocol) for accurate time sync.
/// </summary>
public sealed class NtpTimeProvider : ITimecodeProvider
{
    private readonly ILogger<NtpTimeProvider> _logger;
    private readonly string[] _ntpServers;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private TimeSpan _offset = TimeSpan.Zero;
    private DateTime _lastSync = DateTime.MinValue;
    private bool _isAvailable;

    public string Name => "NTP";
    public bool IsAvailable => _isAvailable;
    public TimeSpan Offset => _offset;

    public NtpTimeProvider(ILogger<NtpTimeProvider> logger, string[]? ntpServers = null)
    {
        _logger = logger;
        _ntpServers = ntpServers ?? new[]
        {
            "time.google.com",
            "pool.ntp.org",
            "time.windows.com",
            "time.apple.com"
        };
    }

    public async Task<DateTimeOffset> GetCurrentTimeAsync(CancellationToken ct = default)
    {
        // Refresh NTP sync if needed (every 30 minutes)
        if (DateTime.UtcNow - _lastSync > TimeSpan.FromMinutes(30))
        {
            await SyncAsync(ct);
        }

        return DateTimeOffset.UtcNow + _offset;
    }

    public Smpte12MTimecode GetTimecode(DateTimeOffset time, FrameRate frameRate, bool dropFrame = false)
    {
        // Convert to time-of-day timecode
        var timeOfDay = time.TimeOfDay;
        return Smpte12MTimecode.FromTimeSpan(timeOfDay, frameRate, dropFrame);
    }

    /// <summary>
    /// Synchronize with NTP servers.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);

        try
        {
            foreach (var server in _ntpServers)
            {
                try
                {
                    var offset = await QueryNtpServerAsync(server, ct);
                    _offset = offset;
                    _lastSync = DateTime.UtcNow;
                    _isAvailable = true;

                    _logger.LogInformation("NTP sync successful. Server: {Server}, Offset: {Offset}ms",
                        server, offset.TotalMilliseconds);

                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "NTP sync failed for {Server}", server);
                }
            }

            _isAvailable = false;
            _logger.LogError("All NTP servers failed");
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<TimeSpan> QueryNtpServerAsync(string server, CancellationToken ct)
    {
        // NTP message structure
        var ntpData = new byte[48];
        ntpData[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (Client)

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = 3000;
        socket.SendTimeout = 3000;

        var addresses = await Dns.GetHostAddressesAsync(server, ct);
        var endpoint = new IPEndPoint(addresses[0], 123);

        var sendTime = DateTime.UtcNow;
        await socket.ConnectAsync(endpoint, ct);
        await socket.SendAsync(ntpData, SocketFlags.None, ct);

        var received = await socket.ReceiveAsync(ntpData, SocketFlags.None, ct);
        var receiveTime = DateTime.UtcNow;

        if (received < 48)
            throw new InvalidOperationException("Invalid NTP response");

        // Extract timestamps from response
        // Transmit timestamp starts at byte 40
        var seconds = (ulong)ntpData[40] << 24 | (ulong)ntpData[41] << 16 |
                      (ulong)ntpData[42] << 8 | ntpData[43];
        var fraction = (ulong)ntpData[44] << 24 | (ulong)ntpData[45] << 16 |
                       (ulong)ntpData[46] << 8 | ntpData[47];

        // NTP timestamp epoch is January 1, 1900
        var ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var serverTime = ntpEpoch.AddSeconds(seconds).AddTicks((long)(fraction * TimeSpan.TicksPerSecond / 0x100000000L));

        // Calculate offset (simplified - doesn't account for network delay properly)
        var roundTripTime = receiveTime - sendTime;
        var offset = serverTime - receiveTime + TimeSpan.FromTicks(roundTripTime.Ticks / 2);

        return offset;
    }
}

/// <summary>
/// Time provider using system clock.
/// </summary>
public sealed class SystemTimeProvider : ITimecodeProvider
{
    public string Name => "System";
    public bool IsAvailable => true;
    public TimeSpan Offset => TimeSpan.Zero;

    public Task<DateTimeOffset> GetCurrentTimeAsync(CancellationToken ct = default)
    {
        return Task.FromResult(DateTimeOffset.Now);
    }

    public Smpte12MTimecode GetTimecode(DateTimeOffset time, FrameRate frameRate, bool dropFrame = false)
    {
        return Smpte12MTimecode.FromTimeSpan(time.TimeOfDay, frameRate, dropFrame);
    }
}

/// <summary>
/// Time provider with manual offset control.
/// </summary>
public sealed class ManualTimeProvider : ITimecodeProvider
{
    private TimeSpan _manualOffset = TimeSpan.Zero;
    private Smpte12MTimecode? _jammedTimecode;
    private DateTime _jamTime;
    private FrameRate _jamFrameRate;

    public string Name => "Manual";
    public bool IsAvailable => true;
    public TimeSpan Offset => _manualOffset;

    public Task<DateTimeOffset> GetCurrentTimeAsync(CancellationToken ct = default)
    {
        return Task.FromResult(DateTimeOffset.Now + _manualOffset);
    }

    public Smpte12MTimecode GetTimecode(DateTimeOffset time, FrameRate frameRate, bool dropFrame = false)
    {
        if (_jammedTimecode.HasValue)
        {
            // Calculate elapsed time since jam
            var elapsed = DateTime.UtcNow - _jamTime;
            var elapsedFrames = (long)(elapsed.TotalSeconds * frameRate.Value);
            var jamFrames = _jammedTimecode.Value.ToTotalFrames(_jamFrameRate);
            var totalFrames = jamFrames + elapsedFrames;

            return Smpte12MTimecode.FromTotalFrames(totalFrames, frameRate, dropFrame);
        }

        return Smpte12MTimecode.FromTimeSpan(time.TimeOfDay + _manualOffset, frameRate, dropFrame);
    }

    /// <summary>
    /// Set a manual time offset.
    /// </summary>
    public void SetOffset(TimeSpan offset)
    {
        _manualOffset = offset;
        _jammedTimecode = null;
    }

    /// <summary>
    /// Jam to a specific timecode value.
    /// </summary>
    public void JamTimecode(Smpte12MTimecode timecode, FrameRate frameRate)
    {
        _jammedTimecode = timecode;
        _jamTime = DateTime.UtcNow;
        _jamFrameRate = frameRate;
    }

    /// <summary>
    /// Clear jammed timecode.
    /// </summary>
    public void ClearJam()
    {
        _jammedTimecode = null;
    }
}
