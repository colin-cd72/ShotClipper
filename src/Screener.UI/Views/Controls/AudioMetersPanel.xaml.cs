using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screener.UI.ViewModels;

namespace Screener.UI.Views.Controls;

public partial class AudioMetersPanel : UserControl
{
    private AudioChannelViewModel? _draggingChannel;
    private FrameworkElement? _draggingElement;

    public AudioMetersPanel()
    {
        InitializeComponent();
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    /// <summary>
    /// Scroll wheel on a channel's meter area adjusts that channel's volume.
    /// Each notch = ~0.5 dB. Shift+scroll for fine ~0.1 dB steps.
    /// </summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var channel = FindChannelUnderMouse(e.OriginalSource);
        if (channel == null) return;

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.012 : 0.06;
        double delta = e.Delta > 0 ? step : -step;
        channel.Volume = Math.Clamp(channel.Volume + delta, 0, 4);
        e.Handled = true;
    }

    /// <summary>
    /// Mouse down on a meter bar: start drag to adjust volume, or double-click to reset to 0 dB.
    /// </summary>
    private void MeterBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (element.DataContext is not AudioChannelViewModel channel) return;

        if (e.ClickCount == 2)
        {
            // Double-click: reset to unity (0 dB)
            channel.Volume = 1.0;
            e.Handled = true;
            return;
        }

        // Start drag
        _draggingChannel = channel;
        _draggingElement = element;
        element.CaptureMouse();

        var pos = e.GetPosition(element);
        SetVolumeFromMouseY(channel, element.ActualHeight, pos.Y);
        e.Handled = true;
    }

    private void MeterBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingChannel == null || _draggingElement == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(_draggingElement);
        SetVolumeFromMouseY(_draggingChannel, _draggingElement.ActualHeight, pos.Y);
        e.Handled = true;
    }

    private void MeterBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingElement != null)
        {
            _draggingElement.ReleaseMouseCapture();
            _draggingElement = null;
            _draggingChannel = null;
        }
    }

    /// <summary>
    /// Convert a mouse Y position within the meter to a volume value.
    /// Top of meter = +12 dB, bottom = silence.
    /// </summary>
    private static void SetVolumeFromMouseY(AudioChannelViewModel channel, double meterHeight, double mouseY)
    {
        if (meterHeight <= 0) return;

        // 0 = top (+12dB), meterHeight = bottom (silence)
        var normalized = Math.Clamp(1.0 - mouseY / meterHeight, 0, 1);

        if (normalized < 0.015)
        {
            channel.Volume = 0;
            return;
        }

        // Reverse the NormalizedVolume mapping:
        // NormalizedVolume = (VolumeDb + 60) / 72
        // VolumeDb = normalized * 72 - 60
        // Volume = 10^(VolumeDb / 20)
        var volumeDb = normalized * 72.0 - 60.0;
        var volume = Math.Pow(10, volumeDb / 20.0);
        channel.Volume = Math.Clamp(volume, 0, 4);
    }

    private static AudioChannelViewModel? FindChannelUnderMouse(object originalSource)
    {
        var element = originalSource as DependencyObject;
        while (element != null)
        {
            if (element is FrameworkElement fe && fe.DataContext is AudioChannelViewModel channel)
                return channel;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
