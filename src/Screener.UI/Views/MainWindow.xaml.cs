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
}
