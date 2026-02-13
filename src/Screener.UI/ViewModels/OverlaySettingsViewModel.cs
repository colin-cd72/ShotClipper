using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Screener.Golf.Overlays;
using Screener.Golf.Persistence;

namespace Screener.UI.ViewModels;

/// <summary>
/// ViewModel for the overlay settings dialog (logo bug + lower third configuration).
/// </summary>
public partial class OverlaySettingsViewModel : ObservableObject
{
    private readonly OverlayRepository _overlayRepository;
    private readonly ILogger<OverlaySettingsViewModel> _logger;

    // Logo Bug
    [ObservableProperty]
    private string _logoPath = string.Empty;

    [ObservableProperty]
    private LogoPosition _logoPosition = LogoPosition.TopRight;

    [ObservableProperty]
    private double _logoScale = 1.0;

    [ObservableProperty]
    private int _logoMargin = 20;

    // Lower Third
    [ObservableProperty]
    private bool _lowerThirdEnabled = true;

    [ObservableProperty]
    private int _fontSize = 48;

    [ObservableProperty]
    private string _fontColor = "white";

    [ObservableProperty]
    private string _fontFamily = "Arial";

    [ObservableProperty]
    private string _boxColor = "black@0.6";

    [ObservableProperty]
    private bool _showBox = true;

    [ObservableProperty]
    private int _boxPadding = 10;

    [ObservableProperty]
    private int _lowerThirdX = 40;

    [ObservableProperty]
    private int _yFromBottom = 80;

    public LogoPosition[] AvailablePositions { get; } = Enum.GetValues<LogoPosition>();

    /// <summary>Fired to close the dialog window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public OverlaySettingsViewModel(
        OverlayRepository overlayRepository,
        ILogger<OverlaySettingsViewModel> logger)
    {
        _overlayRepository = overlayRepository;
        _logger = logger;

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // Load logo bug config
            var logoBugRecord = await _overlayRepository.GetDefaultAsync("logo_bug");
            if (logoBugRecord != null)
            {
                var config = logoBugRecord.DeserializeConfig<LogoBugConfig>();
                if (config != null)
                {
                    LogoPath = config.LogoPath ?? string.Empty;
                    LogoPosition = config.Position;
                    LogoScale = config.Scale;
                    LogoMargin = config.Margin;
                }
            }

            // Load lower third config
            var lowerThirdRecord = await _overlayRepository.GetDefaultAsync("lower_third");
            if (lowerThirdRecord != null)
            {
                var config = lowerThirdRecord.DeserializeConfig<LowerThirdConfig>();
                if (config != null)
                {
                    LowerThirdEnabled = config.Enabled;
                    FontSize = config.FontSize;
                    FontColor = config.FontColor;
                    FontFamily = config.FontFamily;
                    BoxColor = config.BoxColor;
                    ShowBox = config.ShowBox;
                    BoxPadding = config.BoxPadding;
                    LowerThirdX = config.X;
                    YFromBottom = config.YFromBottom;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load overlay settings");
        }
    }

    [RelayCommand]
    private void BrowseLogo()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Logo Image",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp|PNG Files|*.png|All Files|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            LogoPath = dlg.FileName;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            // Save logo bug config
            var logoBugConfig = new LogoBugConfig
            {
                LogoPath = string.IsNullOrWhiteSpace(LogoPath) ? null : LogoPath,
                Position = LogoPosition,
                Scale = LogoScale,
                Margin = LogoMargin
            };

            var logoBugRecord = await _overlayRepository.GetDefaultAsync("logo_bug");
            if (logoBugRecord == null)
            {
                logoBugRecord = new OverlayConfigRecord
                {
                    Name = "Default Logo Bug",
                    Type = "logo_bug",
                    IsDefault = true
                };
            }
            logoBugRecord.SerializeConfig(logoBugConfig);
            logoBugRecord.AssetPath = logoBugConfig.LogoPath;
            await _overlayRepository.SaveAsync(logoBugRecord);

            // Save lower third config
            var lowerThirdConfig = new LowerThirdConfig
            {
                Enabled = LowerThirdEnabled,
                FontSize = FontSize,
                FontColor = FontColor,
                FontFamily = FontFamily,
                BoxColor = BoxColor,
                ShowBox = ShowBox,
                BoxPadding = BoxPadding,
                X = LowerThirdX,
                YFromBottom = YFromBottom
            };

            var lowerThirdRecord = await _overlayRepository.GetDefaultAsync("lower_third");
            if (lowerThirdRecord == null)
            {
                lowerThirdRecord = new OverlayConfigRecord
                {
                    Name = "Default Lower Third",
                    Type = "lower_third",
                    IsDefault = true
                };
            }
            lowerThirdRecord.SerializeConfig(lowerThirdConfig);
            await _overlayRepository.SaveAsync(lowerThirdRecord);

            _logger.LogInformation("Overlay settings saved");
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save overlay settings");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, false);
    }
}
