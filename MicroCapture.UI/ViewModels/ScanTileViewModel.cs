using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MicroCapture.UI.ViewModels;

/// <summary>The "current scan" tile in the filmstrip — a marker for where the next capture will
/// land, not a captured page. It is deliberately NOT a <see cref="ThumbnailItem"/> and never
/// enters <c>RecentCaptures</c>: keeping it out of that collection means every selection,
/// renumber, delete, browse and trim path that iterates <c>RecentCaptures</c> ignores it for
/// free, with no null/type guards sprinkled through them.
///
/// <para>All state is pushed in by <c>MainWindowViewModel.RefreshScanTile()</c>, which is the
/// single owner of the tile's truth. This class holds no logic of its own beyond deriving the
/// display label.</para></summary>
public partial class ScanTileViewModel : ObservableObject
{
    /// <summary>Page number the next capture will take. With no insert point this is
    /// <c>PageCount + 1</c>; with one it is the chosen slot.</summary>
    [ObservableProperty] private int _targetPageNumber = 1;

    /// <summary>True when an insert point is set AND it falls within the batch — the tile then
    /// renders inline between two page tiles rather than at the trailing end.</summary>
    [ObservableProperty] private bool _isInline;

    /// <summary>Pages produced by one shutter press: 1 normally, <c>Frames.Count</c> in
    /// fixed-frame mode. Drives the "Next: N pages (frames)" badge.</summary>
    [ObservableProperty] private int _pagesPerShot = 1;

    /// <summary>Live-view bitmap to preview. Bound straight to
    /// <c>MainWindowViewModel.LiveViewImage</c>; this view model never disposes it — the main
    /// live-view panel owns that bitmap's lifetime.</summary>
    [ObservableProperty] private Bitmap? _livePreview;

    /// <summary>False when the camera feed is not actually streaming (during/after a capture,
    /// around setting changes, when disconnected). The tile shows a static placeholder then
    /// rather than a stale or black frame.</summary>
    [ObservableProperty] private bool _isLiveActive;

    /// <summary>Whether the tile should be in the strip at all — false when no batch is open or
    /// the open batch can no longer be captured into (e.g. already exported).</summary>
    [ObservableProperty] private bool _isVisible;

    /// <summary>Mirrors <c>MainWindowViewModel.CaptureReadiness</c> so the tile's border can
    /// reflect NOT READY / READY / waiting without the operator's eye leaving the strip.</summary>
    [ObservableProperty] private string _readiness = "NOT READY";

    /// <summary>True from the moment a capture starts until its placeholder row is inserted —
    /// drives a brief "capturing…" pulse so the operator sees the page land.</summary>
    [ObservableProperty] private bool _isCapturing;

    public string Label => PagesPerShot > 1
        ? $"Next: {PagesPerShot} pages (frames)"
        : $"Next: Page {TargetPageNumber}";

    partial void OnPagesPerShotChanged(int value) => OnPropertyChanged(nameof(Label));
    partial void OnTargetPageNumberChanged(int value) => OnPropertyChanged(nameof(Label));
}
