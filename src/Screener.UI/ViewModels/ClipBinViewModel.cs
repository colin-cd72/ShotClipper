using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Screener.UI.ViewModels;

public partial class ClipBinViewModel : ObservableObject
{
    public ObservableCollection<ClipViewModel> Clips { get; } = new();
    public ObservableCollection<MarkerViewModel> LiveMarkers { get; } = new();

    [ObservableProperty]
    private ClipViewModel? _selectedClip;

    [ObservableProperty]
    private bool _isListView = true;

    public void AddClip(ClipViewModel clip)
    {
        // Insert at top so newest is first
        Clips.Insert(0, clip);
        SelectedClip = clip;
    }
}

public partial class ClipViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private TimeSpan _inPoint;

    [ObservableProperty]
    private TimeSpan _outPoint;

    [ObservableProperty]
    private DateTime _createdAt = DateTime.UtcNow;

    [ObservableProperty]
    private long _fileSizeBytes;

    public string FileSizeDisplay => FormatBytes(FileSizeBytes);

    public string FileName => Path.GetFileName(FilePath);

    [RelayCommand]
    private void RevealInExplorer()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            return;

        Process.Start("explorer.exe", $"/select,\"{FilePath}\"");
    }

    [RelayCommand]
    private void PlayFile()
    {
        if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = FilePath,
            UseShellExecute = true
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }
}

public partial class MarkerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private TimeSpan _position;

    [ObservableProperty]
    private string _timecode = string.Empty;

    [ObservableProperty]
    private string _markerType = "Chapter";
}
