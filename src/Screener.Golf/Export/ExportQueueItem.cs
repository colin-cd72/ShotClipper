using CommunityToolkit.Mvvm.ComponentModel;

namespace Screener.Golf.Export;

/// <summary>
/// Display model for a single swing export in the UI queue.
/// </summary>
public partial class ExportQueueItem : ObservableObject
{
    [ObservableProperty]
    private int _swingNumber;

    [ObservableProperty]
    private string _status = "Pending";

    [ObservableProperty]
    private string? _outputPath;

    [ObservableProperty]
    private TimeSpan? _duration;
}
