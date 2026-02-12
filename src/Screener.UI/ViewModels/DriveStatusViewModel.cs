using CommunityToolkit.Mvvm.ComponentModel;

namespace Screener.UI.ViewModels;

public partial class DriveStatusViewModel : ObservableObject
{
    [ObservableProperty]
    private string _driveName = "No Drive Selected";

    [ObservableProperty]
    private string _drivePath = string.Empty;

    [ObservableProperty]
    private long _totalSize;

    [ObservableProperty]
    private long _freeSpace;

    [ObservableProperty]
    private double _writeBitrateMbps = 15.0;

    public double UsedPercent => TotalSize > 0 ? 100.0 * (TotalSize - FreeSpace) / TotalSize : 0;

    public string FreeSpaceDisplay => FormatBytes(FreeSpace);

    public string RecordingTimeRemaining
    {
        get
        {
            if (WriteBitrateMbps <= 0 || FreeSpace <= 0) return "Unknown";

            var bytesPerSecond = WriteBitrateMbps * 1024 * 1024 / 8;
            var secondsRemaining = FreeSpace / bytesPerSecond;
            var time = TimeSpan.FromSeconds(secondsRemaining);

            if (time.TotalHours >= 1)
                return $"~{time.TotalHours:F1}h remaining";
            if (time.TotalMinutes >= 1)
                return $"~{time.TotalMinutes:F0}m remaining";
            return "< 1m remaining";
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
}
