using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Scheduling;
using Screener.Abstractions.Upload;

namespace Screener.UI.ViewModels;

/// <summary>
/// ViewModel for the scheduler window.
/// </summary>
public partial class SchedulerViewModel : ObservableObject
{
    private readonly ILogger<SchedulerViewModel> _logger;
    private readonly ISchedulingService _schedulingService;
    private readonly IUploadService _uploadService;

    [ObservableProperty]
    private ObservableCollection<ScheduleDisplayItem> _schedules = new();

    [ObservableProperty]
    private ScheduleDisplayItem? _selectedSchedule;

    [ObservableProperty]
    private EditableSchedule? _editingSchedule;

    [ObservableProperty]
    private ObservableCollection<EncodingPresetInfo> _encodingPresets = new();

    [ObservableProperty]
    private ObservableCollection<string> _recurrenceOptions = new()
    {
        "One Time",
        "Daily",
        "Weekly",
        "Monthly"
    };

    [ObservableProperty]
    private ObservableCollection<CloudProviderInfo> _cloudProviders = new();

    [ObservableProperty]
    private bool _hasConflict;

    [ObservableProperty]
    private string _conflictMessage = string.Empty;

    public SchedulerViewModel(
        ILogger<SchedulerViewModel> logger,
        ISchedulingService schedulingService,
        IUploadService uploadService)
    {
        _logger = logger;
        _schedulingService = schedulingService;
        _uploadService = uploadService;

        Initialize();
    }

    private void Initialize()
    {
        // Load encoding presets
        EncodingPresets.Add(new EncodingPresetInfo("Proxy", "720p H.264"));
        EncodingPresets.Add(new EncodingPresetInfo("Medium", "1080p H.264"));
        EncodingPresets.Add(new EncodingPresetInfo("High", "1080p H.264 High"));
        EncodingPresets.Add(new EncodingPresetInfo("Master", "Full resolution"));

        // Load cloud providers
        foreach (var provider in _uploadService.GetProviders())
        {
            CloudProviders.Add(new CloudProviderInfo(
                provider.ProviderId,
                provider.DisplayName,
                provider.IsConfigured));
        }

        // Load existing schedules
        RefreshSchedules();
    }

    private void RefreshSchedules()
    {
        Schedules.Clear();

        foreach (var schedule in _schedulingService.Schedules)
        {
            Schedules.Add(new ScheduleDisplayItem(schedule));
        }

        if (Schedules.Count > 0 && SelectedSchedule == null)
        {
            SelectedSchedule = Schedules[0];
        }
    }

    partial void OnSelectedScheduleChanged(ScheduleDisplayItem? value)
    {
        if (value != null)
        {
            EditingSchedule = new EditableSchedule(value);
            CheckForConflicts();
        }
        else
        {
            EditingSchedule = null;
        }
    }

    [RelayCommand]
    private void AddSchedule()
    {
        var newSchedule = new ScheduleDisplayItem(
            Guid.NewGuid(),
            "New Recording",
            DateTimeOffset.Now.AddHours(1),
            TimeSpan.FromHours(1),
            true);

        Schedules.Add(newSchedule);
        SelectedSchedule = newSchedule;

        _logger.LogInformation("Added new schedule");
    }

    [RelayCommand]
    private async Task RemoveSchedule()
    {
        if (SelectedSchedule == null)
            return;

        await _schedulingService.DeleteScheduleAsync(SelectedSchedule.Id);
        Schedules.Remove(SelectedSchedule);
        SelectedSchedule = Schedules.FirstOrDefault();

        _logger.LogInformation("Removed schedule");
    }

    [RelayCommand]
    private async Task SaveSchedule()
    {
        if (EditingSchedule == null || SelectedSchedule == null)
            return;

        var request = new ScheduledRecordingRequest(
            EditingSchedule.Name,
            EditingSchedule.GetStartDateTime(),
            EditingSchedule.GetDuration(),
            GetPresetFromInfo(EditingSchedule.SelectedPreset),
            EditingSchedule.FilenameTemplate,
            EditingSchedule.GetRecurrence(),
            EditingSchedule.AutoUpload,
            EditingSchedule.SelectedUploadProvider?.ProviderId,
            EditingSchedule.IsEnabled);

        if (SelectedSchedule.IsNew)
        {
            var created = await _schedulingService.CreateScheduleAsync(request);
            SelectedSchedule.Id = created.Id;
            SelectedSchedule.IsNew = false;
        }
        else
        {
            await _schedulingService.UpdateScheduleAsync(SelectedSchedule.Id, request);
        }

        // Update display item
        SelectedSchedule.Name = EditingSchedule.Name;
        SelectedSchedule.StartTime = EditingSchedule.GetStartDateTime();
        SelectedSchedule.Duration = EditingSchedule.GetDuration();
        SelectedSchedule.IsEnabled = EditingSchedule.IsEnabled;

        _logger.LogInformation("Saved schedule: {Name}", EditingSchedule.Name);
    }

    [RelayCommand]
    private void CancelEdit()
    {
        if (SelectedSchedule != null)
        {
            EditingSchedule = new EditableSchedule(SelectedSchedule);
        }
    }

