using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MicroCapture.Processing;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class CropReviewWindow : Window
{
    private enum DragTarget
    {
        None, Corner0, Corner1, Corner2, Corner3, SplitLine,
        LeftCorner0, LeftCorner1, LeftCorner2, LeftCorner3,
        RightCorner0, RightCorner1, RightCorner2, RightCorner3
    }

    private const double HandleHitRadius = 16.0;
    private const double SplitLineHitMargin = 12.0;
    private static readonly Color SingleQuadColor = Color.Parse("#00d2ff");
    private static readonly Color LeftQuadColor = Color.Parse("#00d2ff");
    private static readonly Color RightQuadColor = Color.Parse("#7dd3fc");

    private DragTarget _activeDrag = DragTarget.None;

    public CropReviewWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is CropReviewViewModel vm)
        {
            vm.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName is nameof(vm.TopLeft) or nameof(vm.TopRight) or nameof(vm.BottomRight) or nameof(vm.BottomLeft)
                    or nameof(vm.SplitPercent) or nameof(vm.Image) or nameof(vm.IsSplitBookPages)
                    or nameof(vm.LeftQuad) or nameof(vm.RightQuad) or nameof(vm.IsTwoQuadSplit))
                {
                    RenderOverlay();
                }
            };
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not CropReviewViewModel vm || vm.Image == null) return;

        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null) return;

        var pt = e.GetPosition(canvas);
        var (imgRect, scale) = GetDisplayedImageRect();
        if (imgRect.Width <= 0 || imgRect.Height <= 0) return;

        if (vm.IsSplitBookPages)
        {
            if (vm.IsTwoQuadSplit)
            {
                if (TryHitQuad(vm.LeftQuad, imgRect, scale, pt, out var leftIdx))
                {
                    _activeDrag = DragTarget.LeftCorner0 + leftIdx;
                    e.Pointer.Capture(canvas);
                    return;
                }
                if (TryHitQuad(vm.RightQuad, imgRect, scale, pt, out var rightIdx))
                {
                    _activeDrag = DragTarget.RightCorner0 + rightIdx;
                    e.Pointer.Capture(canvas);
                }
            }
            else
            {
                var lineX = imgRect.X + vm.ImageWidth * (vm.SplitPercent / 100.0) * scale;
                if (Math.Abs(pt.X - lineX) <= SplitLineHitMargin)
                {
                    _activeDrag = DragTarget.SplitLine;
                    e.Pointer.Capture(canvas);
                }
            }
            return;
        }

        var corners = new[] { vm.TopLeft, vm.TopRight, vm.BottomRight, vm.BottomLeft };
        if (TryHitQuad(corners, imgRect, scale, pt, out var idx))
        {
            _activeDrag = DragTarget.Corner0 + idx;
            e.Pointer.Capture(canvas);
        }
    }

    private static bool TryHitQuad(CropPoint[] corners, Rect imgRect, double scale, Point pointerPos, out int index)
    {
        for (var i = 0; i < corners.Length; i++)
        {
            var canvasPt = new Point(imgRect.X + corners[i].X * scale, imgRect.Y + corners[i].Y * scale);
            if (Distance(canvasPt, pointerPos) <= HandleHitRadius) { index = i; return true; }
        }
        index = -1;
        return false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_activeDrag == DragTarget.None || DataContext is not CropReviewViewModel vm || vm.Image == null) return;

        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null) return;

        var pt = e.GetPosition(canvas);
        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        if (_activeDrag == DragTarget.SplitLine)
        {
            var x = Math.Clamp((pt.X - imgRect.X) / scale, 1, Math.Max(1, vm.ImageWidth - 1));
            vm.SplitPercent = Math.Clamp(x / vm.ImageWidth * 100.0, 1.0, 99.0);
            return;
        }

        var rawX = Math.Clamp((pt.X - imgRect.X) / scale, 0, vm.ImageWidth);
        var rawY = Math.Clamp((pt.Y - imgRect.Y) / scale, 0, vm.ImageHeight);
        var rawPoint = new CropPoint(rawX, rawY);

        if (_activeDrag >= DragTarget.LeftCorner0 && _activeDrag <= DragTarget.LeftCorner3)
        {
            var idx = _activeDrag - DragTarget.LeftCorner0;
            var resolved = vm.ResolveSplitCornerDrag(isLeftPage: true, idx, rawPoint);
            var arr = (CropPoint[])vm.LeftQuad.Clone();
            arr[idx] = resolved;
            vm.LeftQuad = arr;
            return;
        }

        if (_activeDrag >= DragTarget.RightCorner0 && _activeDrag <= DragTarget.RightCorner3)
        {
            var idx = _activeDrag - DragTarget.RightCorner0;
            var resolved = vm.ResolveSplitCornerDrag(isLeftPage: false, idx, rawPoint);
            var arr = (CropPoint[])vm.RightQuad.Clone();
            arr[idx] = resolved;
            vm.RightQuad = arr;
            return;
        }

        var cornerIndex = _activeDrag - DragTarget.Corner0;
        var resolvedSingle = vm.ResolveCornerDrag(cornerIndex, rawPoint);
        switch (cornerIndex)
        {
            case 0: vm.TopLeft = resolvedSingle; break;
            case 1: vm.TopRight = resolvedSingle; break;
            case 2: vm.BottomRight = resolvedSingle; break;
            case 3: vm.BottomLeft = resolvedSingle; break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activeDrag != DragTarget.None)
        {
            _activeDrag = DragTarget.None;
            e.Pointer.Capture(null);
        }
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private (Rect displayedRect, double scale) GetDisplayedImageRect()
    {
        var container = this.FindControl<Grid>("ContainerGrid");
        if (container == null || DataContext is not CropReviewViewModel vm || vm.Image == null) return (default, 0);

        var containerW = container.Bounds.Width;
        var containerH = container.Bounds.Height;
        var imgW = vm.Image.Size.Width;
        var imgH = vm.Image.Size.Height;

        if (containerW <= 0 || containerH <= 0 || imgW <= 0 || imgH <= 0) return (default, 0);

        var scale = Math.Min(containerW / imgW, containerH / imgH);
        var dispW = imgW * scale;
        var dispH = imgH * scale;
        var dispX = (containerW - dispW) / 2.0;
        var dispY = (containerH - dispH) / 2.0;

        return (new Rect(dispX, dispY, dispW, dispH), scale);
    }

    private void RenderOverlay()
    {
        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null || DataContext is not CropReviewViewModel vm || vm.Image == null) return;

        canvas.Children.Clear();

        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        if (vm.IsSplitBookPages && vm.IsTwoQuadSplit)
        {
            RenderQuad(canvas, vm.LeftQuad, imgRect, scale, LeftQuadColor);
            RenderQuad(canvas, vm.RightQuad, imgRect, scale, RightQuadColor);
        }
        else if (vm.IsSplitBookPages)
        {
            RenderSplitLine(canvas, vm, imgRect, scale);
        }
        else
        {
            RenderQuad(canvas, new[] { vm.TopLeft, vm.TopRight, vm.BottomRight, vm.BottomLeft }, imgRect, scale, SingleQuadColor);
        }
    }

    private void RenderQuad(Canvas canvas, CropPoint[] corners, Rect imgRect, double scale, Color color)
    {
        var canvasPoints = corners.Select(c => new Point(imgRect.X + c.X * scale, imgRect.Y + c.Y * scale)).ToList();

        var polygon = new Polygon
        {
            Points = canvasPoints,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B))
        };
        canvas.Children.Add(polygon);

        foreach (var p in canvasPoints)
            AddHandle(canvas, p.X, p.Y, color);
    }

    private void RenderSplitLine(Canvas canvas, CropReviewViewModel vm, Rect imgRect, double scale)
    {
        var lineX = imgRect.X + vm.ImageWidth * (vm.SplitPercent / 100.0) * scale;

        // Shade both halves so it's visually unambiguous which side is which.
        var leftShade = new Rectangle
        {
            Width = Math.Max(0, lineX - imgRect.X),
            Height = imgRect.Height,
            Fill = new SolidColorBrush(Color.FromArgb(35, 0, 210, 255))
        };
        Canvas.SetLeft(leftShade, imgRect.X);
        Canvas.SetTop(leftShade, imgRect.Y);
        canvas.Children.Add(leftShade);

        var rightShade = new Rectangle
        {
            Width = Math.Max(0, imgRect.Right - lineX),
            Height = imgRect.Height,
            Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))
        };
        Canvas.SetLeft(rightShade, lineX);
        Canvas.SetTop(rightShade, imgRect.Y);
        canvas.Children.Add(rightShade);

        var line = new Rectangle
        {
            Width = 3,
            Height = imgRect.Height,
            Fill = new SolidColorBrush(Color.Parse("#e94560"))
        };
        Canvas.SetLeft(line, lineX - 1.5);
        Canvas.SetTop(line, imgRect.Y);
        canvas.Children.Add(line);

        AddHandle(canvas, lineX, imgRect.Y + imgRect.Height / 2, Color.Parse("#e94560"));
    }

    private void AddHandle(Canvas canvas, double cx, double cy, Color strokeColor)
    {
        const double radius = 7;
        var handle = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(Color.Parse("#ffffff")),
            Stroke = new SolidColorBrush(strokeColor),
            StrokeThickness = 2
        };
        Canvas.SetLeft(handle, cx - radius);
        Canvas.SetTop(handle, cy - radius);
        canvas.Children.Add(handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as CropReviewViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
