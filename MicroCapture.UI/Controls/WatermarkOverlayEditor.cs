using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MicroCapture.UI.Controls;

/// <summary>Interactive drag/resize/rotate box for positioning a single watermark on top of a
/// page preview — the operator drags the interior to move it, its 8 handles to resize, and its
/// rotation handle to rotate, like positioning a sticker on a photo. Single-watermark analog of
/// <see cref="FixedFrameOverlayEditor"/>: same working-copy-during-drag, commit-on-release
/// discipline, but with no rubber-band creation (a watermark box always exists once the editor
/// is open) and one rotation handle in addition to the 8 resize handles.
///
/// <para><see cref="Transform"/> lives in normalized 0..1 fraction-of-page space, matching
/// <see cref="MicroCapture.Core.Models.WatermarkPreset"/>'s own storage — unlike
/// FixedFrameOverlayEditor's pixel-space Frames, this control never needs a
/// "SourceImageSize"-equivalent property since normalized space IS the page regardless of its
/// pixel size.</para></summary>
public class WatermarkOverlayEditor : Control
{
    private const double HandleHitRadius = 10.0;
    private const double HandleSize = 9.0;
    private const double RotationHandleRadius = 6.0;
    private const double RotationHandleOffsetPx = 24.0;

    public static readonly StyledProperty<WatermarkTransform> TransformProperty =
        AvaloniaProperty.Register<WatermarkOverlayEditor, WatermarkTransform>(nameof(Transform),
            defaultValue: new WatermarkTransform(0.7, 0.85, 0.2, 0.1, 0),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<Size> PageImageSizeProperty =
        AvaloniaProperty.Register<WatermarkOverlayEditor, Size>(nameof(PageImageSize));

    public static readonly StyledProperty<string> WatermarkTypeProperty =
        AvaloniaProperty.Register<WatermarkOverlayEditor, string>(nameof(WatermarkType), defaultValue: "Text");

    public static readonly StyledProperty<string?> TextContentProperty =
        AvaloniaProperty.Register<WatermarkOverlayEditor, string?>(nameof(TextContent));

    public static readonly StyledProperty<Bitmap?> LogoBitmapProperty =
        AvaloniaProperty.Register<WatermarkOverlayEditor, Bitmap?>(nameof(LogoBitmap));

    public static readonly StyledProperty<double> WatermarkOpacityProperty =
        AvaloniaProperty.Register<WatermarkOverlayEditor, double>(nameof(WatermarkOpacity), defaultValue: 0.5);

    public static readonly StyledProperty<bool> IsEditingEnabledProperty =
        AvaloniaProperty.Register<WatermarkOverlayEditor, bool>(nameof(IsEditingEnabled), defaultValue: true);

    public WatermarkTransform Transform
    {
        get => GetValue(TransformProperty);
        set => SetValue(TransformProperty, value);
    }

    /// <summary>Pixel size of the page preview currently displayed underneath, used only to
    /// letterbox-align this control's own drawing/hit-testing with that image (the same role
    /// SourceImageSize plays for FixedFrameOverlayEditor).</summary>
    public Size PageImageSize
    {
        get => GetValue(PageImageSizeProperty);
        set => SetValue(PageImageSizeProperty, value);
    }

    public string WatermarkType
    {
        get => GetValue(WatermarkTypeProperty);
        set => SetValue(WatermarkTypeProperty, value);
    }

    public string? TextContent
    {
        get => GetValue(TextContentProperty);
        set => SetValue(TextContentProperty, value);
    }

    public Bitmap? LogoBitmap
    {
        get => GetValue(LogoBitmapProperty);
        set => SetValue(LogoBitmapProperty, value);
    }

    public double WatermarkOpacity
    {
        get => GetValue(WatermarkOpacityProperty);
        set => SetValue(WatermarkOpacityProperty, value);
    }

    public bool IsEditingEnabled
    {
        get => GetValue(IsEditingEnabledProperty);
        set => SetValue(IsEditingEnabledProperty, value);
    }

    /// <summary>Raised on pointer-up after a real change — always a transform edit, since a
    /// single watermark box has no structural add/remove concept.</summary>
    public event EventHandler? EditCommitted;

    /// <summary>Raised on pointer-down/up so the host can pause its own preview re-render mid-drag.</summary>
    public event EventHandler<bool>? InteractionChanged;

    private WatermarkHandleKind? _activeHandle;
    private WatermarkTransform _workingTransform;
    private Point _lastPointerNormalized;

    static WatermarkOverlayEditor()
    {
        AffectsRender<WatermarkOverlayEditor>(TransformProperty, WatermarkTypeProperty, TextContentProperty,
            LogoBitmapProperty, WatermarkOpacityProperty, IsEditingEnabledProperty, PageImageSizeProperty);
        AffectsRender<WatermarkOverlayEditor>(BoundsProperty);
    }

    public WatermarkOverlayEditor()
    {
        Focusable = false;
    }

    /// <summary>The page preview's displayed rect within this control, accounting for the
    /// letterboxing <c>Stretch="Uniform"</c> introduces, plus the image-to-control scale. Returns
    /// a zero scale when there is nothing to draw against.</summary>
    private (Rect DisplayedRect, double Scale) GetDisplayedPageRect()
    {
        var imgW = PageImageSize.Width;
        var imgH = PageImageSize.Height;
        var containerW = Bounds.Width;
        var containerH = Bounds.Height;
        if (imgW <= 0 || imgH <= 0 || containerW <= 0 || containerH <= 0) return (default, 0);

        var scale = Math.Min(containerW / imgW, containerH / imgH);
        var dispW = imgW * scale;
        var dispH = imgH * scale;
        return (new Rect((containerW - dispW) / 2.0, (containerH - dispH) / 2.0, dispW, dispH), scale);
    }

    private Point ToNormalized(Point controlPoint, Rect imgRect)
    {
        if (imgRect.Width <= 0 || imgRect.Height <= 0) return default;
        return new Point((controlPoint.X - imgRect.X) / imgRect.Width, (controlPoint.Y - imgRect.Y) / imgRect.Height);
    }

    private Point ToControlSpace(Point normalized, Rect imgRect) =>
        new(imgRect.X + normalized.X * imgRect.Width, imgRect.Y + normalized.Y * imgRect.Height);

    private WatermarkTransform Effective => _activeHandle != null ? _workingTransform : Transform;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEditingEnabled) return;

