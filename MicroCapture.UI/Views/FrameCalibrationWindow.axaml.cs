using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MicroCapture.Processing;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class FrameCalibrationWindow : Window
{
    private const double HandleHitRadius = 10.0;
    private const double HandleSize = 9.0;

    // The active drag: which frame, and which handle (or Move for a whole-frame drag).
    // Move drags track the last pointer position (image-space) to compute per-move deltas,
    // since translating a rect is naturally delta-based while resizing an edge is absolute.
    private (int FrameIndex, FrameHandleKind Handle)? _activeDrag;
    private Point _lastImagePointer;

    public FrameCalibrationWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is FrameCalibrationViewModel vm)
        {
            vm.PropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName is nameof(vm.Image) or nameof(vm.SelectedFrameIndex))
                    RenderOverlay();
            };
            vm.Frames.CollectionChanged += (_, _) => RenderOverlay();
        }
    }

    private (Rect displayedRect, double scale) GetDisplayedImageRect()
    {
        var container = this.FindControl<Grid>("ContainerGrid");
        if (container == null || DataContext is not FrameCalibrationViewModel vm || vm.Image == null) return (default, 0);

        var containerW = container.Bounds.Width;
        var containerH = container.Bounds.Height;
        var imgW = vm.Image.Size.Width;
        var imgH = vm.Image.Size.Height;
        if (containerW <= 0 || containerH <= 0 || imgW <= 0 || imgH <= 0) return (default, 0);

        var scale = Math.Min(containerW / imgW, containerH / imgH);
        var dispW = imgW * scale;
        var dispH = imgH * scale;
        return (new Rect((containerW - dispW) / 2.0, (containerH - dispH) / 2.0, dispW, dispH), scale);
    }

    private static Point HandlePoint(FixedFrameRect rect, FrameHandleKind handle) => handle switch
    {
        FrameHandleKind.TopLeft => new Point(rect.X, rect.Y),
        FrameHandleKind.Top => new Point(rect.X + rect.Width / 2, rect.Y),
        FrameHandleKind.TopRight => new Point(rect.X + rect.Width, rect.Y),
        FrameHandleKind.Left => new Point(rect.X, rect.Y + rect.Height / 2),
        FrameHandleKind.Right => new Point(rect.X + rect.Width, rect.Y + rect.Height / 2),
        FrameHandleKind.BottomLeft => new Point(rect.X, rect.Y + rect.Height),
        FrameHandleKind.Bottom => new Point(rect.X + rect.Width / 2, rect.Y + rect.Height),
        FrameHandleKind.BottomRight => new Point(rect.X + rect.Width, rect.Y + rect.Height),
        _ => default
    };

    private static readonly FrameHandleKind[] AllHandles =
    {
        FrameHandleKind.TopLeft, FrameHandleKind.Top, FrameHandleKind.TopRight,
        FrameHandleKind.Left, FrameHandleKind.Right,
        FrameHandleKind.BottomLeft, FrameHandleKind.Bottom, FrameHandleKind.BottomRight
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not FrameCalibrationViewModel vm || vm.Image == null) return;

        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null) return;

        var pt = e.GetPosition(canvas);
        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        // Handles first (topmost frame's handles win on overlap), then interiors for a move —
        // iterate in reverse so the most recently added (visually topmost) frame wins.
        for (var i = vm.Frames.Count - 1; i >= 0; i--)
        {
            var rect = vm.Frames[i];
            foreach (var handle in AllHandles)
            {
                var hp = HandlePoint(rect, handle);
                var canvasPt = new Point(imgRect.X + hp.X * scale, imgRect.Y + hp.Y * scale);
                if (Distance(canvasPt, pt) <= HandleHitRadius)
                {
                    _activeDrag = (i, handle);
                    vm.SelectedFrameIndex = i;
                    e.Pointer.Capture(canvas);
                    return;
                }
            }
        }

        for (var i = vm.Frames.Count - 1; i >= 0; i--)
        {
            var rect = vm.Frames[i];
            var left = imgRect.X + rect.X * scale;
            var top = imgRect.Y + rect.Y * scale;
            var right = left + rect.Width * scale;
            var bottom = top + rect.Height * scale;
            if (pt.X >= left && pt.X <= right && pt.Y >= top && pt.Y <= bottom)
            {
                _activeDrag = (i, FrameHandleKind.Move);
                _lastImagePointer = new Point((pt.X - imgRect.X) / scale, (pt.Y - imgRect.Y) / scale);
                vm.SelectedFrameIndex = i;
                e.Pointer.Capture(canvas);
                return;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_activeDrag is not var (frameIndex, handle) || DataContext is not FrameCalibrationViewModel vm || vm.Image == null) return;
        if (frameIndex < 0 || frameIndex >= vm.Frames.Count) return;

        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null) return;

        var pt = e.GetPosition(canvas);
        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        var imageX = (pt.X - imgRect.X) / scale;
        var imageY = (pt.Y - imgRect.Y) / scale;

        if (handle == FrameHandleKind.Move)
        {
            var deltaX = imageX - _lastImagePointer.X;
            var deltaY = imageY - _lastImagePointer.Y;
            vm.Frames[frameIndex] = vm.MoveFrame(vm.Frames[frameIndex], deltaX, deltaY);
            _lastImagePointer = new Point(imageX, imageY);
        }
        else
        {
            vm.Frames[frameIndex] = vm.ResolveFrameResize(vm.Frames[frameIndex], handle, imageX, imageY);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activeDrag != null)
        {
            _activeDrag = null;
            e.Pointer.Capture(null);
        }
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void RenderOverlay()
    {
        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null || DataContext is not FrameCalibrationViewModel vm || vm.Image == null) return;

        canvas.Children.Clear();
        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        for (var i = 0; i < vm.Frames.Count; i++)
        {
            var rect = vm.Frames[i];
            var color = FixedFrameColorPalette.GetColor(i);
            var isSelected = i == vm.SelectedFrameIndex;

            var left = imgRect.X + rect.X * scale;
            var top = imgRect.Y + rect.Y * scale;

            var shape = new Rectangle
            {
                Width = rect.Width * scale,
                Height = rect.Height * scale,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = isSelected ? 3 : 2,
                StrokeDashArray = isSelected ? new AvaloniaList<double> { 6, 3 } : null,
                Fill = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)55 : (byte)30, color.R, color.G, color.B))
            };
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            canvas.Children.Add(shape);

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(color),
                Padding = new Thickness(5, 2),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold
            };
            Canvas.SetLeft(label, left + 4);
            Canvas.SetTop(label, top + 4);
            canvas.Children.Add(label);

            foreach (var handle in AllHandles)
            {
                var hp = HandlePoint(rect, handle);
                AddHandle(canvas, imgRect.X + hp.X * scale, imgRect.Y + hp.Y * scale, color);
            }
        }
    }

    private void AddHandle(Canvas canvas, double cx, double cy, Color color)
    {
        var handle = new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2
        };
        Canvas.SetLeft(handle, cx - HandleSize / 2);
        Canvas.SetTop(handle, cy - HandleSize / 2);
        canvas.Children.Add(handle);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
