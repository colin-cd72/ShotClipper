using System.Windows;
using Screener.UI.ViewModels;

namespace Screener.UI.Views;

/// <summary>
/// Settings window for application configuration.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, result) =>
        {
            DialogResult = result;
            Close();
        };
    }

    public void SelectTab(int tabIndex)
    {
        if (tabIndex < SettingsTabControl.Items.Count)
            SettingsTabControl.SelectedIndex = tabIndex;
    }
}
