using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MicroCapture.Processing;

namespace MicroCapture.UI.Controls;

/// <summary>What kind of edit just completed, so the host can decide whether to persist
/// immediately or on a debounce.</summary>
public enum FrameEditKind
{
    /// <summary>A drag/resize finished — debounce, since another drag often follows.</summary>
    Transform,
    /// <summary>A frame was created or deleted — a discrete act, persist right away.</summary>
    Structural
}

/// <summary>Interactive fixed-frame overlay drawn directly on top of the live camera view: the
/// operator drags on empty space to draw a new frame, drags a frame's interior to move it, its
/// handles to resize, and the × badge (or the Delete key, handled by the host) to remove it.
/// This replaces the former modal "Calibrate Frames" panel, which required a throwaway
/// full-resolution shot and hid the live view behind a still image.
///
/// <para><b>Coordinate spaces.</b> Three are in play and must not be confused:
/// <list type="bullet">
/// <item>control space — raw pointer coordinates, including the letterbox bars around the feed;</item>
/// <item>image space — pixels of <see cref="SourceImageSize"/>, which is what <see cref="Frames"/>
/// is stored in and what all of <see cref="FrameGeometry"/> operates on;</item>
/// <item>capture space — the full-resolution shot, only used for the size readout, since the
/// operator cares about the real output size not the preview size.</item>
/// </list>
/// The feed is rendered <c>Stretch="Uniform"</c>, so image space maps to a centered, letterboxed
/// rect within control space — see <see cref="GetDisplayedImageRect"/>.</para>
///
/// <para><b>Drag isolation.</b> A drag mutates a private working copy and only writes back to
/// <see cref="Frames"/> on pointer-up. That keeps <c>CollectionChanged</c> from firing at pointer
/// rate, and makes "never persist mid-drag" a structural property rather than a matter of
/// discipline at each call site.</para></summary>
public class FixedFrameOverlayEditor : Control
{
    private const double HandleHitRadius = 10.0;
    private const double HandleSize = 9.0;
    private const double BadgeRadius = 8.0;

    public static readonly StyledProperty<ObservableCollection<FixedFrameRect>?> FramesProperty =
        AvaloniaProperty.Register<FixedFrameOverlayEditor, ObservableCollection<FixedFrameRect>?>(nameof(Frames));

    public static readonly StyledProperty<Size> SourceImageSizeProperty =
        AvaloniaProperty.Register<FixedFrameOverlayEditor, Size>(nameof(SourceImageSize));

    public static readonly StyledProperty<Size> CaptureImageSizeProperty =
        AvaloniaProperty.Register<FixedFrameOverlayEditor, Size>(nameof(CaptureImageSize));

