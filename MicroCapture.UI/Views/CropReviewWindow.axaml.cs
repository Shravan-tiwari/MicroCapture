using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class CropReviewWindow : UserControl
{
    // ---------- Zoom model ----------
    //
    // _scale is the ONE piece of zoom state: absolute image-DIP -> screen-DIP factor, applied
    // as a LayoutTransformControl ScaleTransform. The scrollable extent and the scrollbars are
    // derived from it by Avalonia's own layout, so there is no fit<->viewport feedback loop of
    // the kind the old explicit-Width/Height approach stalled on.
    //
    // "Fit" is not a stored mode — it is _scale == _fitScale within an epsilon.
    //
    // All wiring is done via events declared in the .axaml (PointerWheelChanged, Click,
    // SizeChanged). The earlier version resolved the named controls from the constructor / on
    // Loaded and attached handlers in code; on macOS the name lookup came back null there and
    // zoom got no handlers at all. Handlers declared in XAML don't have that timing problem,
    // and by the time one fires the visual tree exists so FindControl below is safe.

    private const double AbsoluteMaxScale = 8.0;   // 8x the image's own pixels, independent of fit
    private const double ButtonZoomStep = 1.6;

    private double _scale = 1.0;
    private double _fitScale = 1.0;
    // True until the operator zooms to an explicit level. While true, a resize or a new image
    // re-fits automatically; once false, those leave the chosen zoom alone.
    private bool _followFit = true;
    // Guards the re-entrancy a synchronous UpdateLayout() inside a zoom step can cause.
    private bool _applyingZoom;

    private bool _isPanning;
    private Point _panPointerStart;
    private Vector _panOffsetStart;

    public CropReviewWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private ScrollViewer? Scroller => this.FindControl<ScrollViewer>("PreviewScroller");
    private LayoutTransformControl? Host => this.FindControl<LayoutTransformControl>("ZoomHost");
    private Image? TargetImage => this.FindControl<Image>("AdjustTargetImage");
    private ScaleTransform? ScaleXf => Host?.LayoutTransform as ScaleTransform;
    private TextBlock? ZoomLabelText => this.FindControl<TextBlock>("ZoomLabel");

    // ---------- Fit / sizing ----------

    /// <summary>The ScrollViewer's own content area — bounds minus whatever its scrollbars are
    /// occupying. Fit is measured against this so a fitted image is never a scrollbar-width too
    /// wide.</summary>
    private Size ViewportSize()
    {
        var scroller = Scroller;
        if (scroller == null) return default;
        var v = scroller.Viewport;
        if (v.Width > 0 && v.Height > 0) return v;
        return scroller.Bounds.Size; // before first layout, Viewport is zero
    }

    /// <summary>Image size in the DIPs it draws at with Stretch=None — <see cref="Bitmap.Size"/>,
    /// not PixelSize — so the scale factor maps 1:1 to what is on screen.</summary>
    private Size ImageDipSize() =>
        TargetImage?.Source is Bitmap bmp ? bmp.Size : default;

    /// <summary>Scale at which the whole page fits the viewport, capped at 1.0 so "Fit" never
    /// upsamples a small page.</summary>
    private double ComputeFitScale()
    {
        var area = ViewportSize();
        var img = ImageDipSize();
        if (area.Width <= 0 || area.Height <= 0 || img.Width <= 0 || img.Height <= 0) return 0;
        return Math.Min(1.0, Math.Min(area.Width / img.Width, area.Height / img.Height));
    }

    private double MinScale() => _fitScale > 0 ? _fitScale : 0.01;
    private double MaxScale() => Math.Max(MinScale(), AbsoluteMaxScale);

    /// <summary>Recomputes the fitted scale; while still following fit, snaps the current scale
    /// to it. A no-op once the operator has zoomed manually.</summary>
    private void RefitIfFollowing()
    {
        if (_applyingZoom) return;
        var fit = ComputeFitScale();
        if (fit <= 0) return;
        _fitScale = fit;
        if (_followFit)
            SetScale(fit, anchor: null, keepFollowing: true);
        else
            UpdateZoomLabel();
    }

    // ---------- Core zoom ----------

    /// <summary>Applies an absolute scale, re-anchoring so the image point under
    /// <paramref name="anchor"/> (viewport coordinates) stays under it. The
    /// LayoutTransformControl makes the scrolled content exactly imageDip * scale, so the anchor
    /// maths is a straight fraction-of-image mapping (plus the ScrollViewer's centring pad while
    /// the content is smaller than the viewport).</summary>
    private void SetScale(double target, Point? anchor, bool keepFollowing = false)
    {
        var scroller = Scroller;
        var scaleXf = ScaleXf;
        if (scroller == null || scaleXf == null) return;

        var img = ImageDipSize();
        if (img.Width <= 0 || img.Height <= 0) return;

        target = Math.Clamp(target, MinScale(), MaxScale());
        if (Math.Abs(target - _scale) < 0.00001 && !keepFollowing)
        {
            UpdateCursor();
            return;
        }

        var oldViewport = ViewportSize();
        var point = anchor ?? new Point(oldViewport.Width / 2, oldViewport.Height / 2);

        var oldContentW = img.Width * _scale;
        var oldContentH = img.Height * _scale;
        var oldPadX = Math.Max(0, (oldViewport.Width - oldContentW) / 2);
        var oldPadY = Math.Max(0, (oldViewport.Height - oldContentH) / 2);
        var fx = oldContentW > 0 ? Math.Clamp((scroller.Offset.X + point.X - oldPadX) / oldContentW, 0, 1) : 0.5;
        var fy = oldContentH > 0 ? Math.Clamp((scroller.Offset.Y + point.Y - oldPadY) / oldContentH, 0, 1) : 0.5;

        _applyingZoom = true;
        try
        {
            _scale = target;
            _followFit = keepFollowing;
            scaleXf.ScaleX = target;
            scaleXf.ScaleY = target;

            // Force the extent to catch up before touching Offset: ScrollViewer clamps a new
            // offset against the extent from the LAST layout pass.
            Host?.InvalidateMeasure();
            scroller.UpdateLayout();

            // Re-read the viewport after layout — crossing fit adds/removes scrollbars.
            var newViewport = ViewportSize();
            var newContentW = img.Width * target;
            var newContentH = img.Height * target;
            var newPadX = Math.Max(0, (newViewport.Width - newContentW) / 2);
            var newPadY = Math.Max(0, (newViewport.Height - newContentH) / 2);

            var desiredX = newPadX + fx * newContentW - point.X;
            var desiredY = newPadY + fy * newContentH - point.Y;
            var maxX = Math.Max(0, newContentW - newViewport.Width);
            var maxY = Math.Max(0, newContentH - newViewport.Height);
            scroller.Offset = new Vector(
                Math.Clamp(desiredX, 0, maxX),
                Math.Clamp(desiredY, 0, maxY));
        }
        finally
        {
            _applyingZoom = false;
        }

        UpdateZoomLabel();
        UpdateCursor();
    }

    private void ZoomBy(double factor, Point? anchor) => SetScale(_scale * factor, anchor);

    private void UpdateZoomLabel()
    {
        var label = ZoomLabelText;
        if (label == null) return;
        var atFit = _fitScale > 0 && Math.Abs(_scale - _fitScale) < 0.005;
        label.Text = atFit ? "Fit" : $"{_scale * 100:0}%";
    }

    /// <summary>Hand cursor whenever there is somewhere to pan to (past fit), plain arrow at
    /// fit. Re-checked after every zoom change.</summary>
    private void UpdateCursor()
    {
        var scroller = Scroller;
        if (scroller == null) return;
        var canPan = _scale > MinScale() + 0.001;
        scroller.Cursor = new Cursor(canPan ? StandardCursorType.Hand : StandardCursorType.Arrow);
    }

    // ---------- XAML event handlers ----------

    private void OnPreviewImageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Fires on first render and on every Source swap (rotate / re-decode). Either way the
        // fitted scale is stale — snap back to Fit so the whole new page shows.
        _followFit = true;
        Dispatcher.UIThread.Post(RefitIfFollowing, DispatcherPriority.Background);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ZoomBy(ButtonZoomStep, anchor: null);
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ZoomBy(1 / ButtonZoomStep, anchor: null);
    private void OnZoomFitClick(object? sender, RoutedEventArgs e)
    {
        _followFit = true;
        RefitIfFollowing();
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        var scroller = Scroller;
        if (scroller == null) return;

        // Normalise the notch: a mouse wheel reports +/-1 per detent, a trackpad reports many
        // small fractional events. macOS can put the delta on X during a vertical two-finger
        // scroll, so fall back to it when Y is flat. Bounded exponent -> a firm scroll zooms
        // faster than a gentle one without teleporting.
        var delta = Math.Abs(e.Delta.Y) > 0.0001 ? e.Delta.Y : e.Delta.X;
        if (Math.Abs(delta) < 0.0001) return;
        var factor = Math.Pow(1.15, Math.Clamp(delta, -3.0, 3.0));

        SetScale(_scale * factor, e.GetPosition(scroller));
        // Claim the wheel, or the ScrollViewer scrolls too and one notch both zooms and jumps.
        e.Handled = true;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var scroller = Scroller;
        if (scroller == null || _scale <= MinScale() + 0.001) return;
        if (!e.GetCurrentPoint(scroller).Properties.IsLeftButtonPressed) return;

        _isPanning = true;
        _panPointerStart = e.GetPosition(scroller);
        _panOffsetStart = scroller.Offset;
        e.Pointer.Capture(scroller);
        scroller.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var scroller = Scroller;
        if (scroller == null) return;

        var delta = e.GetPosition(scroller) - _panPointerStart;
        var img = ImageDipSize();
        var viewport = ViewportSize();
        var maxX = Math.Max(0, img.Width * _scale - viewport.Width);
        var maxY = Math.Max(0, img.Height * _scale - viewport.Height);
        scroller.Offset = new Vector(
            Math.Clamp(_panOffsetStart.X - delta.X, 0, maxX),
            Math.Clamp(_panOffsetStart.Y - delta.Y, 0, maxY));
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e) => StopPanning();

    private void StopPanning()
    {
        if (!_isPanning) return;
        _isPanning = false;
        UpdateCursor();
    }

    // ---------- Lifecycle ----------

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is CropReviewViewModel vm)
        {
            vm.ConfirmBulkApplyRequested += async (s, request) =>
            {
                // No longer a Window itself (embedded in MainWindow, see MainWindow.axaml's
                // ActiveCropReview host) — ConfirmDialog needs an actual owner Window for
                // centering/modality, so resolve the containing top-level instead of using
                // `this`.
                //
                // When there is no owner to ask through, proceed rather than decline. This used
                // to answer "no" silently, which is the worst of both: the operator is asked
                // nothing, told nothing, and the bulk apply they explicitly asked for simply
                // does not happen — indistinguishable from the feature being broken. The
                // confirmation is a safety net over an intent the operator has already stated by
                // pressing the button; losing the net must not lose the action too. What was
                // applied is reported on the status line either way.
                if (TopLevel.GetTopLevel(this) is not Window owner)
                {
                    request.OnAnswered(true);
                    return;
                }

                request.OnAnswered(await ConfirmDialog.AskAsync(owner, request.Message, "Apply Adjustments"));
            };
        }
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
