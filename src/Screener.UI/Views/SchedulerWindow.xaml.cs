using System.Windows;
using Screener.UI.ViewModels;

namespace Screener.UI.Views;

/// <summary>
/// Window for managing scheduled recordings.
/// </summary>
public partial class SchedulerWindow : Window
{
    public SchedulerWindow(SchedulerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
