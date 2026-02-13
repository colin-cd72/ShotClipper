using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screener.UI.ViewModels;

namespace Screener.UI.Views.Controls;

public partial class ProgramMonitorControl : UserControl
{
    private bool _isDraggingLogo;
    private bool _isDraggingLowerThird;
    private Point _dragOffset;

    public ProgramMonitorControl()
    {
        InitializeComponent();
    }

    private Point GetCanvasPosition(MouseEventArgs e)
    {
        // Convert mouse position from control space to canvas (1920x1080) space
        var mousePos = e.GetPosition(ProgramCanvas);
        return mousePos;
    }

    // Logo drag handlers
    private void OnLogoDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        _isDraggingLogo = true;
        var pos = GetCanvasPosition(e);
        var vm = DataContext as SwitcherViewModel;
        if (vm != null)
        {
            _dragOffset = new Point(pos.X - vm.LogoX, pos.Y - vm.LogoY);
        }
        element.CaptureMouse();
        e.Handled = true;
    }

    private void OnLogoDragMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingLogo) return;
        var pos = GetCanvasPosition(e);
        if (DataContext is SwitcherViewModel vm)
        {
            vm.LogoX = Math.Clamp(pos.X - _dragOffset.X, 0, 1920 - vm.LogoDisplayWidth);
            vm.LogoY = Math.Clamp(pos.Y - _dragOffset.Y, 0, 1080 - vm.LogoDisplayHeight);
        }
        e.Handled = true;
    }

    private void OnLogoDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingLogo) return;
        _isDraggingLogo = false;
        if (sender is FrameworkElement element)
            element.ReleaseMouseCapture();

        // Save position
        if (DataContext is SwitcherViewModel vm)
            _ = vm.SaveOverlayPositionsAsync();

        e.Handled = true;
    }

    // Lower third drag handlers
    private void OnLowerThirdDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        _isDraggingLowerThird = true;
        var pos = GetCanvasPosition(e);
        if (DataContext is SwitcherViewModel vm)
        {
            _dragOffset = new Point(pos.X - vm.LowerThirdX, pos.Y - vm.LowerThirdY);
        }
        element.CaptureMouse();
        e.Handled = true;
    }

    private void OnLowerThirdDragMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingLowerThird) return;
        var pos = GetCanvasPosition(e);
        if (DataContext is SwitcherViewModel vm)
        {
            vm.LowerThirdX = Math.Clamp(pos.X - _dragOffset.X, 0, 1800);
            vm.LowerThirdY = Math.Clamp(pos.Y - _dragOffset.Y, 0, 1050);
        }
        e.Handled = true;
    }

    private void OnLowerThirdDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingLowerThird) return;
        _isDraggingLowerThird = false;
        if (sender is FrameworkElement element)
            element.ReleaseMouseCapture();

        // Save position
        if (DataContext is SwitcherViewModel vm)
            _ = vm.SaveOverlayPositionsAsync();

        e.Handled = true;
    }
}
