using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MicroCapture.UI.Views;

public partial class CropReviewWindow : Window
{
    public CropReviewWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
