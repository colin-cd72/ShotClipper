using System.Windows;
using System.Windows.Media;

namespace Screener.UI.Views;

public partial class ColorPickerDialog : Window
{
    public byte SelectedR { get; private set; }
    public byte SelectedG { get; private set; }
    public byte SelectedB { get; private set; }

    public ColorPickerDialog()
    {
        InitializeComponent();
        UpdatePreview();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (PreviewBrush == null) return;

        var r = (byte)RedSlider.Value;
        var g = (byte)GreenSlider.Value;
        var b = (byte)BlueSlider.Value;

        PreviewBrush.Color = Color.FromRgb(r, g, b);
        HexText.Text = $"#{r:X2}{g:X2}{b:X2}";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SelectedR = (byte)RedSlider.Value;
        SelectedG = (byte)GreenSlider.Value;
        SelectedB = (byte)BlueSlider.Value;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
