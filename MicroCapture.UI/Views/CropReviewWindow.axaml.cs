using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class CropReviewWindow : Window
{
    private enum DragTarget { None, Body, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right }
    private DragTarget _activeDrag = DragTarget.None;
    private Point _dragStartPoint;
    private Rect _initialCropRect;

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
                if (ev.PropertyName is nameof(vm.CropX) or nameof(vm.CropY) or nameof(vm.CropWidth) or nameof(vm.CropHeight) or nameof(vm.Image))
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

        // Convert current crop coords to canvas space
        var cropCanvasX = imgRect.X + (vm.CropX * scale);
        var cropCanvasY = imgRect.Y + (vm.CropY * scale);
        var cropCanvasW = vm.CropWidth * scale;
        var cropCanvasH = vm.CropHeight * scale;
        var cropCanvasRect = new Rect(cropCanvasX, cropCanvasY, cropCanvasW, cropCanvasH);

        const double handleSize = 16.0;
        _activeDrag = HitTest(pt, cropCanvasRect, handleSize);

        if (_activeDrag != DragTarget.None)
        {
            _dragStartPoint = pt;
            _initialCropRect = new Rect(vm.CropX, vm.CropY, vm.CropWidth, vm.CropHeight);
            e.Pointer.Capture(canvas);
        }
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

        var deltaX = (pt.X - _dragStartPoint.X) / scale;
        var deltaY = (pt.Y - _dragStartPoint.Y) / scale;

        var imgW = (int)vm.Image.Size.Width;
        var imgH = (int)vm.Image.Size.Height;

        double x = _initialCropRect.X;
        double y = _initialCropRect.Y;
        double w = _initialCropRect.Width;
        double h = _initialCropRect.Height;

        switch (_activeDrag)
        {
            case DragTarget.Body:
                x = Math.Clamp(_initialCropRect.X + deltaX, 0, imgW - w);
                y = Math.Clamp(_initialCropRect.Y + deltaY, 0, imgH - h);
                break;
            case DragTarget.TopLeft:
                x = Math.Clamp(_initialCropRect.X + deltaX, 0, _initialCropRect.Right - 20);
                y = Math.Clamp(_initialCropRect.Y + deltaY, 0, _initialCropRect.Bottom - 20);
                w = _initialCropRect.Right - x;
                h = _initialCropRect.Bottom - y;
                break;
            case DragTarget.TopRight:
                w = Math.Clamp(_initialCropRect.Width + deltaX, 20, imgW - x);
                y = Math.Clamp(_initialCropRect.Y + deltaY, 0, _initialCropRect.Bottom - 20);
                h = _initialCropRect.Bottom - y;
                break;
            case DragTarget.BottomLeft:
                x = Math.Clamp(_initialCropRect.X + deltaX, 0, _initialCropRect.Right - 20);
                w = _initialCropRect.Right - x;
                h = Math.Clamp(_initialCropRect.Height + deltaY, 20, imgH - y);
                break;
            case DragTarget.BottomRight:
                w = Math.Clamp(_initialCropRect.Width + deltaX, 20, imgW - x);
                h = Math.Clamp(_initialCropRect.Height + deltaY, 20, imgH - y);
                break;
        }

        vm.CropX = (int)x;
        vm.CropY = (int)y;
        vm.CropWidth = (int)w;
        vm.CropHeight = (int)h;

        RenderOverlay();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activeDrag != DragTarget.None)
        {
            _activeDrag = DragTarget.None;
            var canvas = this.FindControl<Canvas>("OverlayCanvas");
            e.Pointer.Capture(null);
        }
    }

    private DragTarget HitTest(Point pt, Rect rect, double handleMargin)
    {
        var tl = new Rect(rect.X - handleMargin, rect.Y - handleMargin, handleMargin * 2, handleMargin * 2);
        var tr = new Rect(rect.Right - handleMargin, rect.Y - handleMargin, handleMargin * 2, handleMargin * 2);
        var bl = new Rect(rect.X - handleMargin, rect.Bottom - handleMargin, handleMargin * 2, handleMargin * 2);
        var br = new Rect(rect.Right - handleMargin, rect.Bottom - handleMargin, handleMargin * 2, handleMargin * 2);

        if (tl.Contains(pt)) return DragTarget.TopLeft;
        if (tr.Contains(pt)) return DragTarget.TopRight;
        if (bl.Contains(pt)) return DragTarget.BottomLeft;
        if (br.Contains(pt)) return DragTarget.BottomRight;

        if (rect.Contains(pt)) return DragTarget.Body;

        return DragTarget.None;
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

        var cropCanvasX = imgRect.X + (vm.CropX * scale);
        var cropCanvasY = imgRect.Y + (vm.CropY * scale);
        var cropCanvasW = vm.CropWidth * scale;
        var cropCanvasH = vm.CropHeight * scale;

        // Crop Box Border
        var box = new Rectangle
        {
            Width = Math.Max(1, cropCanvasW),
            Height = Math.Max(1, cropCanvasH),
            Stroke = new SolidColorBrush(Color.Parse("#00d2ff")),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0, 210, 255))
        };
        Canvas.SetLeft(box, cropCanvasX);
        Canvas.SetTop(box, cropCanvasY);
        canvas.Children.Add(box);

        // Corner Handles
        AddHandle(canvas, cropCanvasX, cropCanvasY);
        AddHandle(canvas, cropCanvasX + cropCanvasW, cropCanvasY);
        AddHandle(canvas, cropCanvasX, cropCanvasY + cropCanvasH);
        AddHandle(canvas, cropCanvasX + cropCanvasW, cropCanvasY + cropCanvasH);
    }

    private void AddHandle(Canvas canvas, double cx, double cy)
    {
        const double radius = 6;
        var handle = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(Color.Parse("#ffffff")),
            Stroke = new SolidColorBrush(Color.Parse("#00d2ff")),
            StrokeThickness = 2
        };
        Canvas.SetLeft(handle, cx - radius);
        Canvas.SetTop(handle, cy - radius);
        canvas.Children.Add(handle);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
