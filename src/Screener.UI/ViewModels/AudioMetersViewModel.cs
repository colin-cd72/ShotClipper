using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Screener.Preview;

namespace Screener.UI.ViewModels;

public partial class AudioMetersViewModel : ObservableObject
{
    private readonly AudioPreviewService _audioPreviewService;
    private readonly ILogger<AudioMetersViewModel> _logger;
    private DateTime _lastPeakReset = DateTime.UtcNow;
    private const double PeakHoldSeconds = 2.0;
    private const double PeakDecayDbPerSecond = 20.0;
    private int _updateCount;

    public ObservableCollection<AudioChannelViewModel> Channels { get; } = new();

    [ObservableProperty]
    private AudioMeterType _meterType = AudioMeterType.PPM;

    [ObservableProperty]
    private double _referenceLevel = -18.0;

    [ObservableProperty]
    private double _masterVolume = 1.0;

    [ObservableProperty]
    private bool _showFirstBank = true;

    [ObservableProperty]
    private bool _showSecondBank = false;

    public double MasterVolumeDb => MasterVolume > 0 ? 20.0 * Math.Log10(MasterVolume) : -60.0;

    public IEnumerable<AudioChannelViewModel> DisplayChannels =>
        ShowFirstBank ? Channels.Take(8) : Channels.Skip(8).Take(8);

    partial void OnMasterVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(MasterVolumeDb));
    }

    partial void OnShowFirstBankChanged(bool value)
    {
        if (value) ShowSecondBank = false;
        OnPropertyChanged(nameof(DisplayChannels));
    }

    partial void OnShowSecondBankChanged(bool value)
    {
        if (value) ShowFirstBank = false;
        OnPropertyChanged(nameof(DisplayChannels));
    }

    public AudioMetersViewModel(AudioPreviewService audioPreviewService, ILogger<AudioMetersViewModel> logger)
    {
        _audioPreviewService = audioPreviewService;
        _logger = logger;

        // Initialize with 16 channels (8 stereo pairs for SDI embedded audio)
        for (int i = 0; i < 16; i++)
        {
            Channels.Add(new AudioChannelViewModel
            {
                ChannelNumber = i + 1,
                Label = $"{i + 1}",
                Level = -60.0
            });
        }

        // Subscribe to audio level updates
        _audioPreviewService.AudioLevelUpdated += OnAudioLevelUpdated;
        _logger.LogInformation("AudioMetersViewModel initialized, subscribed to AudioLevelUpdated");
    }

    private void OnAudioLevelUpdated(object? sender, AudioLevelEventArgs e)
    {
        _updateCount++;
        if (_updateCount <= 5 || _updateCount % 500 == 0)
        {
            _logger.LogInformation("Audio level update #{Count}: {Channels} channels, first level={Level:F1}dB",
                _updateCount, e.LevelsDb.Length, e.LevelsDb.Length > 0 ? e.LevelsDb[0] : -99);
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var now = DateTime.UtcNow;

            for (int i = 0; i < e.LevelsDb.Length && i < Channels.Count; i++)
            {
                var channel = Channels[i];
                var newLevel = e.LevelsDb[i];

                // Smooth decay for PPM-style ballistics (fast attack, slow release)
                if (newLevel > channel.Level)
                {
                    // Fast attack
                    channel.Level = newLevel;
                }
                else
                {
                    // Slow decay (~20dB/second)
                    var decay = PeakDecayDbPerSecond * 0.033; // ~30fps update rate
                    channel.Level = Math.Max(newLevel, channel.Level - decay);
                }

                // Peak hold with timeout
                if (newLevel >= channel.PeakHold)
                {
                    channel.PeakHold = newLevel;
                    channel.LastPeakTime = now;
                }
                else if ((now - channel.LastPeakTime).TotalSeconds > PeakHoldSeconds)
                {
                    // Decay peak after hold time
                    channel.PeakHold = Math.Max(-60.0, channel.PeakHold - PeakDecayDbPerSecond * 0.033);
                }

                // Clipping detection (above -1dB)
                channel.IsClipping = newLevel > -1.0;

                // Update normalized values for UI binding
                channel.OnPropertyChanged(nameof(AudioChannelViewModel.NormalizedLevel));
                channel.OnPropertyChanged(nameof(AudioChannelViewModel.NormalizedPeak));
                channel.OnPropertyChanged(nameof(AudioChannelViewModel.DisplayLevel));
                channel.OnPropertyChanged(nameof(AudioChannelViewModel.DisplayPeak));
            }
        });
    }
}

public partial class AudioChannelViewModel : ObservableObject
{
    [ObservableProperty]
    private int _channelNumber;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private double _level = -60.0;  // dBFS

    [ObservableProperty]
    private double _peakHold = -60.0;

    [ObservableProperty]
    private bool _isClipping;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isSolo;

    [ObservableProperty]
    private double _volume = 1.0;  // 0.0 to 4.0 (allows +12dB boost)

    public DateTime LastPeakTime { get; set; } = DateTime.UtcNow;

    // Volume in dB (-inf to +12dB)
    public double VolumeDb => Volume > 0 ? 20.0 * Math.Log10(Volume) : -60.0;

    // Volume fader position (0-1, dB-linear: 0=-60dB, 0.833=0dB, 1.0=+12dB)
    public double NormalizedVolume => Volume <= 0.001 ? 0 : Math.Clamp((VolumeDb + 60.0) / 72.0, 0, 1);

    // Normalized level for display (0-1)
    public double NormalizedLevel => Math.Clamp((Level + 60.0) / 60.0, 0, 1);
    public double NormalizedPeak => Math.Clamp((PeakHold + 60.0) / 60.0, 0, 1);

    // Display level applies volume adjustment
    public double DisplayLevel => IsMuted ? 0 : Math.Clamp(((Level + VolumeDb) + 60.0) / 60.0, 0, 1);
    public double DisplayPeak => IsMuted ? 0 : Math.Clamp(((PeakHold + VolumeDb) + 60.0) / 60.0, 0, 1);

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeDb));
        OnPropertyChanged(nameof(NormalizedVolume));
        OnPropertyChanged(nameof(DisplayLevel));
        OnPropertyChanged(nameof(DisplayPeak));
    }

    partial void OnIsMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayLevel));
        OnPropertyChanged(nameof(DisplayPeak));
    }

    public new void OnPropertyChanged(string propertyName) => base.OnPropertyChanged(propertyName);
}

public enum AudioMeterType
{
    VU,
    PPM,
    TruePeak
}