    private async void CheckForConflicts()
    {
        if (EditingSchedule == null)
        {
            HasConflict = false;
            return;
        }

        var startTime = EditingSchedule.GetStartDateTime();
        var duration = EditingSchedule.GetDuration();
        var excludeId = SelectedSchedule?.IsNew == true ? (Guid?)null : SelectedSchedule?.Id;

        var conflicts = await _schedulingService.CheckConflictsAsync(startTime, duration, excludeId);

        HasConflict = conflicts.Count > 0;
        if (HasConflict)
        {
            var conflictNames = string.Join(", ", conflicts.Select(c => c.ExistingSchedule.Name));
            ConflictMessage = $"Overlaps with: {conflictNames}";
        }
    }

    private static EncodingPreset GetPresetFromInfo(EncodingPresetInfo? info)
    {
        if (info == null)
            return EncodingPreset.Medium;

        return info.Name switch
        {
            "Proxy" => EncodingPreset.Proxy,
            "High" => EncodingPreset.High,
            "Master" => EncodingPreset.Master,
            _ => EncodingPreset.Medium
        };
    }
}

/// <summary>
/// Display item for a scheduled recording.
/// </summary>
public partial class ScheduleDisplayItem : ObservableObject
{
    public Guid Id { get; set; }
    public bool IsNew { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private DateTimeOffset _startTime;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private bool _isEnabled;

    public string StartTimeFormatted => StartTime.ToString("ddd, MMM d, h:mm tt");
    public string DurationFormatted => $"{Duration.Hours}h {Duration.Minutes}m";
    public string StatusColor => IsEnabled ? "#22C55E" : "#666666";

    public ScheduleDisplayItem(ScheduledRecording recording)
    {
        Id = recording.Id;
        Name = recording.Name;
        StartTime = recording.StartTime;
        Duration = recording.Duration;
        IsEnabled = recording.IsEnabled;
        IsNew = false;
    }

    public ScheduleDisplayItem(Guid id, string name, DateTimeOffset startTime, TimeSpan duration, bool isEnabled)
    {
        Id = id;
        Name = name;
        StartTime = startTime;
        Duration = duration;
        IsEnabled = isEnabled;
        IsNew = true;
    }
}

/// <summary>
/// Editable schedule for the form.
/// </summary>
public partial class EditableSchedule : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private DateTime _startDate = DateTime.Today;

    [ObservableProperty]
    private string _startTime = "09:00";

    [ObservableProperty]
    private int _durationHours = 1;

    [ObservableProperty]
    private int _durationMinutes;

    [ObservableProperty]
    private int _durationSeconds;

    [ObservableProperty]
    private EncodingPresetInfo? _selectedPreset;

    [ObservableProperty]
    private string _filenameTemplate = "{date}_{time}_{name}";

    [ObservableProperty]
    private string _selectedRecurrence = "One Time";

    [ObservableProperty]
    private bool _monday;

    [ObservableProperty]
    private bool _tuesday;

    [ObservableProperty]
    private bool _wednesday;

    [ObservableProperty]
    private bool _thursday;

    [ObservableProperty]
    private bool _friday;

    [ObservableProperty]
    private bool _saturday;

    [ObservableProperty]
    private bool _sunday;

    [ObservableProperty]
    private bool _autoUpload;

    [ObservableProperty]
    private CloudProviderInfo? _selectedUploadProvider;

    [ObservableProperty]
    private bool _isEnabled = true;

    public bool ShowDaysOfWeek => SelectedRecurrence == "Weekly";

    public EditableSchedule()
    {
    }

    public EditableSchedule(ScheduleDisplayItem item)
    {
        Name = item.Name;
        StartDate = item.StartTime.DateTime.Date;
        StartTime = item.StartTime.ToString("HH:mm");
        DurationHours = (int)item.Duration.TotalHours;
        DurationMinutes = item.Duration.Minutes;
        DurationSeconds = item.Duration.Seconds;
        IsEnabled = item.IsEnabled;
    }

    public DateTimeOffset GetStartDateTime()
    {
        if (TimeSpan.TryParse(StartTime, out var time))
        {
            return new DateTimeOffset(StartDate.Add(time));
        }
        return new DateTimeOffset(StartDate);
    }

    public TimeSpan GetDuration()
    {
        return new TimeSpan(DurationHours, DurationMinutes, DurationSeconds);
    }

    public RecurrencePattern? GetRecurrence()
    {
        return SelectedRecurrence switch
        {
            "Daily" => new RecurrencePattern(RecurrenceType.Daily, 1),
            "Weekly" => new RecurrencePattern(RecurrenceType.Weekly, 1, GetSelectedDaysOfWeek()),
            "Monthly" => new RecurrencePattern(RecurrenceType.Monthly, 1),
            _ => null
        };
    }

    private DayOfWeek[] GetSelectedDaysOfWeek()
    {
        var days = new List<DayOfWeek>();
        if (Monday) days.Add(DayOfWeek.Monday);
        if (Tuesday) days.Add(DayOfWeek.Tuesday);
        if (Wednesday) days.Add(DayOfWeek.Wednesday);
        if (Thursday) days.Add(DayOfWeek.Thursday);
        if (Friday) days.Add(DayOfWeek.Friday);
        if (Saturday) days.Add(DayOfWeek.Saturday);
        if (Sunday) days.Add(DayOfWeek.Sunday);
        return days.ToArray();
    }

    partial void OnSelectedRecurrenceChanged(string value)
    {
        OnPropertyChanged(nameof(ShowDaysOfWeek));
    }
}