    public static readonly StyledProperty<int> SelectedFrameIndexProperty =
        AvaloniaProperty.Register<FixedFrameOverlayEditor, int>(nameof(SelectedFrameIndex),
            defaultValue: -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsEditingEnabledProperty =
        AvaloniaProperty.Register<FixedFrameOverlayEditor, bool>(nameof(IsEditingEnabled), defaultValue: true);

    public static readonly StyledProperty<double> ViewRotationRadiansProperty =
        AvaloniaProperty.Register<FixedFrameOverlayEditor, double>(nameof(ViewRotationRadians));

    /// <summary>The authoritative frame list, in the pixel space of <see cref="SourceImageSize"/>.
    /// Index order is page order — it drives output filenames — so this control never reorders it.</summary>
    public ObservableCollection<FixedFrameRect>? Frames
    {
        get => GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    /// <summary>Pixel size of the image <see cref="Frames"/> is expressed in. For frames authored
    /// here this is the live feed's own size; for a batch calibrated the old way it is the
    /// calibration still's size, and frames are re-projected onto the feed for display.</summary>
    public Size SourceImageSize
    {
        get => GetValue(SourceImageSizeProperty);
        set => SetValue(SourceImageSizeProperty, value);
    }

    /// <summary>Full-resolution capture size, used only for the drag size readout so the operator
    /// sees the real output dimensions rather than preview pixels. Zero hides the readout.</summary>
    public Size CaptureImageSize
    {
        get => GetValue(CaptureImageSizeProperty);
        set => SetValue(CaptureImageSizeProperty, value);
    }

    public int SelectedFrameIndex
    {
        get => GetValue(SelectedFrameIndexProperty);
        set => SetValue(SelectedFrameIndexProperty, value);
    }

    /// <summary>False while captures are still processing under the current geometry — the
    /// overlay stays visible but becomes read-only, rather than accepting a drag and rejecting
    /// it after the fact.</summary>
    public bool IsEditingEnabled
    {
        get => GetValue(IsEditingEnabledProperty);
        set => SetValue(IsEditingEnabledProperty, value);
    }

    /// <summary>How far the live view (and this overlay with it) is rotated, in radians clockwise.
    /// The frames themselves ride that rotation so they stay glued to the page; the number badge
    /// and the size readout are counter-rotated by this much so their text stays upright.</summary>
    public double ViewRotationRadians
    {
        get => GetValue(ViewRotationRadiansProperty);
        set => SetValue(ViewRotationRadiansProperty, value);
    }

    /// <summary>Raised when an edit is complete and worth persisting.</summary>
    public event EventHandler<FrameEditKind>? EditCommitted;

    /// <summary>Raised on pointer-down and pointer-up so the host can suspend auto-capture while
    /// the geometry is still moving under the operator's hand.</summary>
    public event EventHandler<bool>? InteractionChanged;

    // Active drag: which frame, and which handle (Move for a whole-frame drag). Move drags track
    // the last pointer position in image space to compute per-move deltas, since translating is
    // naturally delta-based while resizing an edge is absolute.
    private (int FrameIndex, FrameHandleKind Handle)? _activeDrag;
    private Point _lastImagePointer;

    // The in-flight rectangle for the frame being dragged, kept out of Frames until pointer-up.
    private FixedFrameRect? _workingRect;

    // Rubber-band creation state.
    private Point? _rubberBandOrigin;
    private FixedFrameRect? _rubberBandRect;

    private INotifyCollectionChanged? _observedFrames;

    static FixedFrameOverlayEditor()
    {
        AffectsRender<FixedFrameOverlayEditor>(FramesProperty, SourceImageSizeProperty, SelectedFrameIndexProperty, IsEditingEnabledProperty, ViewRotationRadiansProperty);
        // The overlay's geometry is derived from its own bounds (the live feed is Uniform-scaled
        // inside them), so a window resize has to repaint it as well.
        AffectsRender<FixedFrameOverlayEditor>(BoundsProperty);
    }

    public FixedFrameOverlayEditor()
    {
        // The feed is repainted continuously; the overlay must track it and any collection edits.
        Focusable = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FramesProperty)
        {
            if (_observedFrames != null) _observedFrames.CollectionChanged -= OnFramesCollectionChanged;
            _observedFrames = change.GetNewValue<ObservableCollection<FixedFrameRect>?>();
            if (_observedFrames != null) _observedFrames.CollectionChanged += OnFramesCollectionChanged;
            InvalidateVisual();
        }
    }

    private void OnFramesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    /// <summary>The live feed's displayed rect within this control, accounting for the
    /// letterboxing that <c>Stretch="Uniform"</c> introduces, plus the image-to-control scale.
    /// Returns a zero scale when there is nothing to draw against.</summary>
    private (Rect DisplayedRect, double Scale) GetDisplayedImageRect()
    {
        var imgW = SourceImageSize.Width;
        var imgH = SourceImageSize.Height;
        var containerW = Bounds.Width;
        var containerH = Bounds.Height;
        if (imgW <= 0 || imgH <= 0 || containerW <= 0 || containerH <= 0) return (default, 0);

        var scale = Math.Min(containerW / imgW, containerH / imgH);
        var dispW = imgW * scale;
        var dispH = imgH * scale;
        return (new Rect((containerW - dispW) / 2.0, (containerH - dispH) / 2.0, dispW, dispH), scale);
    }

    /// <summary>Converts a control-space pointer position into image space, clamped to the image
    /// so a drag that wanders into the letterbox bars doesn't produce out-of-bounds geometry.</summary>
    private Point ToImageSpace(Point controlPoint, Rect imgRect, double scale)
    {
        var x = (controlPoint.X - imgRect.X) / scale;
        var y = (controlPoint.Y - imgRect.Y) / scale;
        return new Point(
            Math.Clamp(x, 0, SourceImageSize.Width),
            Math.Clamp(y, 0, SourceImageSize.Height));
    }

    /// <summary>The rectangle currently shown for a frame — the working copy while it is being
    /// dragged, otherwise the committed one.</summary>
    private FixedFrameRect EffectiveRect(int index)
    {
        if (_activeDrag is { } drag && drag.FrameIndex == index && _workingRect is { } working) return working;
        return Frames![index];
    }

    private Point BadgeCenter(FixedFrameRect rect, Rect imgRect, double scale) =>
        new(imgRect.X + (rect.X + rect.Width) * scale - BadgeRadius,
            imgRect.Y + rect.Y * scale + BadgeRadius);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEditingEnabled || Frames == null) return;

        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        var pt = e.GetPosition(this);