        var (imgRect, scale) = GetDisplayedPageRect();
        if (scale <= 0) return;

        var pt = e.GetPosition(this);
        var t = Transform;

        // Rotation handle first — topmost, smallest target, sits near the Top resize handle and
        // must win over it.
        var rotationOffsetNormalized = RotationHandleOffsetPx / imgRect.Height;
        var rotationPt = ToControlSpace(WatermarkGeometry.RotationHandlePoint(t, rotationOffsetNormalized), imgRect);
        if (Distance(rotationPt, pt) <= RotationHandleRadius + 3)
        {
            BeginDrag(WatermarkHandleKind.Rotate, t, e);
            return;
        }

        foreach (var handle in WatermarkGeometry.AllResizeHandles)
        {
            var hp = ToControlSpace(WatermarkGeometry.HandlePoint(t, handle), imgRect);
            if (Distance(hp, pt) <= HandleHitRadius)
            {
                BeginDrag(handle, t, e);
                return;
            }
        }

        // Interior hit-test (rotation-aware): rotate the pointer into the box's local frame,
        // then a plain axis-aligned contains check.
        var normalizedPt = ToNormalized(pt, imgRect);
        var center = WatermarkGeometry.Center(t);
        var local = RotatePointAround(normalizedPt, center, -t.RotationDegrees);
        if (local.X >= t.X && local.X <= t.X + t.Width && local.Y >= t.Y && local.Y <= t.Y + t.Height)
        {
            BeginDrag(WatermarkHandleKind.Move, t, e);
        }
    }

    private static Point RotatePointAround(Point p, Point pivot, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = p.X - pivot.X;
        var dy = p.Y - pivot.Y;
        return new Point(pivot.X + dx * cos - dy * sin, pivot.Y + dx * sin + dy * cos);
    }

    private void BeginDrag(WatermarkHandleKind handle, WatermarkTransform current, PointerPressedEventArgs e)
    {
        _activeHandle = handle;
        _workingTransform = current;
        var (imgRect, _) = GetDisplayedPageRect();
        _lastPointerNormalized = ToNormalized(e.GetPosition(this), imgRect);
        e.Pointer.Capture(this);
        InteractionChanged?.Invoke(this, true);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEditingEnabled || _activeHandle is not { } handle) return;

        var (imgRect, scale) = GetDisplayedPageRect();
        if (scale <= 0) return;

        var normalizedPt = ToNormalized(e.GetPosition(this), imgRect);

        if (handle == WatermarkHandleKind.Move)
        {
            _workingTransform = WatermarkGeometry.Move(_workingTransform,
                normalizedPt.X - _lastPointerNormalized.X, normalizedPt.Y - _lastPointerNormalized.Y);
            _lastPointerNormalized = normalizedPt;
        }
        else if (handle == WatermarkHandleKind.Rotate)
        {
            var snap = (e.KeyModifiers & KeyModifiers.Shift) == 0;
            _workingTransform = _workingTransform with
            {
                RotationDegrees = WatermarkGeometry.ResolveRotate(_workingTransform, normalizedPt, snap)
            };
        }
        else
        {
            _workingTransform = WatermarkGeometry.ResolveResize(_workingTransform, handle, normalizedPt);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activeHandle == null) return;

        var changed = !Transform.Equals(_workingTransform);
        if (changed) Transform = _workingTransform;

        _activeHandle = null;
        e.Pointer.Capture(null);
        InteractionChanged?.Invoke(this, false);

        if (changed) EditCommitted?.Invoke(this, EventArgs.Empty);

        InvalidateVisual();
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // A Control with nothing painted is not hit-testable in Avalonia — fill the bounds with
        // a fully transparent brush so pointer events reach this control (same reasoning as
        // FixedFrameOverlayEditor.Render).
        if (IsEditingEnabled)
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var (imgRect, scale) = GetDisplayedPageRect();
        if (scale <= 0) return;

        var t = Effective;
        var center = ToControlSpace(WatermarkGeometry.Center(t), imgRect);
        var boxW = t.Width * imgRect.Width;
        var boxH = t.Height * imgRect.Height;

        using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y)
                   * Matrix.CreateRotation(t.RotationDegrees * Math.PI / 180.0)
                   * Matrix.CreateTranslation(center.X, center.Y)))
        {
            var displayRect = new Rect(center.X - boxW / 2, center.Y - boxH / 2, Math.Max(0, boxW), Math.Max(0, boxH));

            // Faint selection box only — the actual watermark pixels the operator judges
            // opacity/color/rotation against come from the composited preview image underneath
            // this control, not from this outline, so it should not visually double up with it.
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(30, 64, 156, 255)),
                new Pen(new SolidColorBrush(Colors.DodgerBlue), 2, new DashStyle(new double[] { 6, 3 }, 0)),
                displayRect);
        }

        if (!IsEditingEnabled) return;

        foreach (var handle in WatermarkGeometry.AllResizeHandles)
        {
            var hp = ToControlSpace(WatermarkGeometry.HandlePoint(t, handle), imgRect);
            DrawHandle(context, hp.X, hp.Y);
        }

        var rotationOffsetNormalized = RotationHandleOffsetPx / imgRect.Height;
        var rotationPt = ToControlSpace(WatermarkGeometry.RotationHandlePoint(t, rotationOffsetNormalized), imgRect);
        var topHandlePt = ToControlSpace(WatermarkGeometry.HandlePoint(t, WatermarkHandleKind.Top), imgRect);
        context.DrawLine(new Pen(Brushes.DodgerBlue, 1.5), topHandlePt, rotationPt);
        context.DrawEllipse(Brushes.White, new Pen(Brushes.DodgerBlue, 2), rotationPt, RotationHandleRadius, RotationHandleRadius);
    }

    private static void DrawHandle(DrawingContext context, double cx, double cy)
    {
        var rect = new Rect(cx - HandleSize / 2, cy - HandleSize / 2, HandleSize, HandleSize);
        context.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, 2), rect);
    }
}
