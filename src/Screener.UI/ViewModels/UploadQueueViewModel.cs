using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Screener.Abstractions.Upload;

namespace Screener.UI.ViewModels;

public partial class UploadQueueViewModel : ObservableObject
{
    public ObservableCollection<UploadJobViewModel> Jobs { get; } = new();

    public int PendingCount => Jobs.Count(j => j.Status == UploadJobStatus.Queued);
    public int ActiveCount => Jobs.Count(j => j.Status == UploadJobStatus.Uploading);
    public int CompletedCount => Jobs.Count(j => j.Status == UploadJobStatus.Completed);
    public int FailedCount => Jobs.Count(j => j.Status == UploadJobStatus.Failed);

    public double TotalProgress
    {
        get
        {
            var activeJobs = Jobs.Where(j => j.Status == UploadJobStatus.Uploading).ToList();
            if (!activeJobs.Any()) return 0;
            return activeJobs.Average(j => j.ProgressPercent);
        }
    }
}

public partial class UploadJobViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _providerName = string.Empty;

    [ObservableProperty]
    private UploadJobStatus _status = UploadJobStatus.Queued;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _uploadedBytes;

    [ObservableProperty]
    private string? _errorMessage;

    public double ProgressPercent => TotalBytes > 0 ? 100.0 * UploadedBytes / TotalBytes : 0;

    public string ProgressText => $"{FormatBytes(UploadedBytes)} / {FormatBytes(TotalBytes)}";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / 1024.0:F1} KB";
    }
}