        // Hit-test order matters: the × badge overlaps the TopRight handle and must win, and
        // handles must win over the interiors they sit on. Within each pass, iterate in reverse
        // so the most recently added (visually topmost) frame wins an overlap.
        if (SelectedFrameIndex >= 0 && SelectedFrameIndex < Frames.Count)
        {
            var selected = EffectiveRect(SelectedFrameIndex);
            if (Distance(BadgeCenter(selected, imgRect, scale), pt) <= BadgeRadius + 2)
            {
                RemoveFrameAt(SelectedFrameIndex);
                e.Handled = true;
                return;
            }
        }

        for (var i = Frames.Count - 1; i >= 0; i--)
        {
            var rect = Frames[i];
            foreach (var handle in FrameGeometry.AllHandles)
            {
                var hp = FrameGeometry.HandlePoint(rect, handle);
                var canvasPt = new Point(imgRect.X + hp.X * scale, imgRect.Y + hp.Y * scale);
                if (Distance(canvasPt, pt) <= HandleHitRadius)
                {
                    BeginDrag(i, handle, rect, ToImageSpace(pt, imgRect, scale), e);
                    return;
                }
            }
        }

        for (var i = Frames.Count - 1; i >= 0; i--)
        {
            var rect = Frames[i];
            var left = imgRect.X + rect.X * scale;
            var top = imgRect.Y + rect.Y * scale;
            if (pt.X >= left && pt.X <= left + rect.Width * scale &&
                pt.Y >= top && pt.Y <= top + rect.Height * scale)
            {
                BeginDrag(i, FrameHandleKind.Move, rect, ToImageSpace(pt, imgRect, scale), e);
                return;
            }
        }

        // Empty space: start drawing a new frame.
        if (imgRect.Contains(pt))
        {
            _rubberBandOrigin = ToImageSpace(pt, imgRect, scale);
            _rubberBandRect = null;
            SelectedFrameIndex = -1;
            e.Pointer.Capture(this);
            SetInteracting(true);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    private void BeginDrag(int index, FrameHandleKind handle, FixedFrameRect rect, Point imagePointer, PointerPressedEventArgs e)
    {
        _activeDrag = (index, handle);
        _workingRect = rect;
        _lastImagePointer = imagePointer;
        SelectedFrameIndex = index;
        e.Pointer.Capture(this);
        SetInteracting(true);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEditingEnabled || Frames == null) return;

        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        var imagePoint = ToImageSpace(e.GetPosition(this), imgRect, scale);

        if (_rubberBandOrigin is { } origin)
        {
            _rubberBandRect = FrameGeometry.FromDragCorners(origin, imagePoint, SourceImageSize.Width, SourceImageSize.Height);
            InvalidateVisual();
            return;
        }

        if (_activeDrag is not { } drag || _workingRect is not { } working) return;
        if (drag.FrameIndex < 0 || drag.FrameIndex >= Frames.Count) return;

        if (drag.Handle == FrameHandleKind.Move)
        {
            _workingRect = FrameGeometry.Move(working,
                imagePoint.X - _lastImagePointer.X, imagePoint.Y - _lastImagePointer.Y,
                SourceImageSize.Width, SourceImageSize.Height);
            _lastImagePointer = imagePoint;
        }
        else
        {
            _workingRect = FrameGeometry.ResolveResize(working, drag.Handle,
                imagePoint.X, imagePoint.Y, SourceImageSize.Width, SourceImageSize.Height);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Frames == null) return;

        var committedRubberBand = false;

        if (_rubberBandOrigin != null)
        {
            if (_rubberBandRect is { } band &&
                FrameGeometry.IsCommittableSize(band, SourceImageSize.Width, SourceImageSize.Height))
            {
                Frames.Add(band);
                SelectedFrameIndex = Frames.Count - 1;
                committedRubberBand = true;
            }
            _rubberBandOrigin = null;
            _rubberBandRect = null;
        }

        // Write the drag's working copy back to the collection exactly once, here.
        var committedTransform = false;
        if (_activeDrag is { } drag && _workingRect is { } working &&
            drag.FrameIndex >= 0 && drag.FrameIndex < Frames.Count)
        {
            if (!Frames[drag.FrameIndex].Equals(working))
            {
                Frames[drag.FrameIndex] = working;
                committedTransform = true;
            }
        }

        var wasInteracting = _activeDrag != null || committedRubberBand;
        _activeDrag = null;
        _workingRect = null;

        if (wasInteracting || committedRubberBand)
        {
            e.Pointer.Capture(null);
            SetInteracting(false);
        }

        if (committedRubberBand) EditCommitted?.Invoke(this, FrameEditKind.Structural);
        else if (committedTransform) EditCommitted?.Invoke(this, FrameEditKind.Transform);

        InvalidateVisual();
    }

