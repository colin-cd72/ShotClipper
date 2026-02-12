using System.Windows;
using System.Windows.Controls;
using Screener.Abstractions.Timecode;

namespace Screener.UI.Views.Controls;

public partial class TimecodeDisplay : UserControl
{
    public static readonly DependencyProperty TimecodeProperty =
        DependencyProperty.Register(
            nameof(Timecode),
            typeof(Smpte12MTimecode),
            typeof(TimecodeDisplay),
            new PropertyMetadata(default(Smpte12MTimecode), OnTimecodeChanged));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(
            nameof(FontSize),
            typeof(double),
            typeof(TimecodeDisplay),
            new PropertyMetadata(24.0, OnFontSizeChanged));

    public Smpte12MTimecode Timecode
    {
        get => (Smpte12MTimecode)GetValue(TimecodeProperty);
        set => SetValue(TimecodeProperty, value);
    }

    public new double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public TimecodeDisplay()
    {
        InitializeComponent();
    }

    private static void OnTimecodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimecodeDisplay control && e.NewValue is Smpte12MTimecode tc)
        {
            control.TimecodeText.Text = tc.ToString();
        }
    }

    private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimecodeDisplay control && e.NewValue is double size)
        {
            control.TimecodeText.FontSize = size;
        }
    }
}
