using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // Redrawn alongside every new live-view frame (~30fps while connected) — cheap
            // (a handful of shapes) and means the overlay is never more than one frame stale
            // after a (re)calibration, without needing a second change-tracking path.
            vm.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName is nameof(vm.LiveViewImage) or nameof(vm.CurrentFixedFrames))
                    RenderFixedFrameOverlay();
            };
        }
    }

    /// <summary>Draws a read-only outline of the active batch's calibrated fixed frames over
    /// the live view, so the operator can position paper before pressing capture. The
    /// calibration image and the live-view stream are commonly different resolutions (e.g.
    /// MockCameraService's 3840x2160 capture vs. its 640x480 live feed), so frame rects are
    /// projected in two steps: calibration pixels -&gt; fraction of that image, then fraction -&gt;
    /// the live image's own displayed rect (accounting for Stretch="Uniform" letterboxing).</summary>
    private void RenderFixedFrameOverlay()
    {
        var canvas = this.FindControl<Canvas>("FixedFrameOverlayCanvas");
        var container = this.FindControl<Grid>("LiveViewContainer");
        if (canvas == null || container == null) return;

        canvas.Children.Clear();
        if (DataContext is not MainWindowViewModel vm) return;

        var frames = vm.CurrentFixedFrames;
        var liveImage = vm.LiveViewImage;
        if (frames.Length == 0 || vm.CurrentFixedFrameImageWidth <= 0 || vm.CurrentFixedFrameImageHeight <= 0 || liveImage == null)
            return;

        var containerW = container.Bounds.Width;
        var containerH = container.Bounds.Height;
        var imgW = liveImage.Size.Width;
        var imgH = liveImage.Size.Height;
        if (containerW <= 0 || containerH <= 0 || imgW <= 0 || imgH <= 0) return;

        var scale = Math.Min(containerW / imgW, containerH / imgH);
        var dispW = imgW * scale;
        var dispH = imgH * scale;
        var offsetX = (containerW - dispW) / 2.0;
        var offsetY = (containerH - dispH) / 2.0;

        for (var i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            var fx = f.X / vm.CurrentFixedFrameImageWidth;
            var fy = f.Y / vm.CurrentFixedFrameImageHeight;
            var fw = f.Width / vm.CurrentFixedFrameImageWidth;
            var fh = f.Height / vm.CurrentFixedFrameImageHeight;

            var left = offsetX + fx * dispW;
            var top = offsetY + fy * dispH;
            var width = Math.Max(0, fw * dispW);
            var height = Math.Max(0, fh * dispH);
            var color = FixedFrameColorPalette.GetColor(i);

            var outline = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2
            };
            Canvas.SetLeft(outline, left);
            Canvas.SetTop(outline, top);
            canvas.Children.Add(outline);

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(color),
                Padding = new Thickness(4, 1),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold
            };
            Canvas.SetLeft(label, left + 3);
            Canvas.SetTop(label, top + 3);
            canvas.Children.Add(label);
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

    private async void OnDeleteThumbnailClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.DataContext is MicroCapture.UI.ViewModels.ThumbnailItem item
                && DataContext is MicroCapture.UI.ViewModels.MainWindowViewModel vm)
            {
                await vm.DeleteCaptureAsync(item);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] OnDeleteThumbnailClick failed: {ex}");
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
