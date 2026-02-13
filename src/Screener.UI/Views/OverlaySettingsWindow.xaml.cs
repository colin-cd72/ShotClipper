using System.Windows;
using Screener.UI.ViewModels;

namespace Screener.UI.Views;

/// <summary>
/// Window for configuring overlay settings (logo bug and lower third).
/// </summary>
public partial class OverlaySettingsWindow : Window
{
    public OverlaySettingsWindow(OverlaySettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, result) =>
        {
            DialogResult = result;
            Close();
        };
    }
}
