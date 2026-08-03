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
                // Fast, reliable behavior: cycle to the next option on each click.
                var opts = item.Options;
                if (opts == null || opts.Count == 0) return;
                var current = item.SelectedOption;
                int idx = -1;
                if (current != null)
                {
                    for (int i = 0; i < opts.Count; i++)
                    {
                        if (opts[i].Value == current.Value)
                        {
                            idx = i; break;
                        }
                    }
                }
                var next = opts[(idx + 1) % opts.Count];
                item.SelectedOption = next;
                Console.WriteLine($"Cycled {item.DisplayName} -> {next.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CameraControl button click failed: {ex}");
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        try
        {
            var p = e.GetPosition(this);
            Console.WriteLine($"PointerPressed at {p.X:F1},{p.Y:F1} source={e.Source?.GetType().Name}");
        }
        catch { }
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        try
        {
            var p = e.GetPosition(this);
            Console.WriteLine($"PointerReleased at {p.X:F1},{p.Y:F1} source={e.Source?.GetType().Name}");
        }
        catch { }
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
                case Key.F2:
                    // Debug: cycle every camera control to the next option (fallback when mouse is blocked)
                    try
                    {
                        foreach (var ctrl in vm.CameraControls)
                        {
                            var opts = ctrl.Options;
                            if (opts == null || opts.Count == 0) continue;
                            var current = ctrl.SelectedOption;
                            int idx = -1;
                            if (current != null)
                            {
                                for (int i = 0; i < opts.Count; i++) if (opts[i].Value == current.Value) { idx = i; break; }
                            }
                            var next = opts[(idx + 1) % opts.Count];
                            ctrl.SelectedOption = next;
                            Console.WriteLine($"[F2] Cycled {ctrl.DisplayName} -> {next.DisplayName}");
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"[F2] cycle failed: {ex}"); }
                    e.Handled = true;
                    break;
            }
        }
    }
}
