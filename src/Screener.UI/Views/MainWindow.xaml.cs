using System.Windows;
using System.Windows.Input;
using Screener.UI.ViewModels;

namespace Screener.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnSource1Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CutToSource1Command.Execute(null);
    }

    private void OnSource2Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CutToSource2Command.Execute(null);
    }

    private void OnInputThumbnailClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is InputViewModel input && DataContext is MainViewModel vm)
        {
            vm.SelectInputCommand.Execute(input);
        }
    }

    private void OnInputThumbnailRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is InputViewModel input && DataContext is MainViewModel vm)
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var enableItem = new System.Windows.Controls.MenuItem
            {
                Header = "Enable for Recording",
                IsCheckable = true,
                IsChecked = input.IsEnabled
            };
            enableItem.Click += (s, _) => input.IsEnabled = !input.IsEnabled;
            menu.Items.Add(enableItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var previewItem = new System.Windows.Controls.MenuItem { Header = "Set as Preview" };
            previewItem.Click += (s, _) => vm.SelectInputCommand.Execute(input);
            menu.Items.Add(previewItem);

            if (vm.IsGolfModeEnabled)
            {
                menu.Items.Add(new System.Windows.Controls.Separator());

                var golferItem = new System.Windows.Controls.MenuItem { Header = "Assign as Golfer Camera" };
                golferItem.Click += (s, _) => vm.AssignGolferCameraCommand.Execute(input);
                menu.Items.Add(golferItem);

                var simItem = new System.Windows.Controls.MenuItem { Header = "Assign as Simulator" };
                simItem.Click += (s, _) => vm.AssignSimulatorOutputCommand.Execute(input);
                menu.Items.Add(simItem);
            }

            menu.PlacementTarget = fe;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnAddVirtualSourceClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
