using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.TimeZones;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Timecode;
using Screener.Timecode.Providers;

namespace Screener.Timecode;

/// <summary>
/// Central service for timecode generation and management.
/// </summary>
public sealed class TimecodeService : ITimecodeService
{
    private readonly ILogger<TimecodeService> _logger;
    private readonly Dictionary<string, ITimecodeProvider> _providers;
    private ITimecodeProvider _currentProvider;
    private TimeZoneInfo _timezone = TimeZoneInfo.Local;
    private bool _useDropFrame = true;

    public ITimecodeProvider CurrentProvider => _currentProvider;
    public IReadOnlyList<ITimecodeProvider> AvailableProviders => _providers.Values.ToList();

    public TimeZoneInfo Timezone
    {
        get => _timezone;
        set
        {
            _timezone = value;
            _logger.LogInformation("Timezone changed to {Timezone}", value.Id);
        }
    }

    public bool UseDropFrame
    {
        get => _useDropFrame;
        set
        {
            _useDropFrame = value;
            _logger.LogInformation("Drop frame mode: {DropFrame}", value);
        }
    }

    public TimecodeService(ILogger<TimecodeService> logger, IEnumerable<ITimecodeProvider> providers)
    {
        _logger = logger;
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        // Default to NTP provider if available, otherwise System
        _currentProvider = _providers.GetValueOrDefault("NTP")
                           ?? _providers.GetValueOrDefault("System")
                           ?? _providers.Values.First();

        _logger.LogInformation("TimecodeService initialized with provider: {Provider}", _currentProvider.Name);
    }

    public void SetProvider(string providerName)
    {
        if (_providers.TryGetValue(providerName, out var provider))
        {
            _currentProvider = provider;
            _logger.LogInformation("Timecode provider changed to {Provider}", providerName);
        }
        else
        {
            _logger.LogWarning("Unknown timecode provider: {Provider}", providerName);
        }
    }

    public async Task<Smpte12MTimecode> GetCurrentTimecodeAsync(FrameRate frameRate, CancellationToken ct = default)
    {
        var time = await _currentProvider.GetCurrentTimeAsync(ct);

        // Apply timezone
        var localTime = TimeZoneInfo.ConvertTime(time, _timezone);

        // Determine if drop frame should be used
        var useDropFrame = _useDropFrame && IsDropFrameFrameRate(frameRate);

        return _currentProvider.GetTimecode(localTime, frameRate, useDropFrame);
    }

    public void SetManualOffset(TimeSpan offset)
    {
        if (_currentProvider is ManualTimeProvider manualProvider)
        {
            manualProvider.SetOffset(offset);
            _logger.LogInformation("Manual offset set to {Offset}", offset);
        }
        else
        {
            _logger.LogWarning("Current provider does not support manual offset");
        }
    }

    /// <summary>
    /// Get all available timezones.
    /// </summary>
    public IReadOnlyList<TimeZoneInfo> GetAvailableTimezones()
    {
        return TimeZoneInfo.GetSystemTimeZones().ToList();
    }

    /// <summary>
    /// Get a timezone by ID.
    /// </summary>
    public TimeZoneInfo? GetTimezone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            // Try NodaTime for IANA timezone IDs
            try
            {
                var tzdb = DateTimeZoneProviders.Tzdb;
                var zone = tzdb.GetZoneOrNull(id);
                if (zone != null)
                {
                    // Convert NodaTime zone to TimeZoneInfo (approximate)
                    return TimeZoneInfo.FindSystemTimeZoneById(
                        TzdbDateTimeZoneSource.Default.WindowsMapping.MapZones
                            .FirstOrDefault(m => m.TzdbIds.Contains(id))?.WindowsId
                        ?? TimeZoneInfo.Local.Id);
                }
            }
            catch
            {
            }

            return null;
        }
    }

    private static bool IsDropFrameFrameRate(FrameRate frameRate)
    {
        // Drop frame is used for 29.97 and 59.94 fps
        var fps = frameRate.Value;
        return Math.Abs(fps - 29.97) < 0.01 || Math.Abs(fps - 59.94) < 0.01;
    }
}
