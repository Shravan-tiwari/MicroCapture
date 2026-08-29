using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

public partial class CropReviewWindow : UserControl
{
    // Zoom as a multiple of "fits the window", so 1.0 always means the whole page is visible
    // whatever its size or the window's. The alternative — a multiple of the image's own pixel
    // size — makes the starting zoom a different number for every page.
    private const double FitZoom = 1.0;
    private const double MaxZoom = 12.0;
    private const double ZoomStep = 1.25;
    // How much of the preview's own detail the loupe shows: the sampled patch is the loupe's
    // size divided by this, so 2 means two loupe pixels per preview pixel.
    private const double LoupeMagnification = 2.0;

    private double _zoom = FitZoom;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;

    public CropReviewWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachViewportHandlers();
    }

    private void AttachViewportHandlers()
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        var image = this.FindControl<Image>("AdjustTargetImage");
        if (scroller == null || image == null) return;

        // Tunnelling: ScrollViewer treats the wheel as scroll and handles the event, so a
        // bubbling handler would only ever see the wheel when the image already fits.
        scroller.AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);
        scroller.PointerMoved += OnPreviewPointerMoved;
        scroller.PointerExited += (_, _) => HideLoupe();
        scroller.PointerPressed += OnPreviewPointerPressed;
        scroller.PointerReleased += (_, _) => _isPanning = false;

        // The fitted size depends on the viewport, so it has to be recomputed when either the
        // window or the image changes — a rotation swaps the page's proportions entirely.
        scroller.PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty) ApplyZoom();
        };
        image.PropertyChanged += (_, args) =>
        {
            if (args.Property == Image.SourceProperty) ApplyZoom();
        };

        this.FindControl<Button>("ZoomInButton")!.Click += (_, _) => ZoomBy(ZoomStep, null);
        this.FindControl<Button>("ZoomOutButton")!.Click += (_, _) => ZoomBy(1 / ZoomStep, null);
        this.FindControl<Button>("ZoomFitButton")!.Click += (_, _) => { _zoom = FitZoom; ApplyZoom(); };
        this.FindControl<Button>("ZoomActualButton")!.Click += (_, _) =>
        {
            var fit = FitScale();
            // "100%" means one preview pixel per screen pixel, which is a zoom of 1/fit.
            if (fit > 0) _zoom = Math.Clamp(1 / fit, FitZoom, MaxZoom);
            ApplyZoom();
        };
        this.FindControl<ToggleButton>("LoupeToggle")!.IsCheckedChanged += (_, _) =>
        {
            if (this.FindControl<ToggleButton>("LoupeToggle")?.IsChecked != true) HideLoupe();
        };
    }

    /// <summary>Scale at which the whole preview fits the viewport — the meaning of zoom 1.0.</summary>
    private double FitScale()
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        var source = this.FindControl<Image>("AdjustTargetImage")?.Source;
        if (scroller == null || source == null) return 0;

        var viewport = scroller.Bounds.Size;
        var image = source.Size;
        if (viewport.Width <= 0 || viewport.Height <= 0 || image.Width <= 0 || image.Height <= 0) return 0;

        return Math.Min(viewport.Width / image.Width, viewport.Height / image.Height);
    }

    /// <summary>Sizes the image to the current zoom. Explicit width/height rather than a stretch
    /// mode, so the ScrollViewer has something genuinely larger than itself to pan over.</summary>
    private void ApplyZoom()
    {
        var image = this.FindControl<Image>("AdjustTargetImage");
        var source = image?.Source;
        if (image == null || source == null) return;

        var fit = FitScale();
        if (fit <= 0) return;

        var scale = fit * _zoom;
        image.Width = Math.Max(1, source.Size.Width * scale);
        image.Height = Math.Max(1, source.Size.Height * scale);

        var label = this.FindControl<TextBlock>("ZoomLabel");
        if (label != null)
            label.Text = Math.Abs(_zoom - FitZoom) < 0.001 ? "Fit" : $"{scale * 100:0}%";
    }

    /// <summary>Zooms about a point in the viewport — the cursor when there is one. Without the
    /// offset correction the view would zoom about its top-left corner and slide whatever the
    /// operator was looking at off the screen, which is the whole reason to zoom at the pointer.</summary>
    private void ZoomBy(double factor, Point? anchor)
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null) return;

        var fit = FitScale();
        if (fit <= 0) return;

        var oldScale = fit * _zoom;
        var newZoom = Math.Clamp(_zoom * factor, FitZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;
        var newScale = fit * newZoom;

        var focus = anchor ?? new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2);
        var offset = scroller.Offset;

        // Where the focus point sits within the content, in image pixels, before the change.
        var contentX = (offset.X + focus.X) / oldScale;
        var contentY = (offset.Y + focus.Y) / oldScale;

        _zoom = newZoom;
        ApplyZoom();

        // Re-measure before setting an offset, or the scroller clamps against its old extent and
        // the anchor drifts on every step.
        scroller.UpdateLayout();
        scroller.Offset = new Vector(
            Math.Max(0, contentX * newScale - focus.X),
            Math.Max(0, contentY * newScale - focus.Y));
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null) return;

        ZoomBy(e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(scroller));
        UpdateLoupe(e.GetPosition(scroller));
        // Claim the wheel: otherwise the ScrollViewer also scrolls, so one notch both zooms and
        // jumps the page.
        e.Handled = true;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null || !e.GetCurrentPoint(scroller).Properties.IsLeftButtonPressed) return;
        _isPanning = _zoom > FitZoom;
        _panStart = e.GetPosition(scroller);
        _panStartOffset = scroller.Offset;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        if (scroller == null) return;
        var position = e.GetPosition(scroller);

        if (_isPanning && e.GetCurrentPoint(scroller).Properties.IsLeftButtonPressed)
        {
            var delta = position - _panStart;
            scroller.Offset = new Vector(
                Math.Max(0, _panStartOffset.X - delta.X),
                Math.Max(0, _panStartOffset.Y - delta.Y));
            HideLoupe();
            return;
        }

        _isPanning = false;
        UpdateLoupe(position);
    }

    /// <summary>Points the loupe at whatever the cursor is over and parks it clear of the
    /// pointer.</summary>
    private void UpdateLoupe(Point positionInScroller)
    {
        var loupe = this.FindControl<Border>("Loupe");
        var loupeImage = this.FindControl<Image>("LoupeImage");
        var image = this.FindControl<Image>("AdjustTargetImage");
        var container = this.FindControl<Grid>("ContainerGrid");
        var scroller = this.FindControl<ScrollViewer>("PreviewScroller");
        var enabled = this.FindControl<ToggleButton>("LoupeToggle")?.IsChecked == true;

        if (loupe == null || loupeImage == null || image == null || container == null || scroller == null
            || image.Source is not Bitmap bitmap || !enabled)
        {
            HideLoupe();
            return;
        }

        // Scroller coordinates -> the image's own pixels. Going via the image control's bounds
        // rather than the scroll offset keeps this correct while the image is smaller than the
        // viewport and therefore centred rather than at the origin.
        var inImage = scroller.TranslatePoint(positionInScroller, image);
        if (inImage == null) { HideLoupe(); return; }

        var scale = image.Bounds.Width / bitmap.Size.Width;
        if (scale <= 0) { HideLoupe(); return; }

        var pixelX = inImage.Value.X / scale;
        var pixelY = inImage.Value.Y / scale;
        if (pixelX < 0 || pixelY < 0 || pixelX >= bitmap.Size.Width || pixelY >= bitmap.Size.Height)
        {
            HideLoupe();
            return;
        }

        // Sample a patch of the preview around the cursor and blow it up to fill the loupe.
        var patch = loupe.Width / LoupeMagnification;
        var half = patch / 2;
        var left = Math.Clamp(pixelX - half, 0, Math.Max(0, bitmap.Size.Width - patch));
        var top = Math.Clamp(pixelY - half, 0, Math.Max(0, bitmap.Size.Height - patch));
        var width = Math.Min(patch, bitmap.Size.Width - left);
        var height = Math.Min(patch, bitmap.Size.Height - top);
        if (width < 1 || height < 1) { HideLoupe(); return; }

        var previous = loupeImage.Source as CroppedBitmap;
        loupeImage.Source = new CroppedBitmap(bitmap,
            new PixelRect((int)left, (int)top, (int)width, (int)height));
        previous?.Dispose();

        // Offset from the cursor, flipping to the other side near an edge so the loupe never
        // ends up half outside the window covering the thing being inspected.
        var inContainer = scroller.TranslatePoint(positionInScroller, container) ?? positionInScroller;
        const double gap = 24;
        var x = inContainer.X + gap;
        if (x + loupe.Width > container.Bounds.Width) x = inContainer.X - gap - loupe.Width;
        var y = inContainer.Y + gap;
        if (y + loupe.Height > container.Bounds.Height) y = inContainer.Y - gap - loupe.Height;

        loupe.Margin = new Thickness(
            Math.Clamp(x, 0, Math.Max(0, container.Bounds.Width - loupe.Width)),
            Math.Clamp(y, 0, Math.Max(0, container.Bounds.Height - loupe.Height)),
            0, 0);
        loupe.IsVisible = true;
    }

    private void HideLoupe()
    {
        var loupe = this.FindControl<Border>("Loupe");
        if (loupe != null) loupe.IsVisible = false;
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
                var confirmed = TopLevel.GetTopLevel(this) is Window owner
                    && await ConfirmDialog.AskAsync(owner, request.Message, "Apply Adjustments");
                request.OnAnswered(confirmed);
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
