using System;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MicroCapture.Processing;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class CropReviewWindow : UserControl
{
    private enum DragTarget
    {
        None, Corner0, Corner1, Corner2, Corner3, SplitLine,
        LeftCorner0, LeftCorner1, LeftCorner2, LeftCorner3,
        RightCorner0, RightCorner1, RightCorner2, RightCorner3,
        DewarpTop0, DewarpTop1, DewarpTop2, DewarpTop3, DewarpTop4,
        DewarpBottom0, DewarpBottom1, DewarpBottom2, DewarpBottom3, DewarpBottom4
    }

    private const double HandleHitRadius = 16.0;
    private const double SplitLineHitMargin = 12.0;
    // Matches the accent tokens in Styles/Theme.axaml (PrimaryBrush / PrimaryHoverBrush) —
    // kept as literal colors here since this canvas is drawn entirely in code-behind, not XAML.
    private static readonly Color SingleQuadColor = Color.Parse("#5e6ad2");
    private static readonly Color LeftQuadColor = Color.Parse("#5e6ad2");
    private static readonly Color RightQuadColor = Color.Parse("#828fff");
    private static readonly Color TopCurveColor = Color.Parse("#5e6ad2");
    private static readonly Color BottomCurveColor = Color.Parse("#d99a3d");
    // Method 4's raw detected trace (drawn underneath the editable quad, see RenderOverlay) —
    // distinct, higher-saturation colors so it reads as "what was actually detected" versus the
    // editable shape's own softer accent colors.
    private static readonly Color Method4TopColor = Color.Parse("#22c55e");
    private static readonly Color Method4BottomColor = Color.Parse("#22c55e");
    private static readonly Color Method4LeftColor = Color.Parse("#06b6d4");
    private static readonly Color Method4RightColor = Color.Parse("#06b6d4");
    private static readonly Color Method4GutterColor = Color.Parse("#f472b6");

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
                    or nameof(vm.LeftQuad) or nameof(vm.RightQuad) or nameof(vm.IsTwoQuadSplit)
                    or nameof(vm.IsDewarpMode) or nameof(vm.DewarpTopPoints) or nameof(vm.DewarpBottomPoints)
                    or nameof(vm.DewarpBackdropImage) or nameof(vm.IsAdjustMode)
                    or nameof(vm.Method4TopCurve) or nameof(vm.Method4BottomCurve)
                    or nameof(vm.Method4LeftCurve) or nameof(vm.Method4RightCurve) or nameof(vm.Method4GutterLine))
                {
                    RenderOverlay();
                }
            };
            vm.ConfirmBulkApplyRequested += async (s, request) =>
            {
                // No longer a Window itself (embedded in MainWindow, see MainWindow.axaml's
                // ActiveCropReview host) — ConfirmDialog needs an actual owner Window for
                // centering/modality, so resolve the containing top-level instead of using
                // `this`.
                var confirmed = TopLevel.GetTopLevel(this) is Window owner
                    && await ConfirmDialog.AskAsync(owner, request.Message, "Apply Adjustments");
                request.OnAnswered(confirmed);
            };
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not CropReviewViewModel vm || vm.Image == null || vm.IsAdjustMode) return;

        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null) return;

        var pt = e.GetPosition(canvas);
        var (imgRect, scale) = GetDisplayedImageRect();
        if (imgRect.Width <= 0 || imgRect.Height <= 0) return;

        if (vm.IsDewarpMode)
        {
            if (TryHitQuad(vm.DewarpTopPoints, imgRect, scale, pt, out var topIdx))
            {
                _activeDrag = DragTarget.DewarpTop0 + topIdx;
                e.Pointer.Capture(canvas);
            }
            else if (TryHitQuad(vm.DewarpBottomPoints, imgRect, scale, pt, out var bottomIdx))
            {
                _activeDrag = DragTarget.DewarpBottom0 + bottomIdx;
                e.Pointer.Capture(canvas);
            }
            return;
        }

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
        if (_activeDrag == DragTarget.None || DataContext is not CropReviewViewModel vm) return;

        var canvas = this.FindControl<Canvas>("OverlayCanvas");
        if (canvas == null) return;

        var pt = e.GetPosition(canvas);
        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        if (_activeDrag >= DragTarget.DewarpTop0 && _activeDrag <= DragTarget.DewarpBottom4)
        {
            var dewarpX = Math.Clamp((pt.X - imgRect.X) / scale, 0, vm.DewarpSpaceWidth);
            var dewarpY = Math.Clamp((pt.Y - imgRect.Y) / scale, 0, vm.DewarpSpaceHeight);
            var dewarpRawPoint = new CropPoint(dewarpX, dewarpY);

            var isTop = _activeDrag <= DragTarget.DewarpTop4;
            var baseTarget = isTop ? DragTarget.DewarpTop0 : DragTarget.DewarpBottom0;
            var idx = _activeDrag - baseTarget;
            var resolved = vm.ResolveDewarpPointDrag(isTop, idx, dewarpRawPoint);
            var arr = (CropPoint[])(isTop ? vm.DewarpTopPoints : vm.DewarpBottomPoints).Clone();
            arr[idx] = resolved;
            if (isTop) vm.DewarpTopPoints = arr; else vm.DewarpBottomPoints = arr;
            return;
        }

        if (vm.Image == null) return;

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
        if (container == null || DataContext is not CropReviewViewModel vm) return (default, 0);

        // Dewarp mode displays/edits against DewarpBackdropImage (the undewarped crop, in the
        // crop-preview renderer's own downscaled pixel space) instead of the raw source image —
        // a different bitmap with different dimensions, so the displayed rect must be computed
        // against whichever one is actually on screen.
        Bitmap? active = vm.IsDewarpMode ? vm.DewarpBackdropImage : vm.Image;
        if (active == null) return (default, 0);

        var containerW = container.Bounds.Width;
        var containerH = container.Bounds.Height;
        var imgW = active.Size.Width;
        var imgH = active.Size.Height;

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
        if (canvas == null || DataContext is not CropReviewViewModel vm) return;

        canvas.Children.Clear();

        if (vm.IsAdjustMode) return; // no draggable geometry in Adjust mode — sliders only

        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        if (vm.IsDewarpMode)
        {
            RenderDewarpCurve(canvas, vm.DewarpTopPoints, imgRect, scale, TopCurveColor);
            RenderDewarpCurve(canvas, vm.DewarpBottomPoints, imgRect, scale, BottomCurveColor);
            return;
        }

        // Method 4's own raw trace, drawn first (underneath the editable quad below) — this is
        // what the detector actually found, distinct from the 4-corner quad the operator drags,
        // which is only a starting approximation of it. Null for a job with a saved manual crop
        // (no detection ran) or when detection failed.
        RenderMethod4Trace(canvas, vm.Method4TopCurve, imgRect, scale, Method4TopColor);
        RenderMethod4Trace(canvas, vm.Method4BottomCurve, imgRect, scale, Method4BottomColor);
        RenderMethod4Trace(canvas, vm.Method4LeftCurve, imgRect, scale, Method4LeftColor);
        RenderMethod4Trace(canvas, vm.Method4RightCurve, imgRect, scale, Method4RightColor);
        RenderMethod4Trace(canvas, vm.Method4GutterLine, imgRect, scale, Method4GutterColor);

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

    /// <summary>Draws one of Method 4's raw detected traces (top/bottom edge, left/right side,
    /// or gutter line) as a plain polyline through its already-dense points — no spline
    /// interpolation needed (unlike <see cref="RenderDewarpCurve"/>'s sparse control points,
    /// these already have one point per column/row) and no drag handles, since this is a
    /// read-only "here's what was detected" reference, not something the operator edits
    /// directly.</summary>
    private static void RenderMethod4Trace(Canvas canvas, CropPoint[]? points, Rect imgRect, double scale, Color color)
    {
        if (points == null || points.Length < 2) return;

        var canvasPoints = points.Select(p => new Point(imgRect.X + p.X * scale, imgRect.Y + p.Y * scale)).ToList();
        canvas.Children.Add(new Polyline
        {
            Points = canvasPoints,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeDashArray = new AvaloniaList<double> { 4, 3 }
        });
    }

    /// <summary>Draws one curve (top or bottom edge) as a smooth spline through its control
    /// points — same interpolation ImageProcessor.ApplyDewarp uses to densify them, so what the
    /// operator sees traces the same curve that will actually be applied — plus a draggable
    /// handle at each control point.</summary>
    private void RenderDewarpCurve(Canvas canvas, CropPoint[] points, Rect imgRect, double scale, Color color)
    {
        if (points.Length < 2) return;

        var xs = points.Select(p => p.X).ToArray();
        var ys = points.Select(p => p.Y).ToArray();
        var minX = xs.Min();
        var maxX = xs.Max();

        const int samples = 48;
        var curvePoints = new System.Collections.Generic.List<Point>(samples + 1);
        for (var i = 0; i <= samples; i++)
        {
            var x = minX + (maxX - minX) * i / (double)samples;
            var y = ImageProcessor.InterpolateSpline(xs, ys, x);
            curvePoints.Add(new Point(imgRect.X + x * scale, imgRect.Y + y * scale));
        }

        canvas.Children.Add(new Polyline
        {
            Points = curvePoints,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.5
        });

        foreach (var p in points)
            AddHandle(canvas, imgRect.X + p.X * scale, imgRect.Y + p.Y * scale, color);
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

        // Outline + shade both halves so each reads as an actual crop box, not just a tint.
        var leftShade = new Rectangle
        {
            Width = Math.Max(0, lineX - imgRect.X),
            Height = imgRect.Height,
            Fill = new SolidColorBrush(Color.FromArgb(35, 94, 106, 210)),
            Stroke = new SolidColorBrush(LeftQuadColor),
            StrokeThickness = 2
        };
        Canvas.SetLeft(leftShade, imgRect.X);
        Canvas.SetTop(leftShade, imgRect.Y);
        canvas.Children.Add(leftShade);

        var rightShade = new Rectangle
        {
            Width = Math.Max(0, imgRect.Right - lineX),
            Height = imgRect.Height,
            Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            Stroke = new SolidColorBrush(RightQuadColor),
            StrokeThickness = 2
        };
        Canvas.SetLeft(rightShade, lineX);
        Canvas.SetTop(rightShade, imgRect.Y);
        canvas.Children.Add(rightShade);

        var line = new Rectangle
        {
            Width = 3,
            Height = imgRect.Height,
            Fill = new SolidColorBrush(Color.Parse("#d99a3d"))
        };
        Canvas.SetLeft(line, lineX - 1.5);
        Canvas.SetTop(line, imgRect.Y);
        canvas.Children.Add(line);

        AddHandle(canvas, lineX, imgRect.Y + imgRect.Height / 2, Color.Parse("#d99a3d"));
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

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        // MainWindowViewModel disposes the view model itself when it clears ActiveCropReview
        // (see OpenCropReview/CloseCropReview) — this is a defensive backstop in case this
        // control is ever removed from the tree some other way.
        (DataContext as CropReviewViewModel)?.Dispose();
        base.OnUnloaded(e);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