    private void RemoveFrameAt(int index)
    {
        if (Frames == null || index < 0 || index >= Frames.Count) return;
        Frames.RemoveAt(index);
        SelectedFrameIndex = Frames.Count > 0 ? Math.Min(index, Frames.Count - 1) : -1;
        EditCommitted?.Invoke(this, FrameEditKind.Structural);
        InvalidateVisual();
    }

    private void SetInteracting(bool interacting) => InteractionChanged?.Invoke(this, interacting);

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // A Control with nothing painted is not hit-testable in Avalonia, so pointer events would
        // fall straight through to the panel underneath and no frame could ever be drawn. Filling
        // the bounds with a fully transparent brush is what makes the surface interactive — the
        // same reason the read-only Canvas this replaced carried Background="Transparent".
        if (IsEditingEnabled)
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (Frames == null || (Frames.Count == 0 && _rubberBandRect == null)) return;

        var (imgRect, scale) = GetDisplayedImageRect();
        if (scale <= 0) return;

        for (var i = 0; i < Frames.Count; i++)
        {
            var rect = EffectiveRect(i);
            var color = FixedFrameColorPalette.GetColor(i);
            var isSelected = i == SelectedFrameIndex;

            var displayRect = new Rect(
                imgRect.X + rect.X * scale, imgRect.Y + rect.Y * scale,
                Math.Max(0, rect.Width * scale), Math.Max(0, rect.Height * scale));

            var stroke = new Pen(new SolidColorBrush(color), isSelected ? 3 : 2,
                isSelected ? new DashStyle(new double[] { 6, 3 }, 0) : null);
            var fill = new SolidColorBrush(Color.FromArgb(isSelected ? (byte)55 : (byte)30, color.R, color.G, color.B));
            context.DrawRectangle(fill, stroke, displayRect);

            // Every frame carries its number, not just the selected one. Frame order decides
            // output page order, so which frame is which is exactly what the operator needs to
            // read at a glance — and colour alone can't say it when a batch has more frames than
            // the palette has entries. Counter-rotated so it stays upright while the frame turns.
            using (PushUprightText(context, new Point(displayRect.X + 3, displayRect.Y + 3)))
                DrawFrameNumber(context, displayRect, i + 1, color);

            if (!IsEditingEnabled) continue;

            foreach (var handle in FrameGeometry.AllHandles)
            {
                var hp = FrameGeometry.HandlePoint(rect, handle);
                DrawHandle(context, imgRect.X + hp.X * scale, imgRect.Y + hp.Y * scale, color);
            }

            if (isSelected)
            {
                DrawRemoveBadge(context, BadgeCenter(rect, imgRect, scale), color);
                using (PushUprightText(context, new Point(displayRect.X + 4, displayRect.Y + 4)))
                    DrawSizeReadout(context, rect, displayRect);
            }
        }

