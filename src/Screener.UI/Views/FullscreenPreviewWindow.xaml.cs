using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Screener.UI.Views;

public partial class FullscreenPreviewWindow : Window
{
    public static readonly DependencyProperty PreviewImageProperty =
        DependencyProperty.Register(
            nameof(PreviewImage),
            typeof(ImageSource),
            typeof(FullscreenPreviewWindow),
            new PropertyMetadata(null));

    public ImageSource? PreviewImage
    {
        get => (ImageSource?)GetValue(PreviewImageProperty);
        set => SetValue(PreviewImageProperty, value);
    }

    public FullscreenPreviewWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.F11)
        {
            Close();
        }
    }
}
