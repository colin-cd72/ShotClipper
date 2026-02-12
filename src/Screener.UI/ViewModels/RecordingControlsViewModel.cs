using CommunityToolkit.Mvvm.ComponentModel;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Recording;

namespace Screener.UI.ViewModels;

public partial class RecordingControlsViewModel : ObservableObject
{
    [ObservableProperty]
    private RecordingState _state = RecordingState.Stopped;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private string _currentFilename = string.Empty;

    [ObservableProperty]
    private EncodingPreset _selectedPreset = EncodingPreset.Medium;

    public bool IsRecording => State == RecordingState.Recording;
    public bool IsPaused => State == RecordingState.Paused;
    public bool CanStart => State == RecordingState.Stopped;
    public bool CanStop => State is RecordingState.Recording or RecordingState.Paused;
}
