using Avalonia.Controls;
using Avalonia.Input;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Don't fire shortcuts when typing in text fields
        if (e.Source is TextBox) return;

        if (DataContext is MainWindowViewModel vm)
        {
            switch (e.Key)
            {
                case Key.Space:
                    vm.HandleKeyShortcut("Space");
                    e.Handled = true;
                    break;
                case Key.R:
                    vm.HandleKeyShortcut("R");
                    e.Handled = true;
                    break;
                case Key.A:
                    vm.HandleKeyShortcut("A");
                    e.Handled = true;
                    break;
            }
        }
    }
}