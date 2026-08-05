using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Windows.Input;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Global shortcuts must win even when a button currently has keyboard focus.
        // Avalonia's Button handles Space/Enter as its own "activate" key at the focused
        // control during the normal bubble phase, which otherwise consumes the event before
        // it ever reaches OnKeyDown below — that's why Space was re-clicking whatever button
        // was last clicked instead of firing Capture. Intercepting during the tunnel phase
        // (root to focused control, before the focused control's own handling runs) fixes it.
        AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
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

    private void OnExportFormatButtonClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is MicroCapture.UI.ViewModels.MainWindowViewModel vm)
            {
                var formats = new[] { "PDF", "TIFF", "JPG", "PNG" };
                var current = vm.ExportFormat ?? "PDF";
                var idx = Array.IndexOf(formats, current);
                var next = formats[(idx + 1) % formats.Length];
                vm.ExportFormat = next;
                Console.WriteLine($"Cycled export format -> {next}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"ExportFormat click failed: {ex}"); }
    }

    private void OnThumbnailClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn)
            {
                var jobId = btn.Tag as string ?? (btn.DataContext as global::MicroCapture.UI.ViewModels.ThumbnailItem)?.JobId;
                if (!string.IsNullOrEmpty(jobId) && DataContext is MicroCapture.UI.ViewModels.MainWindowViewModel vm)
                {
                    Console.WriteLine($"[Thumbnail] Opening review for {jobId}");
                    
                    // Invoke the generated ReviewCropCommand if available to keep ViewModel encapsulation
                    try
                    {
                        var cmdProp = vm.GetType().GetProperty("ReviewCropCommand");
                        if (cmdProp != null)
                        {
                            var cmd = cmdProp.GetValue(vm) as System.Windows.Input.ICommand;
                            if (cmd != null)
                            {
                                Console.WriteLine($"[Thumbnail] ReviewCropCommand found, CanExecute={cmd.CanExecute(jobId)}");
                                if (cmd.CanExecute(jobId))
                                {
                                    cmd.Execute(jobId);
                                    Console.WriteLine($"[Thumbnail] Command executed");
                                }
                                else
                                {
                                    Console.WriteLine($"[Thumbnail] Command CanExecute returned false");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[Thumbnail] ReviewCropCommand property is null");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[Thumbnail] ReviewCropCommand property not found");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Thumbnail] Command invoke failed: {ex}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] OnThumbnailClick failed: {ex}");
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            await vm.ShutdownAsync();
        base.OnClosed(e);
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
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
