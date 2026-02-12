using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Screener.UI.Views.Controls;

public partial class DriveSpaceIndicator : UserControl
{
    public static readonly DependencyProperty UsedPercentProperty =
        DependencyProperty.Register(
            nameof(UsedPercent),
            typeof(double),
            typeof(DriveSpaceIndicator),
            new PropertyMetadata(0.0, OnUsedPercentChanged));

    public double UsedPercent
    {
        get => (double)GetValue(UsedPercentProperty);
        set => SetValue(UsedPercentProperty, value);
    }

    public DriveSpaceIndicator()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private static void OnUsedPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DriveSpaceIndicator control)
        {
            control.UpdateIndicator();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateIndicator();
    }

    private void UpdateIndicator()
    {
        var percent = Math.Clamp(UsedPercent, 0, 100);
        PercentText.Text = $"{percent:F0}%";

        // Update arc
        var centerX = ActualWidth / 2;
        var centerY = ActualHeight / 2;
        var radius = Math.Min(centerX, centerY) - 4;

        if (radius <= 0) return;

        var angle = percent / 100.0 * 360;
        var startAngle = -90; // Start from top
        var endAngle = startAngle + angle;

        var startRad = startAngle * Math.PI / 180;
        var endRad = endAngle * Math.PI / 180;

        var startX = centerX + radius * Math.Cos(startRad);
        var startY = centerY + radius * Math.Sin(startRad);
        var endX = centerX + radius * Math.Cos(endRad);
        var endY = centerY + radius * Math.Sin(endRad);

        var largeArc = angle > 180;

        var pathGeometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = new Point(startX, startY),
            IsClosed = false
        };

        figure.Segments.Add(new ArcSegment(
            new Point(endX, endY),
            new Size(radius, radius),
            0,
            largeArc,
            SweepDirection.Clockwise,
            true));

        pathGeometry.Figures.Add(figure);
        ProgressArc.Data = pathGeometry;

        // Update color based on usage
        if (percent > 90)
            ProgressArc.Stroke = (Brush)FindResource("AccentRedBrush");
        else if (percent > 75)
            ProgressArc.Stroke = (Brush)FindResource("AccentYellowBrush");
        else
            ProgressArc.Stroke = (Brush)FindResource("AccentGreenBrush");
    }
}
