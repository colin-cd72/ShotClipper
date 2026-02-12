using Microsoft.Extensions.Logging;

namespace Screener.Recording;

/// <summary>
/// Monitors available drives and disk space.
/// </summary>
public sealed class DriveManager : IDisposable
{
    private readonly ILogger<DriveManager> _logger;
    private readonly List<DriveStatus> _drives = new();
    private readonly Timer _monitorTimer;
    private readonly object _lock = new();
    private DriveStatus? _selectedDrive;
    private bool _disposed;

    public IReadOnlyList<DriveStatus> AvailableDrives
    {
        get
        {
            lock (_lock)
            {
                return _drives.ToList();
            }
        }
    }

    public DriveStatus? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            _selectedDrive = value;
            SelectedDriveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler<DriveStatusEventArgs>? DriveStatusChanged;
    public event EventHandler? SelectedDriveChanged;
    public event EventHandler<DriveWarningEventArgs>? LowSpaceWarning;

    public DriveManager(ILogger<DriveManager> logger)
    {
        _logger = logger;
        _monitorTimer = new Timer(MonitorDrives, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Start monitoring drives.
    /// </summary>
    public void StartMonitoring(TimeSpan interval)
    {
        RefreshDrives();
        _monitorTimer.Change(TimeSpan.Zero, interval);
    }

    /// <summary>
    /// Stop monitoring.
    /// </summary>
    public void StopMonitoring()
    {
        _monitorTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Refresh the list of available drives.
    /// </summary>
    public void RefreshDrives()
    {
        try
        {
            var driveInfos = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .ToList();

            lock (_lock)
            {
                _drives.Clear();

                foreach (var info in driveInfos)
                {
                    var status = new DriveStatus
                    {
                        RootPath = info.RootDirectory.FullName,
                        Name = string.IsNullOrEmpty(info.VolumeLabel)
                            ? info.Name
                            : $"{info.Name} {info.VolumeLabel}",
                        DriveType = info.DriveType,
                        TotalSize = info.TotalSize,
                        FreeSpace = info.AvailableFreeSpace,
                        IsReady = info.IsReady
                    };

                    _drives.Add(status);
                }

                // Update selected drive if still valid
                if (_selectedDrive != null)
                {
                    _selectedDrive = _drives.FirstOrDefault(d => d.RootPath == _selectedDrive.RootPath);
                }

                // Auto-select first drive if none selected
                _selectedDrive ??= _drives.FirstOrDefault();
            }

            _logger.LogDebug("Found {Count} drives", _drives.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh drives");
        }
    }

    /// <summary>
    /// Get estimated recording time remaining for a drive at given bitrate.
    /// </summary>
    public TimeSpan GetRecordingTimeRemaining(string drivePath, double bitrateMbps)
    {
        var drive = _drives.FirstOrDefault(d => d.RootPath == drivePath);
        if (drive == null || bitrateMbps <= 0)
            return TimeSpan.Zero;

        var bytesPerSecond = bitrateMbps * 1024 * 1024 / 8;
        var seconds = drive.FreeSpace / bytesPerSecond;

        return TimeSpan.FromSeconds(seconds);
    }

    private void MonitorDrives(object? state)
    {
        if (_disposed) return;

        try
        {
            var previousDrives = _drives.ToDictionary(d => d.RootPath, d => d.FreeSpace);

            RefreshDrives();

            // Check for significant changes or low space warnings
            foreach (var drive in _drives)
            {
                if (previousDrives.TryGetValue(drive.RootPath, out var previousSpace))
                {
                    var change = Math.Abs(drive.FreeSpace - previousSpace);

                    // Notify if more than 100MB changed
                    if (change > 100 * 1024 * 1024)
                    {
                        DriveStatusChanged?.Invoke(this, new DriveStatusEventArgs { Drive = drive });
                    }
                }

                // Check for low space (< 10GB or < 10%)
                var percentFree = 100.0 * drive.FreeSpace / drive.TotalSize;
                if (drive.FreeSpace < 10L * 1024 * 1024 * 1024 || percentFree < 10)
                {
                    LowSpaceWarning?.Invoke(this, new DriveWarningEventArgs
                    {
                        Drive = drive,
                        Level = percentFree < 5 ? WarningLevel.Critical : WarningLevel.Warning,
                        Message = $"Low disk space on {drive.Name}: {FormatBytes(drive.FreeSpace)} remaining"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring drives");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB";
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorTimer.Dispose();
    }
}

public class DriveStatus
{
    public string RootPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DriveType DriveType { get; init; }
    public long TotalSize { get; init; }
    public long FreeSpace { get; init; }
    public bool IsReady { get; init; }

    public double UsedPercent => TotalSize > 0 ? 100.0 * (TotalSize - FreeSpace) / TotalSize : 0;
    public double FreePercent => 100.0 - UsedPercent;
}

public class DriveStatusEventArgs : EventArgs
{
    public required DriveStatus Drive { get; init; }
}

public class DriveWarningEventArgs : EventArgs
{
    public required DriveStatus Drive { get; init; }
    public required WarningLevel Level { get; init; }
    public required string Message { get; init; }
}

public enum WarningLevel
{
    Info,
    Warning,
    Critical
}
