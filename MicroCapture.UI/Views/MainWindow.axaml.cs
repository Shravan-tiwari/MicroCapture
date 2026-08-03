using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnCameraControlButtonClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.DataContext is CameraControlItem item)
            {
                // Build a simple ContextMenu from the available options.
                var menu = new ContextMenu();
                var menuItems = new List<MenuItem>();
                foreach (var opt in item.Options)
                {
                    var mi = new MenuItem { Header = opt.DisplayName };
                    var captured = opt; // capture loop variable
                    mi.Click += (_, __) =>
                    {
                        try { item.SelectedOption = captured; }
                        catch (Exception ex) { Console.Error.WriteLine($"Menu item click failed: {ex}"); }
                    };
                    menuItems.Add(mi);
                }
                foreach (var mi in menuItems)
                    menu.Items.Add(mi);
                btn.ContextMenu = menu;
                // Open next tick to ensure the menu is attached
                Avalonia.Threading.Dispatcher.UIThread.Post(() => menu.Open(btn));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CameraControl menu creation failed: {ex}");
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.ShutdownAsync();
        base.OnClosed(e);
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
