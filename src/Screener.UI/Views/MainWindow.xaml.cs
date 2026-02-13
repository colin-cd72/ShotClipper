using System.Windows;
using System.Windows.Input;
using Screener.UI.ViewModels;

namespace Screener.UI.Views;

public partial class MainWindow : Window
{
    private Point _dragStartPoint;
    private bool _isDragging;

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
            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
            vm.SelectInputCommand.Execute(input);
        }
    }

    private void OnInputThumbnailDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging)
            return;

        var pos = e.GetPosition(null);
        var diff = pos - _dragStartPoint;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.Tag is InputViewModel input)
            {
                _isDragging = true;
                DragDrop.DoDragDrop(fe, new DataObject(typeof(InputViewModel), input), DragDropEffects.Move);
                _isDragging = false;
            }
        }
    }

    private void OnInputThumbnailDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(InputViewModel)))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnInputThumbnailDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is InputViewModel target &&
            e.Data.GetData(typeof(InputViewModel)) is InputViewModel source &&
            DataContext is MainViewModel vm && source != target)
        {
            var inputs = vm.InputConfiguration.Inputs;
            int oldIndex = inputs.IndexOf(source);
            int newIndex = inputs.IndexOf(target);
            if (oldIndex >= 0 && newIndex >= 0)
                inputs.Move(oldIndex, newIndex);
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

            var audioLockItem = new System.Windows.Controls.MenuItem
            {
                Header = "Lock Audio to This Source",
                IsCheckable = true,
                IsChecked = vm.IsAudioLocked && vm.AudioLockedInput == input
            };
            audioLockItem.Click += (s, _) =>
            {
                if (vm.IsAudioLocked && vm.AudioLockedInput == input)
                    vm.UnlockAudio();
                else
                    vm.LockAudioToSource(input);
            };
            menu.Items.Add(audioLockItem);

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
