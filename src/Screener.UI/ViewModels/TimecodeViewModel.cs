using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Timecode;

namespace Screener.UI.ViewModels;

public partial class TimecodeViewModel : ObservableObject
{
    private readonly ITimecodeService _timecodeService;
    private readonly ILogger<TimecodeViewModel> _logger;
    private readonly DispatcherTimer _updateTimer;

    [ObservableProperty]
    private Smpte12MTimecode _currentTimecode;

    [ObservableProperty]
    private string _source = "NTP";

    [ObservableProperty]
    private TimeZoneInfo _selectedTimezone = TimeZoneInfo.Local;

    [ObservableProperty]
    private bool _useDropFrame = true;

    [ObservableProperty]
    private FrameRate _frameRate = FrameRate.Fps29_97;

    public string FormattedTimecode => CurrentTimecode.ToString();

    public TimecodeViewModel(ITimecodeService timecodeService, ILogger<TimecodeViewModel> logger)
    {
        _timecodeService = timecodeService;
        _logger = logger;

        // Initialize timecode source from service
        Source = _timecodeService.CurrentProvider.Name;
        UseDropFrame = _timecodeService.UseDropFrame;
        SelectedTimezone = _timecodeService.Timezone;

        // Start timer to update timecode at frame rate (~30fps for smooth display)
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 updates per second
        };
        _updateTimer.Tick += OnTimerTick;
        _updateTimer.Start();

        _logger.LogInformation("TimecodeViewModel initialized, updating at {Interval}ms", _updateTimer.Interval.TotalMilliseconds);
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            CurrentTimecode = await _timecodeService.GetCurrentTimecodeAsync(FrameRate);
            OnPropertyChanged(nameof(FormattedTimecode));
        }
        catch (Exception ex)
        {
            // Don't log every frame - only occasional errors
            System.Diagnostics.Debug.WriteLine($"Timecode update failed: {ex.Message}");
        }
    }

    public void SetFrameRate(double fps)
    {
        // Map common frame rates to their proper numerator/denominator
        FrameRate = fps switch
        {
            <= 23.98 and >= 23.97 => FrameRate.Fps23_976,
            24.0 => FrameRate.Fps24,
            25.0 => FrameRate.Fps25,
            <= 29.98 and >= 29.96 => FrameRate.Fps29_97,
            30.0 => FrameRate.Fps30,
            50.0 => FrameRate.Fps50,
            <= 59.95 and >= 59.93 => FrameRate.Fps59_94,
            60.0 => FrameRate.Fps60,
            _ => new FrameRate((int)(fps * 1000), 1000)
        };
    }

    public void Stop()
    {
        _updateTimer.Stop();
    }
}