        if (_rubberBandRect is { } band)
        {
            var bandRect = new Rect(
                imgRect.X + band.X * scale, imgRect.Y + band.Y * scale,
                Math.Max(0, band.Width * scale), Math.Max(0, band.Height * scale));
            var color = FixedFrameColorPalette.GetColor(Frames.Count);
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
                new Pen(new SolidColorBrush(color), 2, new DashStyle(new double[] { 4, 3 }, 0)),
                bandRect);
            using (PushUprightText(context, new Point(bandRect.X + 4, bandRect.Y + 4)))
                DrawSizeReadout(context, band, bandRect);
        }
    }

    /// <summary>Pushes a transform that rotates the coming draw calls by the negative of the view
    /// rotation, about <paramref name="pivot"/> — so a label painted while the overlay is turned
    /// still reads upright, hinged at the frame corner it belongs to. A no-op push at 0°.</summary>
    private DrawingContext.PushedState PushUprightText(DrawingContext context, Point pivot)
    {
        var angle = ViewRotationRadians;
        if (angle == 0)
            return context.PushTransform(Matrix.Identity);

        var m = Matrix.CreateTranslation(-pivot.X, -pivot.Y)
              * Matrix.CreateRotation(-angle)
              * Matrix.CreateTranslation(pivot.X, pivot.Y);
        return context.PushTransform(m);
    }

    /// <summary>Draws a frame's 1-based number in its top-left corner, on the frame's own colour
    /// so number and outline read as the same object. Sits inside the frame rather than outside,
    /// so it can't be clipped at the edge of the live view or overlap a neighbour.</summary>
    private static void DrawFrameNumber(DrawingContext context, Rect displayRect, int number, Color color)
    {
        if (displayRect.Width < 22 || displayRect.Height < 22) return;

        var text = new FormattedText(number.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);

        const double padding = 5;
        var width = Math.Max(18, text.Width + padding * 2);
        var height = text.Height + 3;
        var badge = new Rect(displayRect.X + 3, displayRect.Y + 3, width, height);

        context.DrawRectangle(new SolidColorBrush(color), null, new RoundedRect(badge, 3));
        context.DrawText(text, new Point(badge.X + (badge.Width - text.Width) / 2, badge.Y + 1));
    }

    private static void DrawHandle(DrawingContext context, double cx, double cy, Color color)
    {
        var rect = new Rect(cx - HandleSize / 2, cy - HandleSize / 2, HandleSize, HandleSize);
        context.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(color), 2), rect);
    }

    private static void DrawRemoveBadge(DrawingContext context, Point center, Color color)
    {
        context.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, 1.5), center, BadgeRadius, BadgeRadius);
        const double arm = 3.5;
        var pen = new Pen(Brushes.White, 1.8);
        context.DrawLine(pen, new Point(center.X - arm, center.Y - arm), new Point(center.X + arm, center.Y + arm));
        context.DrawLine(pen, new Point(center.X - arm, center.Y + arm), new Point(center.X + arm, center.Y - arm));
    }

    /// <summary>Draws the frame's size in CAPTURE pixels — what the operator will actually get on
    /// disk — rather than the live-preview pixels they happen to be dragging in.</summary>
    private void DrawSizeReadout(DrawingContext context, FixedFrameRect rect, Rect displayRect)
    {
        if (SourceImageSize.Width <= 0 || SourceImageSize.Height <= 0) return;

        double w = rect.Width, h = rect.Height;
        if (CaptureImageSize.Width > 0 && CaptureImageSize.Height > 0)
        {
            w = rect.Width * (CaptureImageSize.Width / SourceImageSize.Width);
            h = rect.Height * (CaptureImageSize.Height / SourceImageSize.Height);
        }

        var text = new FormattedText(
            $"{Math.Round(w)} × {Math.Round(h)} px",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, Brushes.White);

        var pad = 4.0;
        var origin = new Point(displayRect.X + pad, displayRect.Y + pad);
        context.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(190, 15, 16, 17)), null,
            new Rect(origin.X - pad / 2, origin.Y - pad / 2, text.Width + pad, text.Height + pad), 3, 3);
        context.DrawText(text, origin);
    }
}
