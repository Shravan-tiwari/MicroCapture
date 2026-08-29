using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Windows.Input;
using MicroCapture.UI.Controls;
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
        // Drag-to-reorder drop targets are the filmstrip tiles, but DragDrop's events are routed
        // events that Avalonia won't accept as XAML attributes — so they're handled here at the
        // window and resolved back to whichever tile is under the pointer.
        AddHandler(DragDrop.DragOverEvent, OnThumbnailDragOver);
        AddHandler(DragDrop.DropEvent, OnThumbnailDrop);
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Finds the filmstrip tile an event landed on, by walking up from whatever inner
    /// control actually received it (an Image or TextBlock inside the tile, usually).</summary>
    private static ThumbnailItem? ThumbnailUnder(object? source)
    {
        var current = source as Visual;
        while (current != null)
        {
            if (current is Control { DataContext: ThumbnailItem item }) return item;
            current = current.GetVisualParent();
        }
        return null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            // The cart is in page order, so a new capture lands at the far end, off screen once
            // the batch is more than a screenful long. Scroll to it so the operator still sees
            // what they just shot.
            viewModel.CartAppended -= OnCartAppended;
            viewModel.CartAppended += OnCartAppended;
        }

        var editor = this.FindControl<FixedFrameOverlayEditor>("FixedFrameOverlay");
        if (editor == null) return;

        // The editor owns pointer interaction; the view model owns the frame list and decides
        // what an edit means for persistence and auto-capture. These two events are the whole
        // seam between them.
        editor.EditCommitted -= OnFrameEditCommitted;
        editor.InteractionChanged -= OnFrameInteractionChanged;
        editor.EditCommitted += OnFrameEditCommitted;
        editor.InteractionChanged += OnFrameInteractionChanged;
    }

    private void OnCartAppended(object? sender, EventArgs e)
    {
        // Deferred: the new item hasn't been laid out yet at the moment it's added, so the
        // scroller doesn't know its extent has grown until the next layout pass.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var scroller = this.FindControl<ScrollViewer>("FilmstripScroller");
            scroller?.ScrollToEnd();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnFrameEditCommitted(object? sender, FrameEditKind kind)
    {
        if (DataContext is MainWindowViewModel vm) vm.OnFrameEditCommitted(kind);
    }

    private void OnFrameInteractionChanged(object? sender, bool interacting)
    {
        if (DataContext is MainWindowViewModel vm) vm.OnFrameInteractionChanged(interacting);
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

    // Set by OnThumbnailPointerPressed (which does receive KeyModifiers) just before the
    // Button's own Click fires, so OnThumbnailClick below can tell a plain click from a
    // ctrl/shift-click without RoutedEventArgs carrying modifier state itself.
    private bool _nextThumbnailClickIsSelectToggle;

    // Drag-to-reorder state. A drag only begins once the pointer has actually travelled, so an
    // ordinary click still opens Crop Review rather than every click becoming a micro-drag.
    private const double DragStartDistance = 6;
    private Point? _thumbnailDragOrigin;
    private string? _thumbnailDragJobId;
    private bool _thumbnailDragStarted;

    /// <summary>Identifies the dragged page. A private format rather than text/plain so a drag
    /// from another application can never be mistaken for a page reorder.</summary>
    private const string ThumbnailDragFormat = "microcapture/page";

    private void OnThumbnailPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _nextThumbnailClickIsSelectToggle = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) != 0;

        // Modifier-clicks are multi-select, not drags.
        if (_nextThumbnailClickIsSelectToggle) return;
        if (sender is not Button button) return;

        _thumbnailDragOrigin = e.GetPosition(this);
        _thumbnailDragJobId = button.Tag as string ?? (button.DataContext as ThumbnailItem)?.JobId;
        _thumbnailDragStarted = false;
    }

    private async void OnThumbnailPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_thumbnailDragOrigin is not { } origin || _thumbnailDragJobId == null || _thumbnailDragStarted) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var travelled = e.GetPosition(this) - origin;
        if (Math.Abs(travelled.X) < DragStartDistance && Math.Abs(travelled.Y) < DragStartDistance) return;

        _thumbnailDragStarted = true;
        // The button's Click fires on release; without this a completed drag would also open
        // Crop Review for the page that was dragged.
        _nextThumbnailClickIsSelectToggle = false;

        try
        {
            var data = new DataObject();
            data.Set(ThumbnailDragFormat, _thumbnailDragJobId);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // A failed drag must leave the cart exactly as it was rather than taking the app down.
        }
        finally
        {
            _thumbnailDragOrigin = null;
            _thumbnailDragJobId = null;
        }
    }

    private void OnThumbnailPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _thumbnailDragOrigin = null;
        _thumbnailDragJobId = null;
        // Cleared on the next pointer press; leaving it set here would suppress a genuine click.
        _thumbnailDragStarted = false;
    }

    private void OnThumbnailDragOver(object? sender, DragEventArgs e)
    {
        var overThumbnail = e.Data.Contains(ThumbnailDragFormat) && ThumbnailUnder(e.Source) != null;
        e.DragEffects = overThumbnail ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnThumbnailDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(ThumbnailDragFormat)) return;
        if (e.Data.Get(ThumbnailDragFormat) is not string draggedJobId) return;
        if (ThumbnailUnder(e.Source) is not { } target) return;
        if (DataContext is not MainWindowViewModel vm) return;

        e.Handled = true;
        await vm.ReorderCaptureAsync(draggedJobId, target.JobId);
    }

    private void OnThumbnailClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn)
            {
                var isSelectToggle = _nextThumbnailClickIsSelectToggle;
                _nextThumbnailClickIsSelectToggle = false;
                if (isSelectToggle && btn.DataContext is global::MicroCapture.UI.ViewModels.ThumbnailItem selectItem
                    && DataContext is MicroCapture.UI.ViewModels.MainWindowViewModel selectVm)
                {
                    selectVm.ToggleThumbnailSelection(selectItem);
                    return;
                }

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

    private void OnThumbnailSelectClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.DataContext is MicroCapture.UI.ViewModels.ThumbnailItem item
                && DataContext is MicroCapture.UI.ViewModels.MainWindowViewModel vm)
            {
                vm.ToggleThumbnailSelection(item);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] OnThumbnailSelectClick failed: {ex}");
        }
    }

    /// <summary>Turns a vertical wheel into horizontal movement over the filmstrip.
    ///
    /// <para>Avalonia's ScrollViewer maps the wheel to vertical scrolling, and the filmstrip
    /// scrolls horizontally only — so the wheel did nothing over it and the strip appeared stuck
    /// at whichever pages were on screen. A mouse without a horizontal wheel had no way to reach
    /// the rest of the batch except by dragging the scrollbar.</para></summary>
    /// <summary>Delete acts on the current context: a selected frame first, then the page being
    /// browsed, then a cart selection. Deleting a page asks first — unlike removing a frame, it
    /// cannot be undone.</summary>
    private async Task HandleDeleteKeyAsync(MainWindowViewModel vm)
    {
        if (vm.SelectedFrameIndex >= 0)
        {
            vm.HandleKeyShortcut("Delete");
            return;
        }

        var browsing = vm.CurrentBrowsePageItem;
        if (browsing != null)
        {
            var confirmed = await ConfirmDialog.AskAsync(this,
                $"Delete page {browsing.PageNumber} from this batch?\n\n" +
                "It will be removed from the cart and from any export. This can't be undone.",
                "Delete page");
            if (confirmed) await vm.DeleteCaptureAsync(browsing);
            return;
        }

        if (vm.HasSelection)
        {
            vm.DeleteSelectedCommand.Execute(this);
            return;
        }

        vm.StatusText = "Nothing to delete — select a frame, browse to a page with the arrow keys, or select pages in the cart.";
    }

    /// <summary>Brings the browsed page into view, centred so the pages either side stay visible —
    /// which is the point of browsing rather than jumping. Deferred so the scroller sees the
    /// layout the change produced rather than the one before it.</summary>
    private void ScrollBrowsedPageIntoView(MainWindowViewModel vm)
    {
        var index = vm.BrowseRequestedIndex;
        if (index < 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var scroller = this.FindControl<ScrollViewer>("FilmstripScroller");
            if (scroller == null) return;
            const double tileStride = 118; // tile plus its insert-point button
            var target = index * tileStride - (scroller.Viewport.Width - tileStride) / 2;
            var maximum = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
            scroller.Offset = scroller.Offset.WithX(Math.Clamp(target, 0, maximum));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnFilmstripWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;

        // Either axis moves the strip, so a trackpad's horizontal gesture works too.
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;

        const double step = 90; // a little under one tile, so a notch moves without overshooting
        var target = scroller.Offset.X - delta * step;
        var maximum = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
        scroller.Offset = scroller.Offset.WithX(Math.Clamp(target, 0, maximum));
        e.Handled = true;
    }

    private void OnInsertPointClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ThumbnailItem item }
            && DataContext is MainWindowViewModel vm)
        {
            vm.SetInsertPointCommand.Execute(item.PageNumber);
        }
    }

    private async void OnDeleteThumbnailClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.DataContext is MicroCapture.UI.ViewModels.ThumbnailItem item
                && DataContext is MicroCapture.UI.ViewModels.MainWindowViewModel vm)
            {
                // Deleting a page removes it from the batch and from every export. It sits one
                // click away on a small target next to the thumbnail, so it has to ask first —
                // and unlike an adjustment, this one cannot be undone.
                var confirmed = await ConfirmDialog.AskAsync(this,
                    $"Delete page {item.PageNumber} from this batch?\n\n" +
                    "It will be removed from the cart and from any export. This can't be undone.",
                    "Delete page");
                if (!confirmed) return;

                await vm.DeleteCaptureAsync(item);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] OnDeleteThumbnailClick failed: {ex}");
        }
    }

    private void OnClearSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MicroCapture.UI.ViewModels.MainWindowViewModel vm)
            vm.ClearSelection();
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
                case Key.Left:
                    vm.BrowsePages(-1);
                    ScrollBrowsedPageIntoView(vm);
                    e.Handled = true;
                    break;
                case Key.Right:
                    vm.BrowsePages(1);
                    ScrollBrowsedPageIntoView(vm);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    // Only claimed while browsing; otherwise Enter belongs to whatever has focus.
                    if (vm.CurrentBrowsePageItem != null)
                    {
                        vm.OpenCurrentBrowsePage();
                        e.Handled = true;
                    }
                    break;
                case Key.Delete:
                case Key.Back:
                    // Acts on whatever the operator is actually working with. It used to remove a
                    // fixed frame unconditionally, so pressing it while browsing pages either did
                    // nothing (no frame selected) or removed a frame they weren't looking at.
                    _ = HandleDeleteKeyAsync(vm);
                    e.Handled = true;
                    break;
            }
        }
    }
}
