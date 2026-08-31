using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class CropReviewWindow : UserControl
{
    // Zoom as a multiple of "fits the window", so 1.0 always means the whole page is visible
    // whatever its size or the window's.
    private const double FitZoom = 1.0;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.25;

    private double _zoom = FitZoom;
    private Size _lastAppliedSize;

    // Drag-to-pan state. Only active once zoomed past fit — at fit there is nothing to pan, and
    // treating every click as a pan-start would swallow clicks meant for whatever is behind the
    // image (there is nothing there today, but the intent is "drag moves the view", not "the
    // whole image is a button").
    private bool _isPanning;
    private Point _panPointerStart;
    private Vector _panOffsetStart;

    public CropReviewWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachZoomHandlers();
    }

    private void AttachZoomHandlers()
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        var image = this.FindControl<Image>("AdjustTargetImage");
        var container = this.FindControl<Grid>("ContainerGrid");
        if (scroller == null || image == null || container == null) return;

        // Tunnelling: ScrollViewer treats the wheel as scroll and marks it handled, so a
        // bubbling handler would only ever see the wheel when the image already fits.
        scroller.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        scroller.PointerPressed += OnPreviewPointerPressed;
        scroller.PointerMoved += OnPreviewPointerMoved;
        scroller.PointerReleased += OnPreviewPointerReleased;
        scroller.PointerCaptureLost += (_, _) => StopPanning();

        // The fitted size is measured against the container, never the ScrollViewer or the image.
        // Measuring it against either of those is what froze this window: sizing the image from
        // its own container's bounds means every resize feeds the next one, and the layout pass
        // never settles. The container's size comes from the window's column definition and does
        // not depend on what is inside it, so nothing here can loop. Panning below only ever
        // touches scroller.Offset, which the container's bounds don't depend on either.
        container.PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty) ApplyZoom();
        };
        image.PropertyChanged += (_, args) =>
        {
            // A rotation swaps the page's proportions, so the fitted size has to be recomputed
            // whenever the preview itself is replaced.
            if (args.Property == Image.SourceProperty) ApplyZoom();
        };

        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => ZoomBy(ZoomStep, anchor: null);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => ZoomBy(1 / ZoomStep, anchor: null);
        this.FindControl<Button>("ZoomFitButton")!.Click += (_, _) => { _zoom = FitZoom; ApplyZoom(); UpdateCursor(); };
    }

    /// <summary>Scale at which the whole preview fits the viewing area — the meaning of zoom 1.0.
    /// Measured against the container, for the reason given in <see cref="AttachZoomHandlers"/>.</summary>
    private double FitScale()
    {
        var container = this.FindControl<Grid>("ContainerGrid");
        var source = this.FindControl<Image>("AdjustTargetImage")?.Source;
        if (container == null || source == null) return 0;

        var area = container.Bounds.Size;
        var image = source.Size;
        if (area.Width <= 0 || area.Height <= 0 || image.Width <= 0 || image.Height <= 0) return 0;

        return Math.Min(area.Width / image.Width, area.Height / image.Height);
    }

    /// <summary>Sizes the image to the current zoom. An explicit size rather than a stretch mode,
    /// so the ScrollViewer has something genuinely larger than itself to scroll over.</summary>
    private void ApplyZoom()
    {
        var image = this.FindControl<Image>("AdjustTargetImage");
        var source = image?.Source;
        if (image == null || source == null) return;

        var fit = FitScale();
        if (fit <= 0) return;

        var scale = fit * _zoom;
        var size = new Size(
            Math.Max(1, source.Size.Width * scale),
            Math.Max(1, source.Size.Height * scale));

        // Belt and braces against the freeze described above: even if something upstream did
        // manage to feed a size change back round, an unchanged size stops the cycle here.
        if (Math.Abs(size.Width - _lastAppliedSize.Width) < 0.5
            && Math.Abs(size.Height - _lastAppliedSize.Height) < 0.5) return;
        _lastAppliedSize = size;

        image.Width = size.Width;
        image.Height = size.Height;

        var label = this.FindControl<TextBlock>("ZoomLabel");
        if (label != null)
            label.Text = Math.Abs(_zoom - FitZoom) < 0.001 ? "Fit" : $"{scale * 100:0}%";
    }

    /// <summary>Zooms about a point in the scroller's own coordinates — the cursor, when there is
    /// one — so the thing under the pointer is still under it afterwards instead of the view
    /// re-centring and sliding whatever was being looked at off screen. Only touches
    /// scroller.Offset, which the freeze-avoidance in <see cref="AttachZoomHandlers"/> does not
    /// depend on, so this cannot reintroduce it.</summary>
    private void ZoomBy(double factor, Point? anchor)
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null) return;

        var fit = FitScale();
        if (fit <= 0) return;

        var oldScale = fit * _zoom;
        var next = Math.Clamp(_zoom * factor, FitZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.0001) return;
        var newScale = fit * next;

        var point = anchor ?? new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2);
        var offset = scroller.Offset;

        // Where the anchor sits within the image, in the image's own zoomed pixels, before the
        // change — recovered from the current offset since the image is centred within the
        // scroller whenever it's smaller than the viewport, not pinned at the origin.
        var contentX = offset.X + point.X;
        var contentY = offset.Y + point.Y;

        _zoom = next;
        ApplyZoom();

        // Re-measure before setting an offset: ScrollViewer clamps against its extent from the
        // last layout pass, and setting the offset before that updates leaves the anchor off by
        // whatever the size just changed by.
        scroller.UpdateLayout();

        var ratio = newScale / oldScale;
        scroller.Offset = new Vector(
            Math.Max(0, contentX * ratio - point.X),
            Math.Max(0, contentY * ratio - point.Y));

        UpdateCursor();
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null) return;

        ZoomBy(e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(scroller));
        // Claim the wheel, or the ScrollViewer scrolls as well and one notch both zooms and
        // jumps the page.
        e.Handled = true;
    }

    /// <summary>A hand cursor whenever there is somewhere to drag to — i.e. past Fit — and the
    /// ordinary arrow at Fit, where a drag would have nothing to do. Checked after every zoom
    /// change, not just on hover, since zooming in with the wheel or the + button can make
    /// dragging newly available without the pointer having moved.</summary>
    private void UpdateCursor()
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null) return;
        scroller.Cursor = new Cursor(_zoom > FitZoom + 0.001 ? StandardCursorType.Hand : StandardCursorType.Arrow);
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null || _zoom <= FitZoom + 0.001) return;
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
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null) return;

        var delta = e.GetPosition(scroller) - _panPointerStart;
        scroller.Offset = new Vector(
            Math.Max(0, _panOffsetStart.X - delta.X),
            Math.Max(0, _panOffsetStart.Y - delta.Y));
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e) => StopPanning();

    private void StopPanning()
    {
        if (!_isPanning) return;
        _isPanning = false;
        UpdateCursor();
    }

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
