using System.Windows;
using Screener.UI.ViewModels;

namespace Screener.UI.Views;

/// <summary>
/// Window for managing golfer profiles.
/// </summary>
public partial class GolferManagementWindow : Window
{
    public GolferManagementWindow(GolferManagementViewModel viewModel)
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
