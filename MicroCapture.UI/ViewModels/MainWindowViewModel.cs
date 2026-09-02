using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Interfaces;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.UI.Theming;
using MicroCapture.UI.Views;

namespace MicroCapture.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICameraService _cameraService;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly BatchManifestService _manifests;
    private readonly BatchLockService _batchLocks;
    private readonly BatchSyncService _batchSync;
    /// <summary>Folder of the batch currently open, or null for a legacy batch that hasn't been
    /// given one yet. This is what the manifest is read from and published back to.</summary>
    private string? _currentBatchFolder;
    private readonly AppPreferences _preferences = AppPreferences.Load();

    // ───────────── Day / night mode ─────────────
    // One control that cycles Dark → Light → System rather than three, because this is a
    // preference an operator sets once and then forgets, not a mode they switch between while
    // working. The label states which one is in force so the cycle is never a guess.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeModeLabel))]
    [NotifyPropertyChangedFor(nameof(ThemeModeTooltip))]
    private ThemeMode _themeMode = AppTheme.Current;

    /// <summary>Sun for the light palette, moon for the dark one, half-moon for "whatever the OS
    /// says". The glyph shows the mode that is on, not the one a click would bring.</summary>
    public string ThemeModeLabel => ThemeMode switch
    {
        ThemeMode.Light => "\u2600",
        ThemeMode.Dark => "\u263e",
        _ => "\u25d1",
    };

    public string ThemeModeTooltip => ThemeMode switch
    {
        ThemeMode.Light => "Day mode. Click for system mode.",
        ThemeMode.Dark => "Night mode. Click for day mode.",
        _ => "Following the system setting. Click for night mode.",
    };

    [RelayCommand]
    private void CycleTheme()
    {
        ThemeMode = AppTheme.Next(ThemeMode);
        AppTheme.ApplyAndSave(ThemeMode, _preferences);
    }

    // ───────────── Live view orientation ─────────────
    // Degrees clockwise, 0/90/180/270. Applied to the live view for the operator and stamped onto
    // each capture as it is queued, so what is on screen is what lands in the batch.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveViewRotated))]
    [NotifyPropertyChangedFor(nameof(LiveViewRotationLabel))]
    [NotifyPropertyChangedFor(nameof(LiveViewRotationRadians))]
    private int _liveViewRotation;

    public bool IsLiveViewRotated => LiveViewRotation != 0;

    // Shown at every angle, 0° included, so the orientation is never a silent mode — the operator
    // can always read which way is up, and the "0°" state is explicit rather than an absence.
    public string LiveViewRotationLabel => $"{LiveViewRotation}°";

    /// <summary>Rotation in radians, for the overlay's counter-rotation of on-frame text.</summary>
    public double LiveViewRotationRadians => LiveViewRotation * Math.PI / 180.0;

    [RelayCommand]
    private void RotateLiveViewLeft() => SetLiveViewRotation(LiveViewRotation - 90);

    [RelayCommand]
    private void RotateLiveViewRight() => SetLiveViewRotation(LiveViewRotation + 90);

    private void SetLiveViewRotation(int degrees)
    {
        LiveViewRotation = ((degrees % 360) + 360) % 360;

        // Remembered per machine, not per batch: it describes how the camera sits on this rig, so
        // it should still be right after a restart mid-book. A batch that spans a restart would
        // otherwise change orientation partway through — the one thing "only applies to upcoming
        // captures" must not turn into.
        _preferences.LiveViewRotation = LiveViewRotation;
        _preferences.Save();
    }

    // Refreshes this machine's claim on the open batch. Without it the lock ages past
    // BatchLockService.StaleAfter while the batch is still being worked, so a second workstation
    // opening the same folder sees an abandoned lock and is never warned — which is the one
    // situation the lock exists for.
    private Avalonia.Threading.DispatcherTimer? _batchLockHeartbeat;

    private void StartBatchLockHeartbeat()
    {
        _batchLockHeartbeat ??= new Avalonia.Threading.DispatcherTimer
        {
            Interval = BatchLockService.HeartbeatInterval
        };
        _batchLockHeartbeat.Tick -= OnBatchLockHeartbeat;
        _batchLockHeartbeat.Tick += OnBatchLockHeartbeat;
        _batchLockHeartbeat.Start();
    }

    private void OnBatchLockHeartbeat(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentBatchFolder)) _batchLocks.Heartbeat(_currentBatchFolder!);
    }

    /// <summary>Gives up this machine's claim on whichever batch is open, before opening another
    /// or on shutdown. Leaving it held is what litters every batch folder with a stale lock.</summary>
    private void ReleaseCurrentBatchLock()
    {
        if (string.IsNullOrWhiteSpace(_currentBatchFolder)) return;
        _batchLocks.Release(_currentBatchFolder!);
    }

    private string? LastBatchLocation
    {
        get => _preferences.LastBatchLocation;
        set { _preferences.LastBatchLocation = value; _preferences.Save(); }
    }

    private IReadOnlyList<string> RecentBatchFolders => _preferences.RecentBatchFolders;

    // The toolbar no longer offers batch settings to change, so it shows what the open batch IS
    // instead — otherwise, with the codes and dropdowns gone, nothing on screen would say which
    // batch is being captured into or how.
    [ObservableProperty] private bool _hasOpenBatch;
    [ObservableProperty] private string _openBatchLabel = string.Empty;
    [ObservableProperty] private string _openBatchSettingsLabel = string.Empty;

    partial void OnHasOpenBatchChanged(bool value) => RefreshScanTile();

    private void UpdateOpenBatchLabels(Batch? batch)
    {
        if (batch == null)
        {
            HasOpenBatch = false;
            OpenBatchLabel = string.Empty;
            OpenBatchSettingsLabel = string.Empty;
            return;
        }

        HasOpenBatch = true;
        OpenBatchLabel = string.IsNullOrWhiteSpace(_activeProjectCode)
            ? batch.BatchCode
            : $"{_activeProjectCode} / {batch.BatchCode}";

        var options = new List<string> { $"{batch.Dpi} DPI", SelectedCaptureFormat };
        if (batch.DewarpEnabled) options.Add("book curve");
        if (batch.SplitBookPages) options.Add("split pages");
        if (batch.BinarizeEnabled) options.Add("B&W");
        if (batch.BleedthroughEnabled) options.Add("bleedthrough");
        OpenBatchSettingsLabel = string.Join(" · ", options);
    }

    private IEnumerable<string> BatchSearchRoots() => _preferences.EffectiveSearchRoots();

    /// <summary>Where this batch's captures are written: the batch folder's own temp/, once it
    /// has one. The full-resolution original is working material — Crop Review re-crops from it
    /// and it is deleted once the batch is exported — so temp/ is exactly what it is, and the
    /// folder ends up empty after finalizing. The processed derivative goes to the sibling
    /// output/ instead; see ProcessedFilePaths.OutputDirectoryFor, which owns that split.
    ///
    /// <para>Falls back to the project's flat output directory for a batch predating batch
    /// folders, which behaves exactly as before.</para></summary>
    private string CaptureDirectory => string.IsNullOrWhiteSpace(_currentBatchFolder)
        ? _outputDirectory
        : BatchFolder.TempPath(_currentBatchFolder);

    /// <summary>Where a page's cart thumbnail lives — the batch folder's own thumbnails/ once it
    /// has one, so thumbnails travel with the batch and the cart renders on another machine
    /// without re-deriving anything from images that may not be present.</summary>
    private string ThumbnailFileFor(string batchCode, int pageNumber) =>
        string.IsNullOrWhiteSpace(_currentBatchFolder)
            ? MicroCapture.Processing.ThumbnailPaths.FileFor(_outputDirectory, batchCode, pageNumber)
            : Path.Combine(BatchFolder.ThumbnailsPath(_currentBatchFolder), $"{pageNumber:D6}.png");

    private string ThumbnailDirectoryFor(string batchCode) =>
        string.IsNullOrWhiteSpace(_currentBatchFolder)
            ? MicroCapture.Processing.ThumbnailPaths.DirectoryFor(_outputDirectory, batchCode)
            : BatchFolder.ThumbnailsPath(_currentBatchFolder);

    // ---------- Page browsing ----------

    /// <summary>Moves the browse cursor through the cart. The shortcuts panel has advertised
    /// "Browse pages ← →" since before there was any handler for the arrow keys, so this did
    /// nothing at all.</summary>
    public void BrowsePages(int delta)
    {
        if (RecentCaptures.Count == 0)
        {
            StatusText = "No pages to browse yet.";
            return;
        }

        var current = RecentCaptures.ToList().FindIndex(t => t.IsCurrent);
        // Starting fresh from an arrow press lands on the first or last page depending on
        // direction, rather than jumping to whichever end the list happens to begin at.
        var next = current < 0
            ? (delta > 0 ? 0 : RecentCaptures.Count - 1)
            : Math.Clamp(current + delta, 0, RecentCaptures.Count - 1);

        for (var i = 0; i < RecentCaptures.Count; i++) RecentCaptures[i].IsCurrent = i == next;

        CurrentBrowsePage = RecentCaptures[next].PageNumber;
        BrowseRequestedIndex = next;
        BrowseScrollRequested?.Invoke(this, next);
        StatusText = $"Page {RecentCaptures[next].PageNumber} of {RecentCaptures.Count} — Enter to adjust it, Delete to remove it.";
    }

    /// <summary>Index the view should scroll into sight. Raised rather than bound so the view can
    /// scroll after layout has caught up with the change.</summary>
    public event EventHandler<int>? BrowseScrollRequested;
    public int BrowseRequestedIndex { get; private set; } = -1;
    [ObservableProperty] private int _currentBrowsePage;

    /// <summary>Opens whichever page the browse cursor is on.</summary>
    public void OpenCurrentBrowsePage()
    {
        var current = RecentCaptures.FirstOrDefault(t => t.IsCurrent);
        if (current == null) { StatusText = "Use the arrow keys to pick a page first."; return; }
        OpenCropReview(current.JobId, selectionForBulkApply: null);
    }

    /// <summary>The page the Delete key should act on: the one being browsed, or nothing.</summary>
    public ThumbnailItem? CurrentBrowsePageItem => RecentCaptures.FirstOrDefault(t => t.IsCurrent);

    // ---------- Insert point ----------

    /// <summary>Where the next capture lands, when the operator has chosen a spot in the cart
    /// rather than the end. Null means the normal behaviour of appending.</summary>
    private int? _insertBeforePage;

    [ObservableProperty] private bool _hasInsertPoint;
    [ObservableProperty] private string _insertPointLabel = string.Empty;

    private void UpdateInsertPointLabel()
    {
        HasInsertPoint = _insertBeforePage.HasValue;
        InsertPointLabel = _insertBeforePage is { } page
            ? $"Next capture will be inserted as page {page}. Later pages shift down to make room."
            : string.Empty;
        UpdateCaptureReadiness();
        RefreshScanTile();
    }

    /// <summary>Sets the cart position the next capture will be inserted at.</summary>
    [RelayCommand]
    private void SetInsertPoint(object? pageNumber)
    {
        // Moving the insert point mid-capture would leave the in-flight page numbered against
        // the old target while the tile jumps to the new one. The capture itself is already
        // guarded; block the control that feeds it too.
        if (Volatile.Read(ref _captureInProgress) != 0)
        {
            StatusText = "Wait for the current capture to finish before moving the insert point.";
            return;
        }

        var page = pageNumber switch
        {
            int i => i,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => -1
        };
        if (page < 1) return;

        // insertAt == PageCount + 1 is just "append" — collapse it to no insert point rather
        // than carrying a redundant inline state the tile would have to special-case.
        _insertBeforePage = page > PageCount ? (int?)null : page;
        UpdateInsertPointLabel();
        ScanTileMoved?.Invoke(this, EventArgs.Empty);

        if (_insertBeforePage == null)
            StatusText = "Capturing will add pages at the end again.";
        else if (PageCount > RecentCaptures.Count)
            // A batch longer than the cart window: pages before the visible range still exist,
            // they just aren't drawn, so the tile sits among the visible pages by number.
            StatusText = $"Capturing will insert at page {page}. Pages before the visible range of the cart aren't shown.";
        else
            StatusText = $"Capturing will insert at page {page}. Press Insert point off to go back to adding at the end.";
    }

    [RelayCommand]
    private void ClearInsertPoint()
    {
        if (Volatile.Read(ref _captureInProgress) != 0)
        {
            StatusText = "Wait for the current capture to finish before moving the insert point.";
            return;
        }
        if (_insertBeforePage == null) return;
        _insertBeforePage = null;
        UpdateInsertPointLabel();
        ScanTileMoved?.Invoke(this, EventArgs.Empty);
        StatusText = "Capturing will add pages at the end again.";
    }

    // ---------- Current-scan tile ----------

    /// <summary>The filmstrip's "current scan" marker — where the next capture will land. Its
    /// whole state is recomputed by <see cref="RefreshScanTile"/>; nothing else writes to it.</summary>
    public ScanTileViewModel ScanTile { get; } = new();

    /// <summary>Pages that render BEFORE the scan tile in the strip — everything when the tile is
    /// trailing, or pages numbered below the insert point when it is inline. Split out into two
    /// bound collections because the tile sits between them as its own control rather than being
    /// an item in one list.</summary>
    public ObservableCollection<ThumbnailItem> PagesBeforeScanTile { get; } = new();

    /// <summary>Pages that render AFTER the scan tile — empty when it is trailing.</summary>
    public ObservableCollection<ThumbnailItem> PagesAfterScanTile { get; } = new();

    /// <summary>Raised when the scan tile's slot changes (insert point set/cleared, or a capture
    /// just landed) so the view can scroll it back into sight. Deliberately NOT raised by
    /// <see cref="RefreshScanTile"/>'s routine updates (live frame, readiness) — those must not
    /// yank the strip while the operator is looking elsewhere.</summary>
    public event EventHandler? ScanTileMoved;

    /// <summary>Status of the open batch, snapshotted at open time. The scan tile hides for a
    /// batch that can no longer be captured into (already exported).</summary>
    private string? _currentBatchStatus;

    /// <summary>Single owner of the scan tile's truth: recomputes visibility, target page,
    /// pages-per-shot, inline-vs-trailing, and the live/readiness mirror from current VM state,
    /// then repartitions the strip around it. Cheap; call after anything that changes an input
    /// (batch open/close, insert point, frame count, page count, readiness, live-view state,
    /// delete, reorder).</summary>
    private void RefreshScanTile()
    {
        var open = _currentBatchId != null && HasOpenBatch
                   && _currentBatchStatus is null or "Active" or "Draft";
        ScanTile.IsVisible = open;

        if (!open)
        {
            ScanTile.IsInline = false;
            ScanTile.PagesPerShot = 1;
            RebuildStripPartition();
            return;
        }

        // Clamp a stale insert point into range on every refresh. It can fall out of range
        // when pages it pointed past are deleted, when a reorder renumbers the batch, or when
        // another machine inserted pages into the same batch folder. insertAt > PageCount
        // collapses to "append".
        if (_insertBeforePage is { } raw)
        {
            var clamped = Math.Clamp(raw, 1, PageCount + 1);
            _insertBeforePage = clamped > PageCount ? (int?)null : clamped;
            if (clamped != raw)
            {
                HasInsertPoint = _insertBeforePage.HasValue;
                InsertPointLabel = _insertBeforePage is { } p2
                    ? $"Next capture will be inserted as page {p2}. Later pages shift down to make room."
                    : string.Empty;
            }
        }

        ScanTile.PagesPerShot = Frames.Count > 0 ? Frames.Count : 1;
        ScanTile.IsInline = _insertBeforePage is { } p && p <= PageCount;
        ScanTile.TargetPageNumber = _insertBeforePage ?? (PageCount + 1);
        ScanTile.LivePreview = LiveViewImage;
        ScanTile.IsLiveActive = IsLiveViewActive;
        ScanTile.Readiness = CaptureReadiness;

        RebuildStripPartition();
    }

    /// <summary>Splits <see cref="RecentCaptures"/> into the before/after halves the view binds
    /// around the scan tile, and flags the first page after the tile so the template can
    /// suppress the redundant "+" that would set the insert point to where the tile already is.</summary>
    private void RebuildStripPartition()
    {
        var splitAt = ScanTile.IsInline ? ScanTile.TargetPageNumber : int.MaxValue;

        PagesBeforeScanTile.Clear();
        PagesAfterScanTile.Clear();
        foreach (var tile in RecentCaptures)
        {
            tile.IsFirstAfterScanTile = false;
            (tile.PageNumber < splitAt ? PagesBeforeScanTile : PagesAfterScanTile).Add(tile);
        }
        if (PagesAfterScanTile.Count > 0)
            PagesAfterScanTile[0].IsFirstAfterScanTile = true;
    }

    /// <summary>Makes room at <paramref name="insertAt"/> by moving that page and every later one
    /// down by <paramref name="count"/>.
    ///
    /// <para>Every job sharing a page number moves together, retired recapture attempts included —
    /// leaving one behind would attach it to whichever page later took its number. Thumbnails are
    /// renamed to match, highest first so a page never overwrites one that hasn't moved yet.</para></summary>
    private async Task ShiftPagesForInsertAsync(int insertAt, int count)
    {
        if (_currentBatchId == null || count <= 0) return;
        try
        {
            _dbContext.ChangeTracker.Clear();
            var toShift = await _dbContext.CaptureJobs
                .Where(j => j.BatchId == _currentBatchId && j.PageNumber >= insertAt)
                .ToListAsync();
            if (toShift.Count == 0) return;

            foreach (var job in toShift) job.PageNumber += count;
            await _dbContext.SaveChangesAsync();

            // Descending, so each rename targets a number nothing still occupies.
            foreach (var page in toShift.Select(j => j.PageNumber - count).Distinct().OrderByDescending(p => p))
            {
                var from = ThumbnailFileFor(_activeBatchCode, page);
                var to = ThumbnailFileFor(_activeBatchCode, page + count);
                try { if (File.Exists(from)) File.Move(from, to, overwrite: true); }
                catch (IOException) { /* A thumbnail is a cache; losing one costs a re-render. */ }
            }

            foreach (var thumbnail in RecentCaptures.Where(t => t.PageNumber >= insertAt).ToList())
                thumbnail.PageNumber += count;

            PageCount += count;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not make room for the inserted page: {ex.Message}";
            throw;
        }
    }

    /// <summary>Raised when a page is appended to the cart, so the view can scroll to it. The
    /// cart is in page order, so the newest capture is at the far end rather than in view.</summary>
    public event EventHandler? CartAppended;

    /// <summary>Moves a page to a new position in the cart and renumbers the batch to match.
    ///
    /// <para><see cref="CaptureJob.PageNumber"/> is the batch's page order, so reordering means
    /// reassigning it — there is no separate sort field to shuffle instead. That has two
    /// consequences this has to respect.</para>
    ///
    /// <para>First, page number is also recapture identity: a recaptured page keeps the same
    /// number on both the new job and the retired Superseded one. So every job sharing a page
    /// number has to move together, or a retired attempt is left pointing at whatever page later
    /// takes its old number and can resurface in an export.</para>
    ///
    /// <para>Second, page-numbered thumbnail files have to be renamed alongside, or the cart shows
    /// the wrong image for every page after a reorder. The rename runs via temporary names first,
    /// because renumbering swaps numbers that are still in use and a direct rename would collide.</para></summary>
    public async Task ReorderCaptureAsync(string draggedJobId, string targetJobId)
    {
        if (draggedJobId == targetJobId || _currentBatchId == null) return;

        var from = RecentCaptures.ToList().FindIndex(t => t.JobId == draggedJobId);
        var to = RecentCaptures.ToList().FindIndex(t => t.JobId == targetJobId);
        if (from < 0 || to < 0) return;

        RecentCaptures.Move(from, to);

        // A reorder renumbers the whole batch, so an insert point set against the old numbering
        // would now point at a different page. Clear it rather than silently retarget.
        var hadInsertPoint = _insertBeforePage != null;
        if (hadInsertPoint)
        {
            _insertBeforePage = null;
            UpdateInsertPointLabel();
        }

        try
        {
            await RenumberBatchSequentiallyAsync();
            await PublishManifestAsync();
            StatusText = hadInsertPoint
                ? $"Moved page to position {to + 1}. Insert point cleared — pages were renumbered."
                : $"Moved page to position {to + 1}";
        }
        catch (Exception ex)
        {
            // Put the cart back rather than leaving the display disagreeing with the database.
            RecentCaptures.Move(to, from);
            StatusText = $"Could not reorder pages: {ex.Message}";
        }
        finally
        {
            RefreshScanTile();
        }
    }

    /// <summary>Renumbers every page in the batch to a gap-free 1..N run that matches the cart's
    /// current visual order, renaming the page-numbered thumbnail files and recomputing
    /// <see cref="PageCount"/> to match. Shared by drag-reorder and by delete — both leave the
    /// cart in the order the batch should read and then need the stored <see cref="CaptureJob.PageNumber"/>
    /// (which is the only sort key there is) brought back in line with it.
    ///
    /// <para>Every job sharing a page number is moved together — a Superseded recapture attempt
    /// included — or a retired attempt would be left pointing at whatever page later takes its
    /// old number and could resurface in an export. Thumbnails are renamed via temporary names
    /// because the renumber permutes numbers that are all still in use.</para></summary>
    private async Task RenumberBatchSequentiallyAsync()
    {
        if (_currentBatchId == null) return;

        _dbContext.ChangeTracker.Clear();
        var jobs = await _dbContext.CaptureJobs
            .Where(j => j.BatchId == _currentBatchId)
            .ToListAsync();
        if (jobs.Count == 0)
        {
            PageCount = 0;
            return;
        }

        // Old page number -> every job on that page, retired attempts included.
        var byOldPage = jobs.GroupBy(j => j.PageNumber).ToDictionary(g => g.Key, g => g.ToList());

        // The cart shows at most the last MaxCartLoad pages, so renumbering only what it holds
        // would leave every page outside it on its old number — two jobs per number, colliding
        // filenames, and the next capture overwriting an existing page. Build the full batch
        // order instead: pages before the visible window keep their relative order, then the
        // cart's order as it currently stands.
        var visiblePages = RecentCaptures.Select(t => t.PageNumber).ToHashSet();
        var hiddenPagesInOrder = byOldPage.Keys.Where(p => !visiblePages.Contains(p)).OrderBy(p => p).ToList();
        var fullOrder = hiddenPagesInOrder.Concat(RecentCaptures.Select(t => t.PageNumber)).ToList();

        var renames = new List<(string From, string To)>();
        var newPageByOld = new Dictionary<int, int>();
        var newPage = 1;
        foreach (var oldPage in fullOrder)
        {
            if (!byOldPage.ContainsKey(oldPage) || newPageByOld.ContainsKey(oldPage)) continue;
            newPageByOld[oldPage] = newPage;

            if (oldPage != newPage)
            {
                var oldThumb = ThumbnailFileFor(_activeBatchCode, oldPage);
                var newThumb = ThumbnailFileFor(_activeBatchCode, newPage);
                if (File.Exists(oldThumb)) renames.Add((oldThumb, newThumb));
            }
            newPage++;
        }

        // Applied after the mapping is complete, so a page never reads a number another page has
        // already overwritten.
        foreach (var (oldPage, pageJobs) in byOldPage)
        {
            if (!newPageByOld.TryGetValue(oldPage, out var assigned)) continue;
            foreach (var job in pageJobs) job.PageNumber = assigned;
        }
        foreach (var thumbnail in RecentCaptures)
        {
            if (newPageByOld.TryGetValue(thumbnail.PageNumber, out var assigned))
                thumbnail.PageNumber = assigned;
        }

        await _dbContext.SaveChangesAsync();
        MoveThumbnailFiles(renames);

        // Across the whole batch, not just the cart — otherwise a batch longer than the cart
        // window reports a page count lower than pages that actually exist, and the next capture
        // reuses a live page number.
        PageCount = newPageByOld.Count == 0 ? 0 : newPageByOld.Values.Max();
    }

    /// <summary>Applies thumbnail renames via temporary names. A reorder permutes numbers that are
    /// all still in use, so renaming straight to the target would overwrite a file another page
    /// still needs.</summary>
    private static void MoveThumbnailFiles(List<(string From, string To)> renames)
    {
        if (renames.Count == 0) return;
        var staged = new List<(string Temp, string To)>();
        foreach (var (from, to) in renames)
        {
            var temp = from + ".reorder";
            try { File.Move(from, temp, overwrite: true); staged.Add((temp, to)); }
            catch (IOException) { /* A thumbnail is a cache; losing one costs a re-render, not data. */ }
        }
        foreach (var (temp, to) in staged)
        {
            try { File.Move(temp, to, overwrite: true); }
            catch (IOException) { }
        }
    }

    /// <summary>Writes the current batch state back to its manifest. Called after anything the
    /// manifest describes changes, so the folder — not this machine's database — stays the copy
    /// that can be trusted and reopened anywhere.</summary>
    private async Task PublishManifestAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentBatchFolder) || _currentBatchId == null) return;
        try
        {
            var batch = await _dbContext.Batches.FirstOrDefaultAsync(b => b.Id == _currentBatchId);
            if (batch != null) await _batchSync.PublishAsync(batch);
        }
        catch (Exception ex)
        {
            // The database still holds everything; only portability is affected, so this must
            // never interrupt capture.
            Console.Error.WriteLine($"Could not update the batch manifest: {ex}");
        }
    }

    private void RememberRecentBatchFolder(string folder)
    {
        _preferences.RememberBatchFolder(folder);
        _preferences.Save();
    }
    private readonly MicroCapture.Processing.BackgroundProcessingWorker? _worker;

    // --- State ---
    [ObservableProperty] private string _statusText = "Ready — Connect camera to begin";
    [ObservableProperty] private Bitmap? _liveViewImage;
    [ObservableProperty] private bool _isConnected;
    // Mirrors ICameraService.IsLiveViewActive. The focus controls bind to this rather than
    // IsConnected because the camera rejects EVF focus commands whenever live view isn't actually
    // streaming — which happens for real windows during/after a capture and around setting changes.
    [ObservableProperty] private bool _isLiveViewActive;
    [ObservableProperty] private bool _isAutoCapture;
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private string _projectCode = "";
    [ObservableProperty] private string _batchCode = "";
    [ObservableProperty] private string _cameraModel = "Not connected";
    [ObservableProperty] private string _connectionStatus = "DISCONNECTED";
    [ObservableProperty] private string _focusStatus = "—";
    [ObservableProperty] private string _exposureStatus = "—";
    [ObservableProperty] private string _documentStatus = "—";
    [ObservableProperty] private string _captureReadiness = "NOT READY";
    [ObservableProperty] private bool _splitBookPages = false;

    // Fixed-frame capture. Frames are drawn directly on the live view and edited at any time —
    // there is no separate "use fixed frames" intent flag and no modal calibration step. The
    // collection itself IS the mode: zero frames means ordinary auto-detect capture, one or more
    // means crop to exactly those regions. Batch.UseFixedFrames survives only as a derived
    // persistence detail (Frames.Count > 0) because the background worker reads it.
    //
    // Index order is page order: it drives output filenames (_frameNN) and each thumbnail's
    // FrameIndex, so frames are never auto-sorted — the operator reorders them explicitly.
    public ObservableCollection<MicroCapture.Processing.FixedFrameRect> Frames { get; } = new();

    /// <summary>Pixel space <see cref="Frames"/> is expressed in. Frames drawn here are authored
    /// against the live feed, so this is the feed's own size; a batch calibrated before live-view
    /// editing existed keeps its original full-resolution reference instead, so editing such a
    /// batch doesn't make its frames jump. Persisted as Batch.FixedFrameImageWidth/Height and
    /// honored by ImageProcessor.ProcessFixedFrames when it projects frames onto a capture.</summary>
    public int FrameReferenceWidth { get; private set; }
    public int FrameReferenceHeight { get; private set; }

    [ObservableProperty] private int _selectedFrameIndex = -1;

    /// <summary>False while captures are still processing under the current geometry — editing is
    /// blocked up front rather than rejected after the operator has already dragged something.</summary>
    [ObservableProperty] private bool _areFrameEditsAllowed = true;

    /// <summary>True from pointer-down until shortly after pointer-up, so auto-capture can't fire
    /// while the geometry the shot would use is still moving under the operator's hand.</summary>
    [ObservableProperty] private bool _isEditingFrames;

    [ObservableProperty] private bool _isCalibrating;
    [ObservableProperty] private LensCalibrationViewModel? _lensCalibrationViewModel;

    /// <summary>True when the live camera feed's own panel should be shown — false while any
    /// of the sibling panels that share its grid cell (calibration, Crop Review) are active.
    /// The live view keeps running underneath regardless (see ActiveCropReview's own remarks);
    /// this only controls which panel is visually on top.</summary>
    public bool IsShowingLiveView => !IsCalibrating && ActiveCropReview == null;

    partial void OnIsCalibratingChanged(bool value) => OnPropertyChanged(nameof(IsShowingLiveView));
    partial void OnActiveCropReviewChanged(CropReviewViewModel? value) => OnPropertyChanged(nameof(IsShowingLiveView));
    // Run OCR and Finalize's own export step must never overlap — both touch the same jobs'
    // OCR/export status, and a searchable-PDF finalize runs OCR itself first if it isn't
    // already done. IsExporting is set by the Finalize dialog around its own export call.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunOcrCommand))]
    private bool _isOcrRunning;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunOcrCommand))]
    private bool _isExporting;
    public string[] AvailableFormats { get; } = { "PDF", "TIFF", "JPG", "PNG" };

    // DPI is stamped onto each capture at the moment it's taken (see CaptureAsync/RecaptureAsync
    // passing SelectedDpi into EnqueueCaptureAsync) — changing this dropdown mid-batch affects
    // only captures taken afterward, not pages already shot. 150 (the smallest option) is the
    // baseline — the camera has no fixed native optical DPI, so pixel dimensions are left
    // untouched there, and every higher selection upsamples proportionally (never downsamples
    // away real captured detail). See ImageProcessor.BaselineDpi/ResizeForDpi.
    [ObservableProperty] private int _selectedDpi = 150;
    // 150 is the rig's native captured size (see Batch.Dpi / ImageProcessor.BaselineDpi), so the
    // values below it downsample and those above upsample.
    public int[] AvailableDpiOptions { get; } = { 50, 100, 150, 200, 300, 400, 600, 800, 1000, 1200 };

    // Output file format is sticky PER CAPTURE, not per batch like DPI/dewarp/binarize/
    // bleedthrough above — read directly at capture-enqueue time (CaptureAsync/RecaptureAsync)
    // and stamped onto that job's own CaptureFormat, so it can change capture-to-capture within
    // the same batch without any Batch-row persistence or OnXChanged hook. Hydrated from the
    // most recently captured job's own CaptureFormat on startup (see the constructor) so the
    // dropdown remembers the last-used format across app restarts, the same "sticky" behavior
    // DPI/format selections elsewhere in the app already have via their own Batch persistence.
    [ObservableProperty] private string _selectedCaptureFormat = "TIFF";
    partial void OnSelectedCaptureFormatChanged(string value)
    {
        // Capture format persists the current choice but does not retroactively change
        // already-processed jobs in the batch (they keep their original format).
        // This is just a "remember my last choice" convenience.
    }
    // "TIFF" writes an uncompressed archival master; "TIFF LZW" is the same file type with
    // lossless compression. NormalizeCaptureFormat keeps them distinct so the writer honors
    // the choice, so both must appear here or a batch created as "TIFF LZW" would show a blank
    // dropdown on reopen.
    public string[] AvailableCaptureFormats { get; } = { "TIFF", "TIFF LZW", "JPG", "PNG", "JP2", "BMP" };

    // Book curve correction is fixed per batch, like split/fixed-frames/DPI — processing runs
    // in the background queue, off the capture path, so toggling this never affects shutter
    // responsiveness. See ImageProcessor.DetectDewarpCurve/ApplyDewarp.
    [ObservableProperty] private bool _dewarpEnabled = false;

    // Converts processed pages to pure black-and-white (Sauvola local threshold, written as a
    // genuine 1-bit/CCITT-G4 TIFF) — smaller files and crisper OCR input, at the cost of any
    // color/grayscale content. See ImageProcessor.ApplySauvolaBinarization/WriteBitonalTiff.
    [ObservableProperty] private bool _binarizeEnabled = false;

    // Suppresses show-through from the reverse side of a thin page bleeding into the scan.
    // Confirmed not effective on colored-image bleedthrough (grayscale/text show-through
    // only) — opt-in per batch. See ImageProcessor.TryRemoveBleedthrough.
    [ObservableProperty] private bool _bleedthroughEnabled = false;

    /// <summary>Immediately persists one field of the active batch's settings row so a toggle
    /// changed mid-batch (DPI/dewarp/binarize/bleedthrough/split) takes effect for every capture
    /// still to come, without requiring an app restart or a re-opened batch. A no-op before any
    /// batch is started, and suppressed while StartBatchAsync's resume branch is hydrating these
    /// same observable properties FROM a loaded batch (see _suppressPersist) so that doesn't
    /// read back as an operator-initiated change. Failures are reported via StatusText rather
    /// than thrown — a setting that fails to persist should never crash the capture session;
    /// the operator can see the message and retry the toggle.</summary>
    private async void PersistBatchSettingAsync(Action<Batch> apply)
    {
        if (_currentBatchId == null || _suppressPersist) return;
        try
        {
            var batch = await _dbContext.Batches.FindAsync(_currentBatchId);
            if (batch == null) return;
            apply(batch);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save setting: {ex.Message}";
        }
    }

    partial void OnSelectedDpiChanged(int value) => PersistBatchSettingAsync(b => b.Dpi = value);
    partial void OnDewarpEnabledChanged(bool value) => PersistBatchSettingAsync(b => b.DewarpEnabled = value);
    partial void OnBinarizeEnabledChanged(bool value) => PersistBatchSettingAsync(b => b.BinarizeEnabled = value);
    partial void OnBleedthroughEnabledChanged(bool value) => PersistBatchSettingAsync(b => b.BleedthroughEnabled = value);

    /// <summary>Zero frames means ordinary auto-detect capture; one or more means crop to exactly
    /// those regions. Drawing the first frame is what enters fixed-frame mode — there is no
    /// separate toggle to keep in sync.</summary>
    public bool IsFrameMode => Frames.Count > 0;

    /// <summary>Frame mode and book splitting both turn one shutter press into several output
    /// files by different rules, so they stay mutually exclusive. Ticking Split clears any drawn
    /// frames rather than silently winning over them.</summary>
    partial void OnSplitBookPagesChanged(bool value)
    {
        if (value && Frames.Count > 0) ClearAllFrames();
        PersistBatchSettingAsync(b => b.SplitBookPages = value);
    }

    private string? _currentProjectId;
    private string? _currentBatchId;
    // Set true only while StartBatchAsync's resume branch is hydrating the observable DPI/
    // dewarp/binarize/bleedthrough properties FROM an already-saved Batch row — without this,
    // those assignments would round-trip straight back into PersistBatchSettingAsync as if the
    // operator had just changed them, which is at best a redundant write and at worst racy given
    // _currentBatchId's own assignment timing during that same resume.
    private bool _suppressPersist;
    // Snapshotted at StartBatchAsync, sanitized so operator-entered text can never
    // escape the intended output directory or produce an invalid filename.
    private string _activeProjectCode = string.Empty;
    private string _activeBatchCode = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _connectedCameraModel = "Not connected";
    private int _liveViewFramePending;
    private int _captureInProgress;
    private DateTime _lastDocumentCheckUtc = DateTime.MinValue;

    // Auto-capture state machine: fires the shutter automatically once a page has been
    // stable, in-focus, and different from whatever was last captured for
    // StableFramesRequired consecutive checks. See UpdateDocumentStatus.
    // How many thumbnails the cart holds. The load window is larger than the live trim so
    // reopening a batch shows more history than a long capture session accumulates; both are
    // named here because a reorder has to know the cart may not cover the whole batch.
    private const int MaxCartThumbnails = 200;
    private const int MaxCartLoad = 200;

    private const int StableFramesRequired = 3; // ~1.5s at the existing 500ms check interval
    private const double LiveSharpnessThreshold = 40.0; // live-view frames are lower detail than a full capture, so a lower bar than the QC BlurThreshold (100)
    private const double StablePositionToleranceFraction = 0.03; // allowed drift between checks, as a fraction of frame size
    private const double ContentChangeThreshold = 18.0; // mean abs pixel difference (0-255) considered a genuinely different page

    // "Has the page stopped moving" and "is this a different page" are different questions and
    // need very different bars. They shared ContentChangeThreshold, which is deliberately large
    // so an ordinary page turn counts as new content — far too loose for detecting motion, since
    // two frames half a second apart mid-turn differ by well under 18. That is what let
    // auto-capture fire while the page was still being turned.
    private const double MotionThreshold = 4.0;

    // A capture requires a page turn to have been seen since the last one. Stillness alone is not
    // evidence of a new page — a page that never moved is also perfectly still.
    private const double PageTurnMotionThreshold = 10.0;
    private const double PositionSmoothingFactor = 0.35; // weight toward each new detection when updating the smoothed reference
    private int _stableFrameCount;
    // A smoothed (not raw) reference position: comparing each new detection against this
    // instead of the previous raw frame absorbs small per-frame jitter (hand tremor, minor
    // auto-exposure/focus hunting) without resetting stability progress, while a genuine
    // page swap still diverges from it quickly and resets normally.
    private (double X, double Y, double Width, double Height)? _smoothedRect;
    private byte[]? _lastDetectedSignature;
    private byte[]? _lastCapturedSignature;
    // The signature equivalent of _smoothedRect. Position had jitter absorption; content did not,
    // so on a blank or near-uniform page ordinary sensor noise between two consecutive checks
    // could exceed ContentChangeThreshold and keep resetting the settle counter — auto-capture
    // would then never fire no matter how still the page was. Kept as doubles because an EMA of
    // byte samples needs sub-integer precision to converge.
    // The previous frame's raw signature. Motion is the difference between consecutive frames;
    // smoothing this would let the reference chase a moving page and report it as still.
    private byte[]? _previousFrameSignature;

    /// <summary>Whether a page turn has been observed since the last capture. Auto-capture needs
    /// move-then-settle, not just settle, or it re-fires on a page that never moved — and worse,
    /// fires part-way through a turn that briefly looks stable.</summary>
    private bool _sawPageTurnSinceCapture;
    // Last gate that blocked an auto-capture, so transitions can be logged once rather than
    // twice a second. See LogAutoCaptureGate.
    private string? _lastAutoCaptureGate;

    // Thumbnail items for recent captures
    public ObservableCollection<ThumbnailItem> RecentCaptures { get; } = new();
    public ObservableCollection<CameraControlItem> CameraControls { get; } = new();

    // Filmstrip multi-select (ctrl/shift-click) — drives the batch action bar's visibility and
    // targets (Delete Selected, Apply Adjustments to Selected).
    public int SelectedCount => RecentCaptures.Count(t => t.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    /// <summary>Called from MainWindow.axaml.cs's ctrl/shift-click handling on a thumbnail —
    /// toggles that thumbnail's selection and refreshes the computed selection properties the
    /// action bar binds to.</summary>
    /// <summary>Selects every page in the cart, or clears the selection when all are already
    /// selected — one control for both directions, since needing "select all" is almost always
    /// followed by needing to undo it.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var selectAll = RecentCaptures.Any(t => !t.IsSelected);
        foreach (var thumbnail in RecentCaptures) thumbnail.IsSelected = selectAll;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectAllLabel));
        OnPropertyChanged(nameof(SelectAllLabel));
        StatusText = selectAll
            ? $"Selected all {RecentCaptures.Count} page(s) in the cart."
            : "Selection cleared.";
    }

    public string SelectAllLabel =>
        RecentCaptures.Count > 0 && RecentCaptures.All(t => t.IsSelected) ? "Select None" : "Select All";

    public void ToggleThumbnailSelection(ThumbnailItem item)
    {
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectAllLabel));
    }

    public void ClearSelection()
    {
        foreach (var t in RecentCaptures) t.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectAllLabel));
    }

    public MainWindowViewModel()
    {
        // Design-time constructor
        _cameraService = null!;
        _dbContext = null!;
        _queueService = null!;
    }

    public MainWindowViewModel(ICameraService cameraService) : this(cameraService, null)
    {
    }

    /// <param name="dbPath">Overrides the database file this window and its background worker
    /// use — used by tests so they can exercise this exact class without touching the
    /// operator's real database (AppDbContext's own default path). Null (the real app's
    /// usage, via the single-argument constructor above) keeps existing behavior exactly.</param>
    public MainWindowViewModel(ICameraService cameraService, string? dbPath)
    {
        _cameraService = cameraService;
        // Restored before any capture can happen, so a batch resumed after a restart keeps
        // shooting the same way up as the pages already in it.
        _liveViewRotation = ((_preferences.LiveViewRotation % 360) + 360) % 360;
        _dbContext = dbPath == null ? new AppDbContext() : new AppDbContext(dbPath);
        _queueService = new CaptureQueueService(_dbContext);
        _manifests = new BatchManifestService();
        _batchLocks = new BatchLockService();
        _batchSync = new BatchSyncService(_dbContext, _manifests);

        _worker = new MicroCapture.Processing.BackgroundProcessingWorker(dbPath);
        _worker.StatusChanged += (s, msg) => {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = $"Background: {msg}");
        };
        _worker.JobCompleted += (s, result) => {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // A job just left the queue — this is what re-unlocks frame editing once nothing
                // is still in flight under the current geometry.
                _ = RefreshFrameEditPermissionAsync();
                // Match by JobId, not OriginalFilePath: several sibling jobs (one per fixed
                // frame) can share the same source capture file, so FilePath alone is no longer
                // a unique key — each job now gets its own ProcessingResult (stamped with
                // JobId by BackgroundProcessingWorker) and its own single thumbnail row. A
                // normal split-spread job (left/right from one page) is still exactly one job
                // with 2 OutputFilePaths — that page's own single thumbnail just shows the left
                // half's preview (index 0), same as before this change.
                var thumbnail = RecentCaptures.FirstOrDefault(t => t.JobId == result.JobId);
                if (thumbnail != null)
                {
                    thumbnail.Status = !result.Success ? "Processing failed"
                        : result.OcrStatus == "Failed" ? "Processed — OCR failed"
                        : result.QcVerdict == "FAIL" ? "Processed — QC fail"
                        : result.QcVerdict == "WARNING" ? "Processed — needs review"
                        : "Processed";

                    if (result.Success && result.OutputFilePaths.Count > 0)
                    {
                        try
                        {
                            // The processed derivative is a TIFF that Avalonia's Skia-backed
                            // Bitmap decoder can't read directly — bridge through the same
                            // OpenCV-based decode path batch export uses, or the thumbnail
                            // silently never updates past the raw just-captured preview.
                            var bytes = MicroCapture.Processing.ImageDecodeHelper.GetDisplayBytes(result.OutputFilePaths[0]);
                            if (bytes != null)
                            {
                                using var stream = new MemoryStream(bytes);
                                var newThumb = Bitmap.DecodeToWidth(stream, 120);
                                var old = thumbnail.Thumbnail;
                                thumbnail.Thumbnail = newThumb;
                                old?.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Thumbnail refresh failed for '{result.OutputFilePaths[0]}': {ex}");
                        }
                    }
                }
            });
        };
        _worker.Start();

        _cameraService.StateChanged += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsConnected = e.IsConnected;
                ConnectionStatus = e.IsConnected ? "CONNECTED" : "DISCONNECTED";
                CameraModel = e.IsConnected ? _connectedCameraModel : "Not connected";
                StatusText = e.StatusMessage;
                UpdateCaptureReadiness();
            });
        };

        _cameraService.LiveViewActiveChanged += (s, active) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsLiveViewActive = active);
        };

        _cameraService.LiveViewFrameReceived += (s, frameBytes) =>
        {
            // Drop stale frames while the UI is rendering. This keeps Live View from
            // building an unbounded dispatcher queue when camera or processing work is busy.
            if (Interlocked.Exchange(ref _liveViewFramePending, 1) != 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new MemoryStream(frameBytes);
                    var bitmap = new Bitmap(ms);
                    var old = LiveViewImage;
                    LiveViewImage = bitmap;
                    old?.Dispose();

                    // Analysis is throttled to keep the live-view path responsive while still
                    // providing a meaningful capture-readiness gate. Both modes share the
                    // throttle: 500ms x StableFramesRequired is the dwell the operator is used to.
                    if (DateTime.UtcNow - _lastDocumentCheckUtc >= TimeSpan.FromMilliseconds(500))
                    {
                        _lastDocumentCheckUtc = DateTime.UtcNow;
                        if (Frames.Count > 0)
                        {
                            // Frame mode measures only what's inside the drawn frames — there is
                            // no boundary to find, and a frame may deliberately cover a region
                            // with no clean edge at all.
                            var regions = ToFractionalFrames();
                            UpdateFrameModeStatus(regions.Length > 0
                                ? MicroCapture.Processing.ImageProcessor.CheckLiveRegions(frameBytes, regions)
                                : MicroCapture.Processing.LiveRegionsCheck.None);
                        }
                        else
                        {
                            UpdateDocumentStatus(MicroCapture.Processing.ImageProcessor.CheckLiveFrame(frameBytes));
                        }
                    }
                    FocusStatus = "Camera-controlled";
                    ExposureStatus = "Camera-controlled";
                    UpdateCaptureReadiness();
                }
                catch (Exception ex) { Console.Error.WriteLine($"Live View frame decode failed: {ex}"); }
                finally { Volatile.Write(ref _liveViewFramePending, 0); }
            });
        };

        _ = HydrateLastUsedCaptureFormatAsync();
        InitializeFrameTracking();

        // The scan tile sits between two slices of RecentCaptures; keep those slices in step
        // with every add/remove/clear. Individual tiles' PageNumber changes (from a shift or
        // renumber) are followed by the explicit RefreshScanTile calls at those sites.
        RecentCaptures.CollectionChanged += (_, _) => RebuildStripPartition();
    }

    /// <summary>Sets SelectedCaptureFormat's initial value from whatever format the most
    /// recently captured job (across every batch, not just the current one) actually used —
    /// so the dropdown remembers the operator's last choice across app restarts, the same way
    /// DPI/dewarp/etc. are sticky via their own Batch persistence. CaptureFormat is per-job, not
    /// per-batch, so there's no Batch row to read this back from at Start Batch time the way
    /// SelectedDpi etc. do — this is the one-time app-startup equivalent instead. Silently
    /// leaves the "TIFF" default in place if the query fails or no jobs exist yet (a brand-new
    /// install), consistent with how design-time/never-captured state should look.</summary>
    private async Task HydrateLastUsedCaptureFormatAsync()
    {
        try
        {
            var lastFormat = await _dbContext.CaptureJobs
                .OrderByDescending(j => j.Timestamp)
                .Select(j => j.CaptureFormat)
                .FirstOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(lastFormat))
                SelectedCaptureFormat = lastFormat;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not hydrate last-used capture format: {ex}");
        }
    }

    private void UpdateCaptureReadiness()
    {
        if (!IsConnected)
            CaptureReadiness = "NOT READY";
        else if (string.IsNullOrWhiteSpace(ProjectCode) || string.IsNullOrWhiteSpace(BatchCode))
            CaptureReadiness = "SET PROJECT & BATCH";
        else if (IsEditingFrames)
            CaptureReadiness = "EDITING FRAMES";
        else if (IsAutoCapture)
            CaptureReadiness = DocumentStatus.StartsWith("✓") ? "AUTO CAPTURE ACTIVE" : "WAITING FOR DOCUMENT";
        else
            CaptureReadiness = DocumentStatus.StartsWith("✓") ? "READY TO CAPTURE" : "WAITING FOR DOCUMENT";

        // The scan tile's border mirrors readiness. Cheap direct assignment only — this runs on
        // every live-view frame, so it must NOT trigger the full RefreshScanTile (clamp +
        // partition rebuild); position/visibility changes are pushed from their own sites.
        ScanTile.Readiness = CaptureReadiness;
    }

    /// <summary>Projects the drawn frames into 0..1 fractions of the frame they were authored
    /// against, which is what <see cref="MicroCapture.Processing.ImageProcessor.CheckLiveRegions"/>
    /// expects — the live feed's own pixel size may differ from the reference space (a batch
    /// calibrated at full resolution), so fractions are the only common ground.</summary>
    private MicroCapture.Processing.FixedFrameRect[] ToFractionalFrames()
    {
        if (FrameReferenceWidth <= 0 || FrameReferenceHeight <= 0)
            return Array.Empty<MicroCapture.Processing.FixedFrameRect>();

        var result = new MicroCapture.Processing.FixedFrameRect[Frames.Count];
        for (var i = 0; i < Frames.Count; i++)
        {
            var f = Frames[i];
            result[i] = new MicroCapture.Processing.FixedFrameRect(
                f.X / FrameReferenceWidth, f.Y / FrameReferenceHeight,
                f.Width / FrameReferenceWidth, f.Height / FrameReferenceHeight);
        }
        return result;
    }

    /// <summary>Joins every frame's content signature into one buffer so the existing
    /// <see cref="ContentDifference"/> comparison — a mean absolute difference over equal-length
    /// arrays — works unchanged across N frames. A frame whose signature is missing contributes a
    /// zero block so the layout stays positional. When the frame count changes the buffer length
    /// changes too, which ContentDifference reports as "definitely different"; that is the right
    /// answer after an edit, and it costs nothing.</summary>
    private static byte[] ConcatSignatures(MicroCapture.Processing.RegionCheck[] regions)
    {
        const int perRegion = 24 * 24;
        var buffer = new byte[regions.Length * perRegion];
        for (var i = 0; i < regions.Length; i++)
        {
            var sig = regions[i].ContentSignature;
            if (sig == null) continue;
            Array.Copy(sig, 0, buffer, i * perRegion, Math.Min(sig.Length, perRegion));
        }
        return buffer;
    }

    /// <summary>Auto-capture state machine for fixed-frame mode. Deliberately parallel to
    /// <see cref="UpdateDocumentStatus"/>, but with no boundary requirement and no positional
    /// smoothing: the frames are pinned by the operator, so there is no detected rectangle to
    /// track. "Stable" therefore means the frames' *contents* have stopped changing — the page
    /// has settled and the operator's hand has withdrawn — rather than a rectangle holding still.
    ///
    /// <para>The weakest frame gates focus: one out-of-focus frame should hold the whole capture,
    /// since every frame becomes its own output page.</para></summary>
    private void UpdateFrameModeStatus(MicroCapture.Processing.LiveRegionsCheck check)
    {
        if (!check.Decoded || check.Regions == null || check.Regions.Length == 0)
        {
            _stableFrameCount = 0;
            _previousFrameSignature = null;
            DocumentStatus = "Frames set — waiting for live view";
            LogAutoCaptureGate("no-live-view", "frame mode: no decodable regions");
            return;
        }

        var signature = ConcatSignatures(check.Regions);
        _lastDetectedSignature = signature;

        // The geometry a shot would use is still moving under the operator's hand.
        if (IsEditingFrames)
        {
            _stableFrameCount = 0;
            DocumentStatus = "Editing frames — auto-capture paused";
            return;
        }

        if (!IsAutoCapture)
        {
            _stableFrameCount = 0;
            DocumentStatus = $"✓ {Frames.Count} frame(s) — press CAPTURE";
            return;
        }

        var minSharpness = check.Regions.Min(r => r.Sharpness);
        if (minSharpness < LiveSharpnessThreshold)
        {
            _stableFrameCount = 0;
            _previousFrameSignature = signature;
            DocumentStatus = "✓ Frames set — focusing…";
            LogAutoCaptureGate("sharpness", $"frame mode: weakest region {minSharpness:F1} < {LiveSharpnessThreshold} (per-region: {string.Join(", ", check.Regions.Select(r => r.Sharpness.ToString("F1")))})");
            return;
        }

        // Motion is measured between CONSECUTIVE RAW frames. It used to be measured against an
        // exponentially smoothed reference, which quietly defeated the whole check: the reference
        // chases the current frame, so a couple of checks into a page turn it had caught up, the
        // difference collapsed, and a page still visibly moving read as settled.
        var motion = ContentDifference(signature, _previousFrameSignature);
        _previousFrameSignature = signature;

        var moving = _previousFrameSignature != null && motion > MotionThreshold;
        if (moving) _sawPageTurnSinceCapture = true;

        _stableFrameCount = moving ? 0 : Math.Min(_stableFrameCount + 1, StableFramesRequired);
        if (_stableFrameCount < StableFramesRequired)
        {
            DocumentStatus = "✓ Frames set — hold still…";
            LogAutoCaptureGate("settling",
                $"frame mode: {(moving ? $"page still moving, motion {motion:F1} > {MotionThreshold}" : "holding still, counting up")}, stable {_stableFrameCount}/{StableFramesRequired}");
            return;
        }

        // Stillness alone is not evidence of a new page — an untouched page is perfectly still
        // too. Require an actual page turn to have happened since the last capture, so the
        // sequence must be move-then-settle rather than just settle.
        if (_lastCapturedSignature != null && !_sawPageTurnSinceCapture)
        {
            DocumentStatus = "✓ Captured — turn the page to continue";
            LogAutoCaptureGate("awaiting-page-turn", "frame mode: still and in focus, but no page turn seen since the last capture");
            return;
        }

        var capturedDiff = ContentDifference(signature, _lastCapturedSignature);
        if (_lastCapturedSignature != null && capturedDiff < ContentChangeThreshold)
        {
            DocumentStatus = "✓ Captured — swap page to continue";
            LogAutoCaptureGate("same-as-last", $"frame mode: matches last capture, diff {capturedDiff:F1} < {ContentChangeThreshold}");
            return;
        }

        if (Volatile.Read(ref _captureInProgress) != 0)
        {
            DocumentStatus = "✓ Capturing…";
            return;
        }

        DocumentStatus = "✓ Capturing…";
        LogAutoCaptureGate("firing", $"frame mode: {Frames.Count} frame(s), weakest sharpness {minSharpness:F1}");
        _lastCapturedSignature = signature;
        _stableFrameCount = 0;
        _sawPageTurnSinceCapture = false;
        _ = CaptureAsync();
    }

    /// <summary>Auto-capture state machine. Fires the shutter automatically once a page has
    /// held stable and in focus for <see cref="StableFramesRequired"/> consecutive checks and
    /// its content actually differs from whatever was last captured — content, not just
    /// position, because a fixed copy-stand/page guide places every page in nearly the same
    /// spot, so position alone can't tell a page turn from the same page still sitting there.
    /// When <see cref="IsAutoCapture"/> is off, behavior is unchanged from a simple
    /// boundary-present/absent check with no stability, focus, or auto-firing.</summary>
    private void UpdateDocumentStatus(MicroCapture.Processing.LiveFrameCheck check)
    {
        if (!check.Detected)
        {
            _stableFrameCount = 0;
            _smoothedRect = null;
            _lastDetectedSignature = null;
            _previousFrameSignature = null;
            DocumentStatus = "Waiting for boundary";
            LogAutoCaptureGate("no-boundary", "auto-detect: no page-sized contour found — check lighting/contrast against the backdrop, or draw frames instead");
            return;
        }

        var rect = ((double)check.X, (double)check.Y, (double)check.Width, (double)check.Height);
        _lastDetectedSignature = check.ContentSignature;

        if (!IsAutoCapture)
        {
            _stableFrameCount = 0;
            _smoothedRect = rect;
            DocumentStatus = "✓ Boundary detected";
            return;
        }

        if (check.Sharpness < LiveSharpnessThreshold)
        {
            _stableFrameCount = 0;
            _smoothedRect = rect;
            DocumentStatus = "✓ Boundary detected — focusing…";
            LogAutoCaptureGate("sharpness", $"auto-detect: document-region sharpness {check.Sharpness:F1} < {LiveSharpnessThreshold}");
            return;
        }

        // Compare against the smoothed reference from before this update, then blend it
        // toward the new detection — comparing against the raw previous frame instead would
        // make ordinary hand/camera jitter reset stability far too often.
        var previousSmoothed = _smoothedRect;
        var wasStable = previousSmoothed.HasValue && IsRectStable(previousSmoothed.Value, rect, check.ImageWidth, check.ImageHeight);
        _smoothedRect = previousSmoothed.HasValue ? LerpRect(previousSmoothed.Value, rect, PositionSmoothingFactor) : rect;
        _stableFrameCount = wasStable ? Math.Min(_stableFrameCount + 1, StableFramesRequired) : 1;

        // Position stability alone said nothing about the page's CONTENT: a page guide holds every
        // page in the same spot, so a sheet sliding into place mid-turn sits at a stable rect
        // while its content is still changing. Gate on content motion as well.
        var contentMotion = ContentDifference(check.ContentSignature, _previousFrameSignature);
        _previousFrameSignature = check.ContentSignature;
        var contentMoving = _previousFrameSignature != null && contentMotion > MotionThreshold;
        if (contentMoving) _sawPageTurnSinceCapture = true;

        if (!wasStable || contentMoving) _stableFrameCount = 0;

        if (_stableFrameCount < StableFramesRequired)
        {
            DocumentStatus = "✓ Boundary detected — hold still…";
            LogAutoCaptureGate("settling",
                $"auto-detect: {(contentMoving ? $"page still moving, motion {contentMotion:F1} > {MotionThreshold}" : "holding still, counting up")}, stable {_stableFrameCount}/{StableFramesRequired}");
            return;
        }

        if (_lastCapturedSignature != null && !_sawPageTurnSinceCapture)
        {
            DocumentStatus = "✓ Captured — turn the page to continue";
            LogAutoCaptureGate("awaiting-page-turn", "auto-detect: still and in focus, but no page turn seen since the last capture");
            return;
        }

        var contentDiff = ContentDifference(check.ContentSignature, _lastCapturedSignature);
        var isSameAsLastCapture = _lastCapturedSignature != null && contentDiff < ContentChangeThreshold;
        if (isSameAsLastCapture)
        {
            DocumentStatus = "✓ Captured — swap page to continue";
            LogAutoCaptureGate("same-as-last", $"auto-detect: matches last capture, diff {contentDiff:F1} < {ContentChangeThreshold}");
            return;
        }

        if (Volatile.Read(ref _captureInProgress) != 0)
        {
            DocumentStatus = "✓ Capturing…";
            return;
        }

        DocumentStatus = "✓ Capturing…";
        LogAutoCaptureGate("firing", $"auto-detect: sharpness {check.Sharpness:F1}, {DescribeDifference(contentDiff)}");
        _lastCapturedSignature = check.ContentSignature;
        _stableFrameCount = 0;
        _sawPageTurnSinceCapture = false;
        _ = CaptureAsync();
    }

    /// <summary>Mean absolute difference between two content signatures (0-255 scale).
    /// Missing or mismatched signatures are treated as "definitely different" so a
    /// comparison failure never blocks a real capture.</summary>
    /// <summary>Test seam: the thresholds the auto-capture decision turns on. Exposed so a
    /// simulated page turn can assert the decision rather than re-implementing it.</summary>
    public static double MotionThresholdForTests => MotionThreshold;
    public static double ContentChangeThresholdForTests => ContentChangeThreshold;
    public static int StableFramesRequiredForTests => StableFramesRequired;
    public static double ContentDifferenceForTests(byte[]? a, byte[]? b) => ContentDifference(a, b);

    private static double ContentDifference(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0) return double.MaxValue;
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    private static bool IsRectStable((double X, double Y, double Width, double Height) a, (double X, double Y, double Width, double Height) b, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return false;
        var toleranceX = imageWidth * StablePositionToleranceFraction;
        var toleranceY = imageHeight * StablePositionToleranceFraction;
        return Math.Abs(a.X - b.X) <= toleranceX && Math.Abs(a.Width - b.Width) <= toleranceX &&
               Math.Abs(a.Y - b.Y) <= toleranceY && Math.Abs(a.Height - b.Height) <= toleranceY;
    }

    private static (double X, double Y, double Width, double Height) LerpRect(
        (double X, double Y, double Width, double Height) from,
        (double X, double Y, double Width, double Height) to,
        double t) => (
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            from.Width + (to.Width - from.Width) * t,
            from.Height + (to.Height - from.Height) * t);



    /// <summary>Describes why a check hasn't settled yet, in terms that match the number printed
    /// beside it. Distinguishes "the page is genuinely still moving" from "the page is holding
    /// still and we're counting up to the required dwell", which read identically before — and
    /// avoids reporting the no-reference-yet sentinel, which is double.MaxValue and prints as
    /// three hundred digits of noise.</summary>
    private static string DescribeSettling(bool settled, double difference)
    {
        if (settled) return "holding still, counting up";
        if (IsNoReference(difference)) return "first frame — establishing a reference to compare against";
        return $"content still moving, diff {difference:F1} vs threshold {ContentChangeThreshold}";
    }

    /// <summary><see cref="ContentDifference"/> returns
    /// double.MaxValue to mean "nothing to compare against yet", which is right for the
    /// comparison but prints as three hundred digits of noise in a log.</summary>
    private static bool IsNoReference(double difference) =>
        double.IsPositiveInfinity(difference) || difference >= double.MaxValue;

    private static string DescribeDifference(double difference) =>
        IsNoReference(difference) ? "no previous capture to compare against" : $"diff {difference:F1}";

    /// <summary>Records which gate is currently holding auto-capture back, with the numbers behind
    /// it. Auto-capture failing in the field reads as "it just didn't fire" — the on-screen status
    /// says which stage it's stuck at but vanishes immediately and carries no measured values, so
    /// there's nothing to diagnose from afterwards. Logs only on a transition, since checks run
    /// twice a second. Never throws: a diagnostics failure must not disturb capture.</summary>
    private void LogAutoCaptureGate(string gate, string detail)
    {
        if (_lastAutoCaptureGate == gate) return;
        _lastAutoCaptureGate = gate;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroCapture", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "auto-capture.log"),
                $"[{DateTimeOffset.Now:O}] {gate} — {detail}{Environment.NewLine}");
        }
        catch { /* Diagnostics are best-effort. */ }
    }

    // ---------- Adjustment history ----------

    /// <summary>One page's adjustment stack, captured so a change can be undone.</summary>
    private sealed record AdjustmentSnapshot(
        string JobId, bool HasManualAdjustments, int RotationDegrees, bool FlipHorizontal,
        bool FlipVertical, double Brightness, double Contrast, double Saturation,
        double Sharpness, double WhiteBalance)
    {
        public static AdjustmentSnapshot From(CaptureJob job) => new(
            job.Id, job.HasManualAdjustments, job.RotationDegrees, job.FlipHorizontal,
            job.FlipVertical, job.Brightness, job.Contrast, job.Saturation,
            job.Sharpness, job.WhiteBalance);

        public void ApplyTo(CaptureJob job)
        {
            job.HasManualAdjustments = HasManualAdjustments;
            job.RotationDegrees = RotationDegrees;
            job.FlipHorizontal = FlipHorizontal;
            job.FlipVertical = FlipVertical;
            job.Brightness = Brightness;
            job.Contrast = Contrast;
            job.Saturation = Saturation;
            job.Sharpness = Sharpness;
            job.WhiteBalance = WhiteBalance;
        }
    }

    /// <summary>One undoable adjustment change: what the affected pages looked like before, and
    /// what they look like after. Both directions are stored because redo needs the "after" just
    /// as much as undo needs the "before".</summary>
    private sealed record AdjustmentEdit(string Description, List<AdjustmentSnapshot> Before, List<AdjustmentSnapshot> After);

    private readonly List<AdjustmentEdit> _undoStack = new();
    private readonly List<AdjustmentEdit> _redoStack = new();

    [ObservableProperty] private string _undoLabel = "Undo";
    [ObservableProperty] private string _redoLabel = "Redo";

    public bool CanUndoAdjustment => _undoStack.Count > 0;
    public bool CanRedoAdjustment => _redoStack.Count > 0;

    private void RefreshHistoryState()
    {
        UndoLabel = _undoStack.Count > 0 ? $"Undo {_undoStack[^1].Description}" : "Undo";
        RedoLabel = _redoStack.Count > 0 ? $"Redo {_redoStack[^1].Description}" : "Redo";
        OnPropertyChanged(nameof(CanUndoAdjustment));
        OnPropertyChanged(nameof(CanRedoAdjustment));
        UndoAdjustmentCommand.NotifyCanExecuteChanged();
        RedoAdjustmentCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Records an adjustment change so it can be undone. Called after the change has been
    /// saved, with the snapshots taken before it.</summary>
    public async Task RecordAdjustmentEditAsync(string description, IEnumerable<string> jobIds, List<object> beforeSnapshots)
    {
        var before = beforeSnapshots.OfType<AdjustmentSnapshot>().ToList();
        if (before.Count == 0) return;
        try
        {
            _dbContext.ChangeTracker.Clear();
            var ids = jobIds.ToHashSet();
            var after = await _dbContext.CaptureJobs.AsNoTracking()
                .Where(j => ids.Contains(j.Id))
                .Select(j => AdjustmentSnapshot.From(j))
                .ToListAsync();

            _undoStack.Add(new AdjustmentEdit(description, before, after));
            // A new edit invalidates the redo branch, the same as any editor.
            _redoStack.Clear();
            RefreshHistoryState();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not record adjustment history: {ex}");
        }
    }

    /// <summary>The adjustment state of every page Crop Review could reach from here: the open
    /// batch, plus the page and any selection it was opened for, in case the batch can't be
    /// determined. Keyed by job id so an edit's "before" can be assembled from whichever pages
    /// turn out to have been written.</summary>
    private Dictionary<string, AdjustmentSnapshot> SnapshotBatchAdjustments(string jobId, IReadOnlyList<string>? selection)
    {
        var ids = new HashSet<string> { jobId };
        if (selection != null) ids.UnionWith(selection);

        try
        {
            if (!string.IsNullOrEmpty(_currentBatchId))
            {
                _dbContext.ChangeTracker.Clear();
                ids.UnionWith(_dbContext.CaptureJobs.AsNoTracking()
                    .Where(j => j.BatchId == _currentBatchId)
                    .Select(j => j.Id));
            }
        }
        catch (Exception ex)
        {
            // Falling back to the page and its selection still gives a usable undo for the
            // ordinary edit; only a bulk apply would be under-covered.
            Console.Error.WriteLine($"Could not snapshot the batch for undo: {ex}");
        }

        return ReadAdjustmentSnapshots(ids).ToDictionary(s => s.JobId);
    }

    private List<AdjustmentSnapshot> ReadAdjustmentSnapshots(IEnumerable<string> jobIds)
    {
        try
        {
            var ids = jobIds.ToHashSet();
            if (ids.Count == 0) return new List<AdjustmentSnapshot>();
            _dbContext.ChangeTracker.Clear();
            return _dbContext.CaptureJobs.AsNoTracking()
                .Where(j => ids.Contains(j.Id))
                .ToList()
                .Select(AdjustmentSnapshot.From)
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not snapshot adjustments: {ex}");
            return new List<AdjustmentSnapshot>();
        }
    }

    /// <summary>Captures the current adjustment state of the given pages, for the caller to hand
    /// back to <see cref="RecordAdjustmentEditAsync"/> once its change has been saved.</summary>
    public List<object> CaptureAdjustmentSnapshots(IEnumerable<string> jobIds)
    {
        try
        {
            var ids = jobIds.ToHashSet();
            return _dbContext.CaptureJobs.AsNoTracking()
                .Where(j => ids.Contains(j.Id))
                .ToList()
                .Select(j => (object)AdjustmentSnapshot.From(j))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not snapshot adjustments: {ex}");
            return new List<object>();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndoAdjustment))]
    private async Task UndoAdjustmentAsync()
    {
        if (_undoStack.Count == 0) return;
        var edit = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        if (await ApplySnapshotsAsync(edit.Before, $"Undid {edit.Description}"))
        {
            _redoStack.Add(edit);
        }
        RefreshHistoryState();
    }

    [RelayCommand(CanExecute = nameof(CanRedoAdjustment))]
    private async Task RedoAdjustmentAsync()
    {
        if (_redoStack.Count == 0) return;
        var edit = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        if (await ApplySnapshotsAsync(edit.After, $"Redid {edit.Description}"))
        {
            _undoStack.Add(edit);
        }
        RefreshHistoryState();
    }

    /// <summary>Restores a set of adjustment snapshots and re-queues those pages, which is the
    /// same mechanism Crop Review uses to apply an edit — the page is reprocessed from its
    /// preserved original, so undo costs nothing but the reprocessing time.</summary>
    private async Task<bool> ApplySnapshotsAsync(List<AdjustmentSnapshot> snapshots, string statusMessage)
    {
        try
        {
            _dbContext.ChangeTracker.Clear();
            var ids = snapshots.Select(s => s.JobId).ToHashSet();
            var jobs = await _dbContext.CaptureJobs.Where(j => ids.Contains(j.Id)).ToListAsync();
            var byId = snapshots.ToDictionary(s => s.JobId);

            foreach (var job in jobs)
            {
                if (!byId.TryGetValue(job.Id, out var snapshot)) continue;
                snapshot.ApplyTo(job);
                job.ProcessingStatus = "Pending";
                job.QcStatus = "Pending";
                job.OcrStatus = "Pending";
                job.ExportStatus = "Pending";
            }
            await _dbContext.SaveChangesAsync();

            foreach (var thumbnail in RecentCaptures.Where(t => ids.Contains(t.JobId)))
                thumbnail.Status = "Reprocessing…";

            await PublishManifestAsync();
            StatusText = $"{statusMessage} — {jobs.Count} page(s) reprocessing.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not change adjustments: {ex.Message}";
            return false;
        }
    }

    // ---------- Commands ----------

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            if (IsConnected)
            {
                await _cameraService.StopLiveViewAsync();
                await _cameraService.DisconnectAsync();
                return;
            }
            StatusText = "Connecting to camera...";
            var cameras = await _cameraService.GetConnectedCamerasAsync();
            var first = cameras.FirstOrDefault();
            if (first == null)
            {
                StatusText = "No cameras found";
                return;
            }
            if (!await _cameraService.ConnectAsync(first.Id))
            {
                StatusText = "Camera connection failed — see diagnostic log";
                return;
            }
            _connectedCameraModel = first.Model;
            CameraModel = first.Model;
            await _cameraService.StartLiveViewAsync();
            await LoadCameraSettingsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Camera error: {ex.Message}";
        }
    }

    /// <summary>Hydrates every UI-observable field from an already-saved <see cref="Batch"/> row
    /// — the shared core of both "resume the batch matching the typed Project/Batch Code" (see
    /// <see cref="StartBatchAsync"/>) and "reopen a batch picked from Recent Batches" (see
    /// <see cref="OpenRecentBatchesAsync"/>). <paramref name="batch"/> must have its
    /// <see cref="Batch.Project"/> and <see cref="Batch.Captures"/> navigation properties already
    /// loaded.</summary>
    private async Task LoadBatchIntoUiAsync(Batch batch)
    {
        _currentProjectId = batch.ProjectId;
        _activeProjectCode = batch.Project?.Name ?? ProjectCode;
        _activeBatchCode = batch.BatchCode;
        _outputDirectory = batch.Project?.OutputDirectory ?? _outputDirectory;
        _currentBatchId = batch.Id;
        _currentBatchStatus = batch.Status;
        // The insert point is session-local and never persisted; a value left over from a
        // previously open batch must not carry into this one.
        _insertBeforePage = null;
        UpdateInsertPointLabel();
        // Highest live page number — Superseded rows (deleted pages, retired recapture attempts)
        // are excluded so a reopened batch doesn't resurrect a count higher than the pages it
        // actually holds, and the next capture doesn't skip a number.
        var livePageNumbers = batch.Captures
            .Where(c => c.ProcessingStatus != "Superseded")
            .Select(c => c.PageNumber)
            .ToList();
        PageCount = livePageNumbers.Count > 0 ? livePageNumbers.Max() : 0;
        // These assignments hydrate the observable properties FROM the already-saved batch
        // row — without suppression, each one's OnXChanged would immediately
        // PersistBatchSettingAsync straight back to the very row it was just read from
        // (redundant at best; racy at worst, since _currentBatchId above is already set by the
        // time these run).
        // A debounced write still pending for the PREVIOUS batch must not land now that
        // _currentBatchId has moved — PersistBatchSettingAsync resolves it at execution time, so
        // a stale timer would write this batch's row with the old batch's frames.
        _framePersistTimer?.Stop();

        _suppressPersist = true;
        try
        {
            ProjectCode = _activeProjectCode;
            BatchCode = _activeBatchCode;
            HydrateFramesFromBatch(batch);
            SelectedDpi = batch.Dpi;
            DewarpEnabled = batch.DewarpEnabled;
            BinarizeEnabled = batch.BinarizeEnabled;
            BleedthroughEnabled = batch.BleedthroughEnabled;
        }
        finally
        {
            _suppressPersist = false;
        }
        // A batch opened by any route resolves its folder here, so capture and the manifest agree
        // on where this batch lives regardless of which command opened it. A batch predating batch
        // folders gets one now, describing its files where they already are rather than moving them.
        _currentBatchFolder = batch.FolderPath;
        if (string.IsNullOrWhiteSpace(_currentBatchFolder) && !string.IsNullOrWhiteSpace(_outputDirectory))
        {
            try
            {
                _currentBatchFolder = await _batchSync.BackfillLegacyBatchAsync(batch, _outputDirectory);
            }
            catch (Exception ex)
            {
                // A batch that can't be given a folder still opens and works exactly as it did
                // before batch folders existed — it just isn't portable yet.
                Console.Error.WriteLine($"Could not create a batch folder for '{batch.BatchCode}': {ex}");
            }
        }

        UpdateOpenBatchLabels(batch);
        await LoadRecentCapturesFromBatchAsync(batch);
        await RefreshFrameEditPermissionAsync();
        RefreshScanTile();
    }

    /// <summary>Opens the Recent Batches picker and, if the operator picks one, reopens it —
    /// unconditionally flipping its Status back to "Active" (even if it was Completed/Exported)
    /// so a previously-finalized batch becomes fully resumable again, same as any in-progress
    /// batch. One unified reopen behavior, no separate read-only mode, per product decision.</summary>
    [RelayCommand]
    private async Task OpenRecentBatchesAsync(Avalonia.Controls.Window? owner)
    {
        if (owner == null) return;
        try
        {
            var picked = await MicroCapture.UI.Views.RecentBatchesDialog.PickAsync(owner, _dbContext);
            if (picked == null) return;

            // Clear the tracker before re-querying: this _dbContext has been tracking every
            // CaptureJob/Batch it has ever touched this session (see CaptureQueueService.
            // EnqueueCaptureAsync), so a plain Include query below would silently return those
            // frozen-at-creation-time instances — e.g. every job still showing "Pending" even
            // though the background worker (using its own separate context/connection) finished
            // them long ago — instead of the batch's real current state.
            _dbContext.ChangeTracker.Clear();
            var batch = await _dbContext.Batches
                .Include(b => b.Project)
                .Include(b => b.Captures)
                .FirstOrDefaultAsync(b => b.Id == picked.Id);
            if (batch == null) return;

            if (batch.Status != "Active")
            {
                batch.Status = "Active";
                await _dbContext.SaveChangesAsync();
            }

            await LoadBatchIntoUiAsync(batch);
            StatusText = $"Reopened batch '{batch.BatchCode}' for project '{_activeProjectCode}' at page {PageCount}";
        }
        catch (Exception ex)
        {
            // Without this, an exception here (e.g. a SQLite busy/lock error racing the
            // background worker's own writes) unwound the whole async command silently — the
            // dialog never appeared and nothing told the operator why ("Recent" looked entirely
            // dead). Surfacing it at least gives a visible, diagnosable failure instead of none.
            StatusText = $"Could not open Recent Batches: {ex.Message}";
        }
    }

    /// <summary>Closes the open batch and returns the app to an empty state, so another batch can
    /// be created or opened. Without this the only way back to a clean slate was restarting, since
    /// every path into the app either creates a batch or opens one.</summary>
    [RelayCommand]
    private async Task CloseBatchAsync(Avalonia.Controls.Window? owner)
    {
        if (_currentBatchId == null)
        {
            StatusText = "No batch is open.";
            return;
        }

        if (owner != null)
        {
            var proceed = await MicroCapture.UI.Views.ConfirmDialog.AskAsync(owner,
                $"Close batch '{_activeBatchCode}'?\n\n" +
                "Nothing is deleted — every page stays in the batch folder and you can open it " +
                "again at any time. Pages still processing will finish in the background.",
                "Close batch");
            if (!proceed) return;
        }

        try
        {
            // Publish before letting go, so the folder carries everything captured in this
            // session rather than whatever the last incidental publish happened to include.
            await PublishManifestAsync();
            ReleaseCurrentBatchLock();
            _batchLockHeartbeat?.Stop();

            var closed = _activeBatchCode;
            _currentBatchId = null;
            _currentBatchFolder = null;
            _currentBatchStatus = null;
            _activeBatchCode = string.Empty;
            _activeProjectCode = string.Empty;
            ProjectCode = string.Empty;
            BatchCode = string.Empty;
            PageCount = 0;
            _insertBeforePage = null;
            UpdateInsertPointLabel();

            foreach (var thumbnail in RecentCaptures) thumbnail.Thumbnail?.Dispose();
            RecentCaptures.Clear();
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
            RefreshScanTile();

            // A new batch starts with a clean history; undoing into a batch that is no longer
            // open would reprocess pages the operator can't see.
            _undoStack.Clear();
            _redoStack.Clear();
            RefreshHistoryState();

            _lastCapturedSignature = null;
            _previousFrameSignature = null;
            _sawPageTurnSinceCapture = false;
            _stableFrameCount = 0;

            UpdateOpenBatchLabels(null);
            UpdateCaptureReadiness();
            StatusText = $"Closed batch '{closed}'. Create a new batch or open an existing one.";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not close the batch: {ex.Message}";
        }
    }

    /// <summary>Creates a batch: its folder, its manifest, and the local database rows that track
    /// it while it's being worked. The folder is the batch — the database row is this machine's
    /// working copy of it, which is why the manifest is written before anything else.</summary>
    [RelayCommand]
    private async Task NewBatchAsync(Avalonia.Controls.Window? owner)
    {
        if (owner == null)
        {
            StatusText = "Could not open the New Batch dialog — no parent window.";
            return;
        }
        try
        {
            var defaultLocation = LastBatchLocation ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MicroCapture");

            // Feed the project-code box its suggestions. Location is the PARENT of the project's
            // own folder — the dialog re-appends <projectCode>/<batchCode> itself. A project row
            // with a blank OutputDirectory (shouldn't happen, but old data might) still offers its
            // name as a suggestion, just with no location to snap to.
            var knownProjects = _dbContext.Projects.AsNoTracking()
                .Where(p => p.Name != null && p.Name != "")
                .OrderBy(p => p.Name)
                .AsEnumerable()
                .Select(p => new MicroCapture.UI.ViewModels.NewBatchViewModel.KnownProject(
                    p.Name,
                    string.IsNullOrWhiteSpace(p.OutputDirectory)
                        ? string.Empty
                        : Path.GetDirectoryName(p.OutputDirectory) ?? string.Empty))
                .GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var settings = await MicroCapture.UI.Views.NewBatchDialog.ShowAsync(
                owner, defaultLocation, ProjectCode, knownProjects);
            if (settings == null) return;

            var projectCode = MicroCapture.Core.FileNaming.Sanitize(settings.ProjectCode);
            var batchCode = MicroCapture.Core.FileNaming.Sanitize(settings.BatchCode);
            var folder = settings.ResolvedBatchFolder;

            _dbContext.ChangeTracker.Clear();
            var project = _dbContext.Projects.FirstOrDefault(p => p.Name == projectCode);
            if (project == null)
            {
                project = new Project
                {
                    Name = projectCode,
                    Customer = "",
                    Description = "Auto-created from scanning session",
                    CreatedBy = Environment.UserName,
                    // The project's folder is <chosen location>/<projectCode> — the same parent
                    // this batch is about to be created under, so future batches for this project
                    // land beside it. Only a fallback for anything that still resolves against the
                    // project rather than the batch folder; a batch with its own folder keeps
                    // everything inside it.
                    OutputDirectory = Path.Combine(settings.BatchLocation, projectCode)
                };
                _dbContext.Projects.Add(project);
                await _dbContext.SaveChangesAsync();
            }

            var activeCalibrationId = await _dbContext.CameraCalibrations
                .Where(c => c.IsActive).Select(c => c.Id).FirstOrDefaultAsync();

            // Frames drawn before the batch was created are staged in memory and land on the new
            // row here, so the rig can be set up before committing to a batch code.
            EnsureFrameReference();
            var stagedFrameCount = Frames.Count;

            var batch = new Batch
            {
                ProjectId = project.Id,
                Name = batchCode,
                BatchCode = batchCode,
                Operator = Environment.UserName,
                FolderPath = folder,
                SplitBookPages = settings.SplitBookPages && stagedFrameCount == 0,
                Dpi = settings.SelectedDpi,
                PreferredExportFormat = settings.SelectedExportFormat,
                DewarpEnabled = settings.DewarpEnabled,
                BinarizeEnabled = settings.BinarizeEnabled,
                BleedthroughEnabled = settings.BleedthroughEnabled,
                CameraCalibrationId = activeCalibrationId,
                UseFixedFrames = stagedFrameCount > 0,
                FixedFrames = stagedFrameCount > 0 ? MicroCapture.Processing.ImageProcessor.FormatFixedFrames(Frames) : null,
                FixedFrameImageWidth = stagedFrameCount > 0 ? FrameReferenceWidth : 0,
                FixedFrameImageHeight = stagedFrameCount > 0 ? FrameReferenceHeight : 0
            };
            _dbContext.Batches.Add(batch);
            await _dbContext.SaveChangesAsync();

            BatchFolder.EnsureLayout(folder);
            var manifest = settings.ToManifest(batch.Id, project.Id);
            manifest.Settings.FixedFrames = batch.FixedFrames;
            manifest.Settings.FixedFrameImageWidth = batch.FixedFrameImageWidth;
            manifest.Settings.FixedFrameImageHeight = batch.FixedFrameImageHeight;
            _manifests.Save(folder, manifest);

            SelectedCaptureFormat = NormalizeCaptureFormat(settings.SelectedCaptureFormat);
            LastBatchLocation = settings.BatchLocation;
            RememberRecentBatchFolder(folder);

            ReleaseCurrentBatchLock();
            _currentBatchFolder = folder;
            _batchLocks.Acquire(folder);
            StartBatchLockHeartbeat();
            ProjectCode = projectCode;
            BatchCode = batchCode;

            await LoadBatchIntoUiAsync(batch);
            StatusText = $"Created batch '{batchCode}' in {folder}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not create the batch: {ex.Message}";
        }
    }

    /// <summary>Opens a batch from its folder, wherever it came from — this machine, a share, or a
    /// USB stick — rebuilding the local rows from the manifest so a batch created elsewhere is
    /// fully workable rather than merely visible.</summary>
    [RelayCommand]
    private async Task OpenBatchAsync(Avalonia.Controls.Window? owner)
    {
        if (owner == null)
        {
            StatusText = "Could not open the Open Batch dialog — no parent window.";
            return;
        }
        try
        {
            var roots = BatchSearchRoots();
            var folder = await MicroCapture.UI.Views.OpenBatchDialog.PickAsync(owner, _manifests, roots, RecentBatchFolders);
            if (folder == null) return;

            var validation = _manifests.Validate(folder);
            if (!validation.IsValid)
            {
                StatusText = validation.Error ?? "That folder isn't an openable batch.";
                return;
            }

            // Advisory only: tell the operator who else has it and let them decide. A hard block
            // would strand a batch someone left open, and a lock left on an unplugged USB stick
            // is normal rather than exceptional.
            if (_batchLocks.IsHeldByAnother(folder, out var holder) && holder != null)
            {
                var proceed = await MicroCapture.UI.Views.ConfirmDialog.AskAsync(owner,
                    $"This batch is currently open by {BatchLockService.DescribeHolder(holder)}.\n\n" +
                    "You can still open it, but if you both capture into it at the same time your pages can end up with the same page numbers. Continue?",
                    "Batch is open elsewhere");
                if (!proceed) return;
            }

            var batch = await _batchSync.AdoptAsync(folder, validation.Manifest!);

            var missing = _manifests.FindMissingPageFiles(folder, validation.Manifest!);
            ReleaseCurrentBatchLock();
            _currentBatchFolder = folder;
            _batchLocks.Acquire(folder);
            StartBatchLockHeartbeat();
            RememberRecentBatchFolder(folder);
            LastBatchLocation = Path.GetDirectoryName(folder);

            ProjectCode = validation.Manifest!.ProjectCode;
            BatchCode = batch.BatchCode;
            SelectedCaptureFormat = NormalizeCaptureFormat(validation.Manifest.Settings.CaptureFormat);

            await LoadBatchIntoUiAsync(batch);

            // Reported rather than silently ignored, but never fatal — a batch missing some files
            // is still worth opening to recover the rest.
            StatusText = missing.Count == 0
                ? $"Opened batch '{batch.BatchCode}' at page {PageCount}"
                : $"Opened batch '{batch.BatchCode}' at page {PageCount} — {missing.Count} file(s) listed in the batch are missing from disk";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open the batch: {ex.Message}";
        }
    }

    /// <summary>The New Batch dialog offers operator-facing names ("JPEG", "TIFF LZW", "JPEG
    /// 2000"); the capture pipeline stores and writes short ones. Resolved by the processor so
    /// both sides agree on what a name means.</summary>
    private static string NormalizeCaptureFormat(string format) =>
        MicroCapture.Processing.ImageProcessor.NormalizeCaptureFormat(format);

    [RelayCommand]
    private async Task StartBatchAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectCode) || string.IsNullOrWhiteSpace(BatchCode))
        {
            StatusText = "Enter Project Code and Batch Code first";
            return;
        }

        try
        {
            var projectCode = MicroCapture.Core.FileNaming.Sanitize(ProjectCode);
            var batchCode = MicroCapture.Core.FileNaming.Sanitize(BatchCode);

            // Ensure project exists
            var project = _dbContext.Projects.FirstOrDefault(p => p.Name == projectCode);
            if (project == null)
            {
                var projectParent = LastBatchLocation ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MicroCapture");
                project = new Project
                {
                    Name = projectCode,
                    Customer = "",
                    Description = "Auto-created from scanning session",
                    CreatedBy = Environment.UserName,
                    OutputDirectory = Path.Combine(projectParent, projectCode)
                };
                _dbContext.Projects.Add(project);
                await _dbContext.SaveChangesAsync();
            }
            _currentProjectId = project.Id;
            _activeProjectCode = projectCode;
            _activeBatchCode = batchCode;
            _outputDirectory = project.OutputDirectory;

            // Resume an existing active batch with the same code instead of always
            // creating a new one — otherwise a restart mid-batch (crash, power loss)
            // silently orphans every page captured before it and starts numbering over.
            // ChangeTracker.Clear() first: if the operator switches Project/Batch Code back to
            // a batch already touched this session (without restarting the app), a tracked
            // query would return frozen-at-creation-time CaptureJob instances instead of the
            // background worker's real current status for each — see OpenRecentBatchesAsync's
            // identical fix for the full explanation.
            _dbContext.ChangeTracker.Clear();
            var batch = await _dbContext.Batches
                .Include(b => b.Captures)
                .FirstOrDefaultAsync(b => b.ProjectId == project.Id && b.BatchCode == batchCode && b.Status == "Active");

            if (batch != null)
            {
                await LoadBatchIntoUiAsync(batch);
                StatusText = $"Resumed batch '{batchCode}' for project '{projectCode}' at page {PageCount}";
            }
            else
            {
                var activeCalibrationId = await _dbContext.CameraCalibrations
                    .Where(c => c.IsActive)
                    .Select(c => c.Id)
                    .FirstOrDefaultAsync();

                // Frames drawn before Start Batch are staged in memory (PersistFramesNow no-ops
                // without a batch id) and land on the new row here, so the operator can set the
                // rig up before committing to a batch code.
                EnsureFrameReference();
                var stagedFrameCount = Frames.Count;

                batch = new Batch
                {
                    ProjectId = project.Id,
                    Name = batchCode,
                    BatchCode = batchCode,
                    Operator = Environment.UserName,
                    SplitBookPages = SplitBookPages && stagedFrameCount == 0,
                    Dpi = SelectedDpi,
                    DewarpEnabled = DewarpEnabled,
                    BinarizeEnabled = BinarizeEnabled,
                    BleedthroughEnabled = BleedthroughEnabled,
                    CameraCalibrationId = activeCalibrationId,
                    UseFixedFrames = stagedFrameCount > 0,
                    FixedFrames = stagedFrameCount > 0 ? MicroCapture.Processing.ImageProcessor.FormatFixedFrames(Frames) : null,
                    FixedFrameImageWidth = stagedFrameCount > 0 ? FrameReferenceWidth : 0,
                    FixedFrameImageHeight = stagedFrameCount > 0 ? FrameReferenceHeight : 0
                };

                // Give it a real batch folder, the same as the New Batch dialog does. Without
                // this the batch had no folder at all, so _currentBatchFolder kept pointing at
                // whichever batch was open before — captures and thumbnails for the new batch
                // landed inside the previous one's folder.
                var startFolder = Path.Combine(project.OutputDirectory, batchCode);
                BatchFolder.EnsureLayout(startFolder);
                batch.FolderPath = startFolder;

                _dbContext.Batches.Add(batch);
                await _dbContext.SaveChangesAsync();

                ReleaseCurrentBatchLock();
                _currentBatchFolder = startFolder;
                _batchLocks.Acquire(startFolder);
                StartBatchLockHeartbeat();

                _currentBatchId = batch.Id;
                PageCount = 0;
                RecentCaptures.Clear();
                AreFrameEditsAllowed = true;
                UpdateOpenBatchLabels(batch);
                await _batchSync.PublishAsync(batch);
                StatusText = stagedFrameCount > 0
                    ? $"Batch '{batchCode}' started with {stagedFrameCount} frame(s) for project '{projectCode}'"
                    : $"Batch '{batchCode}' started for project '{projectCode}'";
            }

            UpdateCaptureReadiness();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not start batch: {ex.Message}";
        }
    }

    /// <summary>Opens the one-time lens (camera intrinsics/distortion) calibration flow —
    /// independent of any batch/project, since a lens calibration belongs to the physical rig,
    /// not to any one capture session. Unlike fixed frames — which are now drawn directly on the
    /// live view with no capture at all — this owns a repeated-capture loop internally (see
    /// <see cref="LensCalibrationViewModel"/>).</summary>
    [RelayCommand]
    private async Task CalibrateLensAsync()
    {
        if (!IsConnected) { StatusText = "Connect the camera before calibrating the lens."; return; }
        if (IsCalibrating) { StatusText = "Finish or cancel the current calibration first."; return; }

        // A lens calibration isn't tied to any project, so it needs its own directory even
        // when no batch has been started yet (_outputDirectory is only populated by
        // StartBatchAsync) — falls back to a fixed app-data location in that case, reusing
        // AppDbContext's own LocalApplicationData convention.
        var calibrationDir = !string.IsNullOrEmpty(_outputDirectory)
            ? Path.Combine(_outputDirectory, "LensCalibration")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroCapture", "LensCalibration");

        var lensCalibrationViewModel = new LensCalibrationViewModel(_dbContext, _cameraService, calibrationDir, _connectedCameraModel);
        var tcs = new TaskCompletionSource<bool>();
        lensCalibrationViewModel.Saved += (_, _) => tcs.TrySetResult(true);
        lensCalibrationViewModel.Cancelled += (_, _) => tcs.TrySetResult(false);

        LensCalibrationViewModel = lensCalibrationViewModel;
        IsCalibrating = true;

        var saved = await tcs.Task;

        IsCalibrating = false;
        LensCalibrationViewModel = null;
        StatusText = saved ? "Lens calibration saved — new batches will undistort using it." : "Lens calibration cancelled.";
    }

    /// <summary>Rebuilds the thumbnail strip from a resumed batch's most recent, non-superseded capture per page.</summary>
    private async Task LoadRecentCapturesFromBatchAsync(Batch batch)
    {
        RecentCaptures.Clear();

        // Each page — whether an ordinary auto-detect capture or one fixed frame — is its own
        // CaptureJob with its own PageNumber (see CaptureAsync), so grouping by PageNumber
        // already yields exactly one row per page here; no separate "loop N frames per job"
        // multiplication is needed (or correct) anymore.
        // Ascending, so the cart reads left-to-right as page 1, 2, 3 — the order the finished
        // document is in. This is what makes drag-reordering mean what it looks like; with the
        // newest capture on the left, dragging something leftwards would have moved it LATER in
        // the page sequence. The view scrolls to the end after a capture so the newest page is
        // still what you're looking at.
        var latestPerPage = batch.Captures
            .Where(job => job.ProcessingStatus != "Superseded")
            .GroupBy(job => job.PageNumber)
            .Select(group => group.OrderByDescending(job => job.Timestamp).First())
            .OrderBy(job => job.PageNumber)
            .TakeLast(MaxCartLoad);

        foreach (var job in latestPerPage)
        {
            // Prefer the persisted per-page thumbnail (survives the original capture file
            // being deleted once processing succeeds — see AddThumbnail/BackgroundProcessingWorker)
            // over re-decoding the original, which is only reachable for jobs still
            // Pending/InProgress at resume time. Falls back to the original for jobs captured
            // before persisted thumbnails existed (no thumbnail file on disk yet).
            var thumbPath = ThumbnailFileFor(batch.BatchCode, job.PageNumber);
            var sourcePath = File.Exists(thumbPath) ? thumbPath
                : File.Exists(job.OriginalFilePath) ? job.OriginalFilePath
                : null;
            if (sourcePath == null) continue;
            try
            {
                var bytes = await Task.Run(() => File.ReadAllBytes(sourcePath));
                var status = job.ProcessingStatus == "Completed" ? "Processed"
                    : job.ProcessingStatus == "Failed" ? "Processing failed"
                    : "Processing";

                using var stream = new MemoryStream(bytes);
                var thumb = await Task.Run(() => Bitmap.DecodeToWidth(stream, 120));
                RecentCaptures.Add(new ThumbnailItem
                {
                    JobId = job.Id,
                    PageNumber = job.PageNumber,
                    FrameIndex = 0,
                    Thumbnail = thumb,
                    BorderColor = job.ManualOverrideApplied ? new Avalonia.Media.SolidColorBrush(FixedFrameColorPalette.GetColor((job.PageNumber - 1) % 8)) : null,
                    Status = status,
                    FilePath = job.OriginalFilePath
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Thumbnail load failed for '{sourcePath}': {ex}");
            }
        }
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (Interlocked.Exchange(ref _captureInProgress, 1) != 0) return;
        try
        {
        if (IsCalibrating) { StatusText = "Finish or cancel calibration before capturing."; return; }
        if (ActiveCropReview != null) { StatusText = "Finish or cancel crop review before capturing."; return; }
        if (!IsConnected) { StatusText = "Camera not connected"; return; }
        if (_currentBatchId == null) { StatusText = "Start a batch first"; return; }

        var frameCount = Frames.Count;
        var pagesThisShot = frameCount > 0 ? frameCount : 1;

        // Normally the next page goes on the end. With an insert point set, it goes where the
        // operator put it instead and everything from there on shifts down to make room — which
        // is what lets someone who shot 400 pages go back and add the ones they missed at 350
        // without renumbering the batch by hand or recapturing the rest.
        int firstPageNumber;
        if (_insertBeforePage is { } insertAt)
        {
            firstPageNumber = insertAt;
            await ShiftPagesForInsertAsync(insertAt, pagesThisShot);
            // The insert point advances with the shot, so a run of pages inserts in order rather
            // than each one landing on top of the last.
            _insertBeforePage = insertAt + pagesThisShot;
            UpdateInsertPointLabel();
        }
        else
        {
            firstPageNumber = PageCount + 1;
            PageCount += pagesThisShot;
        }
        var pageStr = firstPageNumber.ToString("D6");
        var prefix = $"{_activeProjectCode}_{_activeBatchCode}_{pageStr}";

        StatusText = $"Capturing page{(frameCount > 0 ? "s" : "")} ...";
        ScanTile.IsCapturing = true;
        RefreshScanTile();
        try
        {
            var captureDirectory = CaptureDirectory;
            Directory.CreateDirectory(captureDirectory);
            var filePath = await _cameraService.CaptureAsync(captureDirectory, prefix);

            if (frameCount > 0)
            {
                // Each fixed frame becomes its own independent CaptureJob — its own page
                // number, own crop box, own thumbnail — instead of one job producing N output
                // files under a single shared page number. This is what lets each frame get its
                // own Crop Review, its own place in the export, and (critically) actually apply
                // manual adjustments: routing through EnqueueCaptureAsync's leftCropBox overload
                // marks the job ManualOverrideApplied, so it goes through Process()'s single-page
                // manual-crop path (which calls FinishPageProcessing) instead of the old
                // ProcessFixedFrames passthrough, which never applied rotation/brightness/etc. at
                // all.
                var capturedSize = MicroCapture.Processing.ImageDecodeHelper.GetPixelSize(filePath);
                var scaleX = capturedSize is { } cs1 && FrameReferenceWidth > 0 ? (double)cs1.Width / FrameReferenceWidth : 1.0;
                var scaleY = capturedSize is { } cs2 && FrameReferenceHeight > 0 ? (double)cs2.Height / FrameReferenceHeight : 1.0;

                for (var i = 0; i < frameCount; i++)
                {
                    var pageNumber = firstPageNumber + i;
                    var frame = Frames[i];
                    var px = (int)Math.Round(frame.X * scaleX);
                    var py = (int)Math.Round(frame.Y * scaleY);
                    var pw = (int)Math.Round(frame.Width * scaleX);
                    var ph = (int)Math.Round(frame.Height * scaleY);
                    var cropBox = FormattableString.Invariant($"{px},{py},{pw},{ph}");
                    var frameJob = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, pageNumber, SelectedCaptureFormat, SelectedDpi, cropBox, LiveViewRotation);
                    AddThumbnail(frameJob.Id, filePath, pageNumber, frameIndex: i, cropRect: (px, py, pw, ph));
                }
            }
            else
            {
                // firstPageNumber, NOT PageCount: with an insert point set, firstPageNumber is
                // the chosen slot while PageCount has already been bumped past the tail by
                // ShiftPagesForInsertAsync — using PageCount here numbered an inserted page at
                // the end of the batch, so it landed nowhere near where the operator asked.
                var job = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, firstPageNumber, SelectedCaptureFormat, SelectedDpi, rotationDegrees: LiveViewRotation);
                AddThumbnail(job.Id, filePath, firstPageNumber);
            }

            // A job is now queued under the current geometry — lock frame editing until it lands.
            _ = RefreshFrameEditPermissionAsync();

            // Record the page in the manifest now that it's captured, not when processing
            // finishes: another machine opening this batch must see the page already exists so it
            // doesn't hand out the same page number again.
            await PublishManifestAsync();

            // Require the page's content to actually change before auto-capture (or the
            // readiness indicator) can trigger again for this same physical page.
            _lastCapturedSignature = _lastDetectedSignature;
            _stableFrameCount = 0;
            // Arm the next shot only after another page turn is observed.
            _sawPageTurnSinceCapture = false;

            // Bring the scan tile back into view after any capture — append or insert. The
            // append case also fires CartAppended from AddThumbnail, but an inserted page does
            // not, and the operator still needs to see where the run is landing.
            ScanTileMoved?.Invoke(this, EventArgs.Empty);

            StatusText = $"Page{(frameCount > 0 ? "s" : "")} captured — {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Capture failed: {ex.Message}";
            // Only safe to rewind the count for a plain append. An insert already committed a
            // page-number shift (and moved DB rows) in ShiftPagesForInsertAsync; a failed
            // capture there leaves a one-shot numbering gap that the next delete/reorder's
            // RenumberBatchSequentiallyAsync closes — cheaper and safer than trying to
            // un-shift rows the background worker may already be reading.
            if (_insertBeforePage == null)
                PageCount = firstPageNumber - 1;
        }
        }
        finally
        {
            Volatile.Write(ref _captureInProgress, 0);
            ScanTile.IsCapturing = false;
            RefreshScanTile();
        }
    }

    [RelayCommand]
    private async Task RecaptureAsync()
    {
        if (Interlocked.Exchange(ref _captureInProgress, 1) != 0) return;
        try
        {
        if (IsCalibrating) { StatusText = "Finish or cancel calibration before capturing."; return; }
        if (ActiveCropReview != null) { StatusText = "Finish or cancel crop review before capturing."; return; }
        if (!IsConnected || _currentBatchId == null || PageCount == 0) return;

        var frameCount = Frames.Count;
        // GetCurrentFixedFrameCount() covers split-book-pages too (2 outputs from 1 job), which
        // still uses the single-job path below — only genuine fixed frames (Frames.Count > 0)
        // get split into independent per-frame jobs.
        var pagesInSet = GetCurrentFixedFrameCount();
        var firstPageInSet = PageCount - pagesInSet + 1;
        var pageStr = pagesInSet > 1 ? $"{firstPageInSet}-{PageCount}" : PageCount.ToString("D6");
        var prefix = $"{_activeProjectCode}_{_activeBatchCode}_{firstPageInSet.ToString("D6")}_R";

        StatusText = $"Recapturing page{(pagesInSet > 1 ? "s" : "")} {pageStr}...";
        // Recapture redoes the pages just shot; it ignores any insert point. The tile keeps its
        // slot but pulses so the operator sees the shutter fired.
        ScanTile.IsCapturing = true;
        RefreshScanTile();
        try
        {
            // Supersede all pages in this frame set
            for (var p = firstPageInSet; p <= PageCount; p++)
                await _queueService.SupersedePageAsync(_currentBatchId, p);

            var recaptureDirectory = CaptureDirectory;
            Directory.CreateDirectory(recaptureDirectory);
            var filePath = await _cameraService.CaptureAsync(recaptureDirectory, prefix);

            // Clear out the old thumbnails for this frame set before adding the new ones —
            // same "remove then re-add" shape CaptureAsync uses for a fresh capture.
            var existing = RecentCaptures.Where(t => t.PageNumber >= firstPageInSet && t.PageNumber <= PageCount).ToList();
            foreach (var thumbnail in existing)
            {
                thumbnail.Thumbnail?.Dispose();
                RecentCaptures.Remove(thumbnail);
            }

            if (frameCount > 0)
            {
                // Same per-frame independent-job shape as CaptureAsync — see its own comment for
                // why (manual-crop routing is what makes adjustments/Crop Review/export work).
                var capturedSize = MicroCapture.Processing.ImageDecodeHelper.GetPixelSize(filePath);
                var scaleX = capturedSize is { } cs1 && FrameReferenceWidth > 0 ? (double)cs1.Width / FrameReferenceWidth : 1.0;
                var scaleY = capturedSize is { } cs2 && FrameReferenceHeight > 0 ? (double)cs2.Height / FrameReferenceHeight : 1.0;

                for (var i = 0; i < frameCount; i++)
                {
                    var pageNumber = firstPageInSet + i;
                    var frame = Frames[i];
                    var px = (int)Math.Round(frame.X * scaleX);
                    var py = (int)Math.Round(frame.Y * scaleY);
                    var pw = (int)Math.Round(frame.Width * scaleX);
                    var ph = (int)Math.Round(frame.Height * scaleY);
                    var cropBox = FormattableString.Invariant($"{px},{py},{pw},{ph}");
                    var frameJob = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, pageNumber, SelectedCaptureFormat, SelectedDpi, cropBox, LiveViewRotation);
                    AddThumbnail(frameJob.Id, filePath, pageNumber, isRecapture: true, frameIndex: i, cropRect: (px, py, pw, ph));
                }
            }
            else
            {
                var job = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount, SelectedCaptureFormat, SelectedDpi, rotationDegrees: LiveViewRotation);
                AddThumbnail(job.Id, filePath, PageCount, isRecapture: true);
            }
            _ = RefreshFrameEditPermissionAsync();

            _lastCapturedSignature = _lastDetectedSignature;
            _stableFrameCount = 0;
            // Arm the next shot only after another page turn is observed.
            _sawPageTurnSinceCapture = false;

            // Same reason capture publishes. Without this the manifest still lists the attempt
            // that was just superseded, so reopening the batch resurrects the bad shot and
            // retires the good one — silent, and exactly backwards.
            await PublishManifestAsync();

            StatusText = $"Page{(pagesInSet > 1 ? "s" : "")} {pageStr} recaptured";
        }
        catch (Exception ex)
        {
            StatusText = $"Recapture failed: {ex.Message}";
        }
        }
        finally
        {
            Volatile.Write(ref _captureInProgress, 0);
            ScanTile.IsCapturing = false;
            RefreshScanTile();
        }
    }

    [RelayCommand]
    private void ToggleAutoCapture()
    {
        IsAutoCapture = !IsAutoCapture;
        // Start the stability state fresh so a stale reading from before the toggle can't
        // immediately fire — but deliberately keep _lastCapturedSignature, so toggling AUTO
        // off and back on while the same page is still sitting there doesn't re-fire for it.
        _stableFrameCount = 0;
        _smoothedRect = null;
        StatusText = IsAutoCapture
            ? "Auto-capture: ON — captures automatically once a page is stable, in focus, and new."
            : "Auto-capture: OFF";
        UpdateCaptureReadiness();
    }

    private async Task LoadCameraSettingsAsync()
    {
        CameraControls.Clear();
        try
        {
            var settings = await _cameraService.GetCameraSettingsAsync();
            foreach (var setting in settings)
                CameraControls.Add(new CameraControlItem(setting, _cameraService, message => StatusText = message));
            if (CameraControls.Count == 0)
                StatusText = "Camera connected. This body did not expose configurable capture properties.";
        }
        catch (Exception ex)
        {
            StatusText = $"Camera connected, but settings could not be read: {ex.Message}";
        }
    }

    /// <summary>Manual focus nudge, bound to the Focus panel's Near/Far buttons.
    /// <paramref name="step"/> is a MicroCapture.Core.Interfaces.FocusStep name
    /// (e.g. "NearSmall", "FarLarge") passed as the button's CommandParameter.</summary>
    [RelayCommand]
    private async Task NudgeFocusAsync(string step)
    {
        if (!IsConnected) { StatusText = "Connect the camera before adjusting focus."; return; }
        if (!IsLiveViewActive) { StatusText = "Focus needs live view running. Wait for the preview to appear."; return; }
        try
        {
            var parsed = Enum.Parse<MicroCapture.Core.Interfaces.FocusStep>(step);
            await _cameraService.NudgeFocusAsync(parsed);
            StatusText = $"Focus nudged {(step.StartsWith("Near") ? "nearer" : "farther")}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Focus adjustment failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TriggerAutoFocusAsync()
    {
        if (!IsConnected) { StatusText = "Connect the camera before triggering autofocus."; return; }
        if (!IsLiveViewActive) { StatusText = "Autofocus needs live view running. Wait for the preview to appear."; return; }
        try
        {
            StatusText = "Focusing…";
            await _cameraService.TriggerAutoFocusAsync();
            // Deliberately not "Autofocus complete." The camera accepts the command and reports
            // success even when the lens never moves, so claiming completion contradicts what the
            // operator can see. Say what was done, and point at the setting that most often
            // explains nothing happening.
            StatusText = "Autofocus run. If the image didn't change, check the lens switch is on AF.";
        }
        catch (Exception ex)
        {
            StatusText = $"Autofocus failed: {ex.Message}";
        }
    }

    private bool CanRunOcrOrExport() => !IsOcrRunning && !IsExporting;

    [RelayCommand(CanExecute = nameof(CanRunOcrOrExport))]
    private async Task RunOcrAsync()
    {
        if (_currentBatchId == null) { StatusText = "Start a batch first."; return; }

        IsOcrRunning = true;
        try
        {
            await RunOcrForCurrentBatchAsync();
        }
        finally
        {
            IsOcrRunning = false;
        }
    }

    /// <summary>Runs OCR for the active batch's finalized page set. Shared by the explicit
    /// "Run OCR" button and by PDF export (which needs the text before it can embed it) —
    /// idempotent, since BatchOcrService only touches jobs not already OcrStatus "Completed".
    /// Returns the run summary (rather than just setting StatusText itself) so a PDF export
    /// can tell the operator its searchable-text layer is missing instead of silently
    /// reporting "Exported successfully" over an un-OCR'd PDF.</summary>
    private async Task<MicroCapture.Processing.OcrRunSummary?> RunOcrForCurrentBatchAsync()
    {
        var ocrService = new MicroCapture.Processing.BatchOcrService(_dbContext);
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            StatusText = p.Total == 0 ? "OCR: nothing to do." : $"OCR: {p.Done}/{p.Total} pages...";
        });
        MicroCapture.Processing.OcrRunSummary? summary = null;
        try
        {
            summary = await ocrService.RunOcrForBatchAsync(_currentBatchId!, progress);
            StatusText = summary switch
            {
                { CliMissing: true } => "OCR skipped — Tesseract OCR is not installed (or not on PATH). Install it, then click Run OCR again.",
                { Failed: > 0 } s => $"OCR complete: {s.Completed} succeeded, {s.Failed} failed.",
                { Completed: 0, Failed: 0, Skipped: 0 } => "OCR: nothing to do — already up to date.",
                { } s => $"OCR complete: {s.Completed} page(s)."
            };
        }
        catch (Exception ex)
        {
            StatusText = $"OCR failed: {ex.Message}";
        }

        // Refresh OcrStatus on whatever thumbnails are currently visible for this batch.
        var refreshed = await _dbContext.CaptureJobs.AsNoTracking()
            .Where(j => j.BatchId == _currentBatchId)
            .ToDictionaryAsync(j => j.Id, j => j.OcrStatus);
        foreach (var thumbnail in RecentCaptures)
        {
            if (refreshed.TryGetValue(thumbnail.JobId, out var ocrStatus))
                thumbnail.OcrStatus = ocrStatus;
        }

        return summary;
    }

    /// <summary>Opens the Finalize Batch dialog — review/reorder/delete pages, choose export
    /// format, filename, destination, and whether to embed searchable OCR text, then export.
    /// Replaces the old standalone Export Batch button/format dropdown (see
    /// FinalizeBatchDialog/FinalizeBatchViewModel for the actual export logic, which subsumes
    /// what this method used to do directly).</summary>
    [RelayCommand]
    private async Task OpenFinalizeBatchAsync(Avalonia.Controls.Window? owner)
    {
        if (owner == null) return;
        if (_currentBatchId == null)
        {
            StatusText = "Start a batch first before finalizing.";
            return;
        }

        // AsNoTracking is required here, not optional: this same _dbContext instance has been
        // tracking every CaptureJob since it was first enqueued (see CaptureQueueService.
        // EnqueueCaptureAsync's _dbContext.CaptureJobs.Add), and the background worker updates
        // job status through its own separate AppDbContext/connection. A tracked Include query
        // returns the identity-mapped in-memory instances as-is — frozen at "Pending" from the
        // moment each job was created — never picking up the worker's writes. Without
        // AsNoTracking, this guard sees every job as permanently Pending and Finalize can never
        // proceed, no matter how long the operator waits.
        var batch = await _dbContext.Batches
            .AsNoTracking()
            .Include(b => b.Captures)
            .FirstOrDefaultAsync(b => b.Id == _currentBatchId);
        if (batch == null) return;

        // Previously this blocked opening the dialog at all while any page was Pending/
        // InProgress, showing "images are still processing" and returning — which is exactly
        // what made Finalize look like it did nothing, since the operator had no way to see
        // *when* processing actually finished short of retrying the click blind. The dialog
        // itself now polls (see FinalizeBatchViewModel's _refreshTimer) and shows the same
        // "still processing" state live, updating the moment pages complete — so it's always
        // safe to open, even with nothing completed yet.
        // The batch's own output/ folder — the finished export belongs with the finished pages,
        // which is where the operator looks for the batch's results. Falls back to the project
        // directory only for a batch that predates batch folders.
        var finalizeDirectory = !string.IsNullOrWhiteSpace(_currentBatchFolder)
            ? BatchFolder.OutputPath(_currentBatchFolder)
            : _outputDirectory;
        var result = await MicroCapture.UI.Views.FinalizeBatchDialog.RunAsync(owner, _dbContext, batch, finalizeDirectory);
        if (result == null) return;

        // Export changed batch.Status to "Exported" and deleted the originals it consumed, all
        // of which lives only in the local database until this runs. Without it a reopened batch
        // reads as still Active and reports every page's original as missing from disk.
        await PublishManifestAsync();

        // The batch can no longer be captured into until it is explicitly reopened — hide the
        // scan tile so it doesn't imply otherwise.
        _currentBatchStatus = "Exported";
        RefreshScanTile();

        StatusText = result.MissingOcrText
            ? $"Exported: {Path.GetFileName(result.ExportPath)} — no searchable text layer (Tesseract OCR unavailable or failed)."
            : $"Exported successfully: {Path.GetFileName(result.ExportPath)}";

        // Refresh OcrStatus on whatever thumbnails are currently visible, same as RunOcrForCurrentBatchAsync does.
        var refreshed = await _dbContext.CaptureJobs.AsNoTracking()
            .Where(j => j.BatchId == _currentBatchId)
            .ToDictionaryAsync(j => j.Id, j => j.OcrStatus);
        foreach (var thumbnail in RecentCaptures)
        {
            if (refreshed.TryGetValue(thumbnail.JobId, out var ocrStatus))
                thumbnail.OcrStatus = ocrStatus;
        }
    }

    /// <summary>Deletes every job's OriginalFilePath for a batch that just finished exporting
    /// successfully — never called on a failed or cancelled export (see ExportBatchAsync, which
    /// only reaches this after ExportBatchAsync's own export call returns without throwing).
    /// Originals are retained up to this point specifically so Crop Review can re-crop from the
    /// original at any time before final export; once export has produced its output, that
    /// capability is no longer needed for this batch. Each deletion is independently try/caught
    /// so one locked/missing file can't stop the rest from being cleaned up. Returns the number
    /// of files that could not be deleted, for the caller's status message.</summary>
    // ---------- Helpers ----------

    [RelayCommand]
    private void ReviewCrop(string jobId) => OpenCropReview(jobId, selectionForBulkApply: null);

    /// <summary>The filmstrip batch action bar's "Apply Adjustments to Selected" button — opens
    /// Crop Review on the first selected page in Adjust mode, with the rest of the selection
    /// passed through so its own Apply-to-Selection command knows the target set. Reuses the
    /// single-page adjust UI (with its live preview) to define the values, rather than a second
    /// "pick values blind" surface.</summary>
    [RelayCommand]
    private async Task ApplyAdjustmentsToSelectedAsync(Avalonia.Controls.Window? owner)
    {
        var selectedIds = RecentCaptures.Where(t => t.IsSelected).Select(t => t.JobId).Distinct().ToList();
        if (selectedIds.Count == 0)
        {
            StatusText = "Select the pages you want to adjust first.";
            return;
        }

        // Applying one page's settings across a selection overwrites whatever adjustments those
        // pages already had, and reprocesses every one of them. That is a lot to set in motion
        // from a single click, so say what will happen and to how many pages.
        if (owner != null)
        {
            var proceed = await MicroCapture.UI.Views.ConfirmDialog.AskAsync(owner,
                $"Adjustments you make will be applied to all {selectedIds.Count} selected page(s), " +
                "replacing any adjustments they already have. Every one of them will be reprocessed.\n\n" +
                "You can undo this afterwards from the cart.",
                "Adjust selected pages");
            if (!proceed) return;
        }

        OpenCropReview(selectedIds[0], selectionForBulkApply: selectedIds, openInAdjustMode: true);
    }

    /// <summary>The filmstrip batch action bar's "Delete Selected" button — confirms, then
    /// removes every selected capture the same way the per-thumbnail delete already does
    /// (mark-superseded via CaptureQueueService, not a hard delete), one at a time so each gets
    /// its existing derivative-cleanup/page-count/thumbnail-removal handling.</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync(Avalonia.Controls.Window? owner)
    {
        var selected = RecentCaptures.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0) return;

        if (owner != null)
        {
            var confirmed = await MicroCapture.UI.Views.ConfirmDialog.AskAsync(owner,
                $"Delete {selected.Count} selected page{(selected.Count == 1 ? "" : "s")}? This excludes them from processing and export.",
                "Delete Selected");
            if (!confirmed) return;
        }

        // One pass: purge every selected page and its files, THEN renumber once. Deleting them
        // one at a time renumbered the batch between each, moving the remaining selected items'
        // page numbers so the next delete targeted the wrong file (confirmed: the last page's
        // derivative was left on disk and the count stuck one above zero).
        await DeleteCapturesAsync(selected);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectAllLabel));
    }

    // Non-null while Crop Review is open — MainWindow.axaml hosts a CropReviewWindow view bound
    // to this, replacing the live camera view in place (same pattern as CalibrationViewModel/
    // LensCalibrationViewModel's own inline panels) instead of opening a separate popup window.
    [ObservableProperty] private CropReviewViewModel? _activeCropReview;

    private void OpenCropReview(string jobId, IReadOnlyList<string>? selectionForBulkApply, bool openInAdjustMode = false)
    {
        if (string.IsNullOrEmpty(jobId)) return;

        ActiveCropReview?.Dispose();

        // This window only ever offers Adjust mode now (manual crop-quad/split-line/dewarp-curve
        // editing removed), so openInAdjustMode no longer needs to do anything here.

        // Snapshot before the operator can change anything, so an edit can be undone from the
        // cart.
        //
        // Every page in the batch, not just the one being opened or the selection it was opened
        // for: "Apply to All in Batch" widens the edit to the whole batch after this window is
        // already open, and undo was previously recording only the page that had been clicked —
        // so undoing a 400-page bulk apply restored exactly one page and left the other 399
        // changed. The window reports back which pages it actually wrote, and the baseline has to
        // already cover them. It is one query of ten small fields per page, taken once.
        //
        // Taken synchronously and before the window opens. Doing it on a background task raced
        // the operator: a quick save could land before the snapshot existed, and the edit would
        // silently record nothing to undo.
        var baseline = SnapshotBatchAdjustments(jobId, selectionForBulkApply);

        // Constructed after the snapshot: both share this context, and the snapshot clears its
        // change tracker. Loading first meant clearing the tracker out from under a window that
        // had just populated it.
        var cropReviewViewModel = new CropReviewViewModel(jobId, _dbContext, _queueService, selectionForBulkApply);
        cropReviewViewModel.StatusReported += (_, message) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = message);

        cropReviewViewModel.Saved += (_, _) =>
        {
            // What was written, not what was selected. Drained each time, so an Apply to All
            // followed by a Save records as two undo steps covering their own real extents.
            var affectedIds = cropReviewViewModel.TakeAffectedJobIds().ToList();
            if (affectedIds.Count == 0) affectedIds = new List<string> { jobId };

            var beforeEdit = affectedIds
                .Where(baseline.ContainsKey)
                .Select(id => (object)baseline[id])
                .ToList();

            var description = affectedIds.Count > 1 ? $"adjustments on {affectedIds.Count} pages" : "page adjustment";
            _ = RecordAdjustmentEditAsync(description, affectedIds, beforeEdit);

            // Move the baseline forward for the pages just written, so a second edit in the same
            // session undoes back to the state before *it* rather than all the way to the state
            // before the window opened.
            foreach (var snapshot in ReadAdjustmentSnapshots(affectedIds))
                baseline[snapshot.JobId] = snapshot;

            // Show every affected page working, not just the one that was on screen. A bulk
            // apply that re-queues twelve pages while only one tile visibly changes reads as
            // having applied to one page — which is exactly how a working bulk apply gets
            // reported as broken.
            var affected = affectedIds.ToHashSet();
            foreach (var affectedTile in RecentCaptures.Where(t => affected.Contains(t.JobId)))
                affectedTile.Status = "Reprocessing…";

            var thumbnail = RecentCaptures.FirstOrDefault(t => t.JobId == jobId);
            if (thumbnail != null)
            {
                if (cropReviewViewModel.IsPostExportAdjustOnly)
                {
                    // No background worker will ever pick this job up (its original is gone —
                    // Save already wrote the edit straight to the derivative file, synchronously,
                    // in CropReviewViewModel.Save), so there is no later JobCompleted event to
                    // refresh the thumbnail the normal way. Re-decode right here instead of
                    // leaving the thumbnail stuck on a "Reprocessing…" status that will never
                    // resolve on its own.
                    thumbnail.Status = "Processed";
                    try
                    {
                        var bytes = MicroCapture.Processing.ImageDecodeHelper.GetDisplayBytes(cropReviewViewModel.ImagePath);
                        if (bytes != null)
                        {
                            using var stream = new MemoryStream(bytes);
                            var newThumb = Bitmap.DecodeToWidth(stream, 120);
                            var old = thumbnail.Thumbnail;
                            thumbnail.Thumbnail = newThumb;
                            old?.Dispose();

                            // Also refresh the persisted on-disk thumbnail (see AddThumbnail),
                            // so a later resume from Recent shows this edit too instead of the
                            // stale pre-edit version.
                            var thumbPath = ThumbnailFileFor(_activeBatchCode, thumbnail.PageNumber);
                            using var freshStream = new MemoryStream(bytes);
                            var freshThumb = Bitmap.DecodeToWidth(freshStream, 120);
                            freshThumb.Save(thumbPath);
                            freshThumb.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Post-export thumbnail refresh failed: {ex}");
                    }
                }
                else
                {
                    // Give the thumbnail immediate feedback on save instead of leaving it looking
                    // unchanged for the ~1s the background worker takes to actually pick the job
                    // back up.
                    thumbnail.Status = "Reprocessing…";
                }
            }
            ClearSelection();
        };
        cropReviewViewModel.ReviewClosed += (_, _) => CloseCropReview();

        ActiveCropReview = cropReviewViewModel;
    }

    private void CloseCropReview()
    {
        var current = ActiveCropReview;
        ActiveCropReview = null;
        current?.Dispose();
    }

    /// <summary>Number of pages one Recapture is expected to supersede/recreate for the current
    /// batch's mode. Fixed frames are handled directly in CaptureAsync/RecaptureAsync (each
    /// frame is its own independent CaptureJob — see those methods' own comments); this helper
    /// now only matters for split-book-pages recapture, where one job still legitimately produces
    /// 2 output files (left/right half of one spread) under a single page number.</summary>
    private int GetCurrentFixedFrameCount()
    {
        if (Frames.Count > 0) return Frames.Count;
        if (SplitBookPages) return 2;
        return 1;
    }

    // ───────────── FIXED FRAME EDITING ─────────────

    // Coalesces a burst of drag-ends into one write. Frames change continuously now, so the
    // per-pointer-move rate must never reach the database; a discrete add/remove skips this and
    // persists immediately (see PersistFramesNow).
    private Avalonia.Threading.DispatcherTimer? _framePersistTimer;
    private static readonly TimeSpan FramePersistDebounce = TimeSpan.FromMilliseconds(300);

    // Releases the auto-capture suspension a moment after the operator stops dragging, so a shot
    // isn't fired by the residual motion of letting go.
    private Avalonia.Threading.DispatcherTimer? _frameEditSettleTimer;
    private static readonly TimeSpan FrameEditSettleDelay = TimeSpan.FromMilliseconds(250);

    // Guards the mutual exclusion between Frames and SplitBookPages from recursing.
    private bool _suppressFrameSplitSync;

    /// <summary>Wires up the collection so any structural change keeps derived state honest.
    /// Called once from the constructor.</summary>
    private void InitializeFrameTracking()
    {
        Frames.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsFrameMode));
            OnPropertyChanged(nameof(FrameSummary));
            RebuildFrameList();
            RemoveSelectedFrameCommand.NotifyCanExecuteChanged();
            MoveFrameUpCommand.NotifyCanExecuteChanged();
            MoveFrameDownCommand.NotifyCanExecuteChanged();
            ClearAllFramesCommand.NotifyCanExecuteChanged();

            if (Frames.Count > 0 && SplitBookPages && !_suppressFrameSplitSync)
            {
                _suppressFrameSplitSync = true;
                try { SplitBookPages = false; }
                finally { _suppressFrameSplitSync = false; }
            }

            // Geometry changed, so whatever was last captured is no longer comparable against
            // what the frames now see — force the next auto-capture evaluation to start fresh
            // rather than treating the new layout as "same page already shot".
            _lastCapturedSignature = null;
            _stableFrameCount = 0;

            // The scan tile's "Next: N pages (frames)" badge tracks Frames.Count.
            RefreshScanTile();
        };
    }

    partial void OnSelectedFrameIndexChanged(int value)
    {
        RemoveSelectedFrameCommand.NotifyCanExecuteChanged();
        MoveFrameUpCommand.NotifyCanExecuteChanged();
        MoveFrameDownCommand.NotifyCanExecuteChanged();
        for (var i = 0; i < FrameList.Count; i++) FrameList[i].IsSelected = i == value;
    }

    public string FrameSummary => Frames.Count == 0
        ? "No frames — auto-detect crop"
        : Frames.Count == 1 ? "1 frame — 1 page per capture"
        : $"{Frames.Count} frames — {Frames.Count} pages per capture";

    /// <summary>Display projection of <see cref="Frames"/> that spells out the order-to-filename
    /// mapping, so the operator can see which region becomes which page before shooting. Rebuilt
    /// on any change rather than kept in sync incrementally — the list is a handful of items and
    /// only changes on a discrete edit.</summary>
    public ObservableCollection<FrameListItem> FrameList { get; } = new();

    private void RebuildFrameList()
    {
        FrameList.Clear();
        for (var i = 0; i < Frames.Count; i++)
        {
            FrameList.Add(new FrameListItem
            {
                Label = $"Frame {i + 1} → page {i + 1}  ({Math.Round(Frames[i].Width)}×{Math.Round(Frames[i].Height)})",
                Color = new Avalonia.Media.SolidColorBrush(FixedFrameColorPalette.GetColor(i)),
                IsSelected = i == SelectedFrameIndex
            });
        }
    }

    private bool CanEditSelectedFrame() => AreFrameEditsAllowed && SelectedFrameIndex >= 0 && SelectedFrameIndex < Frames.Count;
    private bool CanClearFrames() => AreFrameEditsAllowed && Frames.Count > 0;

    partial void OnAreFrameEditsAllowedChanged(bool value)
    {
        AddFrameCommand.NotifyCanExecuteChanged();
        RemoveSelectedFrameCommand.NotifyCanExecuteChanged();
        MoveFrameUpCommand.NotifyCanExecuteChanged();
        MoveFrameDownCommand.NotifyCanExecuteChanged();
        ClearAllFramesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Adopts the live feed's dimensions as the space frames are authored in, the first
    /// time a frame is created with no reference already established. A batch calibrated the old
    /// way keeps its original full-resolution reference so its frames don't jump on first edit.</summary>
    private void EnsureFrameReference()
    {
        if (FrameReferenceWidth > 0 && FrameReferenceHeight > 0) return;
        var live = LiveViewImage;
        if (live == null) return;
        FrameReferenceWidth = (int)Math.Round(live.Size.Width);
        FrameReferenceHeight = (int)Math.Round(live.Size.Height);
        OnPropertyChanged(nameof(FrameReferenceWidth));
        OnPropertyChanged(nameof(FrameReferenceHeight));
        OnPropertyChanged(nameof(FrameReferenceSize));
    }

    /// <summary>The space the overlay editor works in. Normally the established reference — the
    /// live feed's size for frames drawn here, or a resumed batch's own calibration resolution —
    /// but before any reference exists it falls back to the live feed's current size, so the very
    /// first frame can be drawn against something real. EnsureFrameReference then pins that
    /// choice as soon as a frame is committed.</summary>
    public Avalonia.Size FrameReferenceSize
    {
        get
        {
            if (FrameReferenceWidth > 0 && FrameReferenceHeight > 0)
                return new Avalonia.Size(FrameReferenceWidth, FrameReferenceHeight);
            var live = LiveViewImage;
            return live != null ? live.Size : default;
        }
    }

    partial void OnLiveViewImageChanged(Bitmap? value)
    {
        // Until a reference is pinned, the editor's coordinate space follows the live feed.
        if (FrameReferenceWidth <= 0 || FrameReferenceHeight <= 0)
            OnPropertyChanged(nameof(FrameReferenceSize));

        // This fires once per live frame — take the cheap path (just hand the bitmap over)
        // rather than a full RefreshScanTile, which does clamping and partition work that
        // nothing here changed.
        ScanTile.LivePreview = value;
    }

    partial void OnIsLiveViewActiveChanged(bool value)
    {
        ScanTile.IsLiveActive = value;
    }

    partial void OnPageCountChanged(int value)
    {
        // The append target is PageCount + 1, so the tile's label follows the tail.
        RefreshScanTile();
    }

    /// <summary>Full-resolution capture dimensions, learned from the first capture of the
    /// session, so the overlay can show a frame's size in the pixels the operator will actually
    /// get on disk rather than live-preview pixels. Zero until something has been captured, which
    /// the editor treats as "no readout available".</summary>
    public Avalonia.Size CaptureImageSize { get; private set; }

    /// <summary>Records the capture resolution and warns once if it disagrees in ASPECT with the
    /// live feed. Frames are authored against the feed and projected onto the capture by
    /// independent x and y scales, which is exactly right when the two share a field of view —
    /// but if the feed is letterboxed or cropped differently from the capture, that projection
    /// stretches the frames and the operator needs to know rather than discovering it in the
    /// output. Real Canon bodies stream and shoot at the same aspect; the mock deliberately
    /// does not, which is what exercises this path.</summary>
    private void NoteCaptureDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (Math.Abs(CaptureImageSize.Width - width) < 0.5 && Math.Abs(CaptureImageSize.Height - height) < 0.5) return;

        CaptureImageSize = new Avalonia.Size(width, height);
        OnPropertyChanged(nameof(CaptureImageSize));

        if (_aspectMismatchWarned || FrameReferenceWidth <= 0 || FrameReferenceHeight <= 0) return;
        var referenceAspect = (double)FrameReferenceWidth / FrameReferenceHeight;
        var captureAspect = (double)width / height;
        if (Math.Abs(referenceAspect - captureAspect) > 0.02)
        {
            _aspectMismatchWarned = true;
            StatusText = $"Note: live view ({FrameReferenceWidth}x{FrameReferenceHeight}) and capture ({width}x{height}) have different aspect ratios — frames will be stretched to fit. Check a captured page before shooting the batch.";
        }
    }

    private bool _aspectMismatchWarned;

    [RelayCommand(CanExecute = nameof(CanAddFrame))]
    private void AddFrame()
    {
        EnsureFrameReference();
        if (FrameReferenceWidth <= 0 || FrameReferenceHeight <= 0)
        {
            StatusText = "Connect the camera and wait for live view before adding frames.";
            return;
        }
        Frames.Add(Controls.FrameGeometry.DefaultFrame(FrameReferenceWidth, FrameReferenceHeight, Frames.Count));
        SelectedFrameIndex = Frames.Count - 1;
        PersistFramesNow();
    }

    private bool CanAddFrame() => AreFrameEditsAllowed;

    [RelayCommand(CanExecute = nameof(CanEditSelectedFrame))]
    private void RemoveSelectedFrame()
    {
        var index = SelectedFrameIndex;
        if (index < 0 || index >= Frames.Count) return;
        Frames.RemoveAt(index);
        SelectedFrameIndex = Frames.Count > 0 ? Math.Min(index, Frames.Count - 1) : -1;
        PersistFramesNow();
    }

    [RelayCommand(CanExecute = nameof(CanMoveFrameUp))]
    private void MoveFrameUp()
    {
        var index = SelectedFrameIndex;
        if (index <= 0 || index >= Frames.Count) return;
        Frames.Move(index, index - 1);
        SelectedFrameIndex = index - 1;
        PersistFramesNow();
    }

    private bool CanMoveFrameUp() => AreFrameEditsAllowed && SelectedFrameIndex > 0 && SelectedFrameIndex < Frames.Count;

    [RelayCommand(CanExecute = nameof(CanMoveFrameDown))]
    private void MoveFrameDown()
    {
        var index = SelectedFrameIndex;
        if (index < 0 || index >= Frames.Count - 1) return;
        Frames.Move(index, index + 1);
        SelectedFrameIndex = index + 1;
        PersistFramesNow();
    }

    private bool CanMoveFrameDown() => AreFrameEditsAllowed && SelectedFrameIndex >= 0 && SelectedFrameIndex < Frames.Count - 1;

    [RelayCommand(CanExecute = nameof(CanClearFrames))]
    private void ClearAllFrames()
    {
        if (Frames.Count == 0) return;
        Frames.Clear();
        SelectedFrameIndex = -1;
        PersistFramesNow();
    }

    /// <summary>Called by the overlay editor when a drag or a structural edit completes.
    /// Transforms debounce (another drag usually follows); structural edits persist at once.</summary>
    public void OnFrameEditCommitted(Controls.FrameEditKind kind)
    {
        EnsureFrameReference();
        OnPropertyChanged(nameof(FrameSummary));
        // A resize changes the rect in place without touching the collection, so the size
        // readouts in the list need refreshing explicitly.
        RebuildFrameList();
        if (kind == Controls.FrameEditKind.Structural) PersistFramesNow();
        else ScheduleFramePersist();
    }

    /// <summary>Called by the overlay editor on pointer-down and pointer-up. Auto-capture stays
    /// suspended for a short settle after release so letting go of a frame can't trip the shutter.</summary>
    public void OnFrameInteractionChanged(bool interacting)
    {
        if (interacting)
        {
            _frameEditSettleTimer?.Stop();
            IsEditingFrames = true;
            UpdateCaptureReadiness();
            return;
        }

        _frameEditSettleTimer ??= CreateOneShotTimer(FrameEditSettleDelay, () =>
        {
            IsEditingFrames = false;
            UpdateCaptureReadiness();
        });
        _frameEditSettleTimer.Stop();
        _frameEditSettleTimer.Start();
    }

    private static Avalonia.Threading.DispatcherTimer CreateOneShotTimer(TimeSpan interval, Action onTick)
    {
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = interval };
        timer.Tick += (s, _) =>
        {
            ((Avalonia.Threading.DispatcherTimer)s!).Stop();
            onTick();
        };
        return timer;
    }

    private void ScheduleFramePersist()
    {
        if (_currentBatchId == null || _suppressPersist) return;
        _framePersistTimer ??= CreateOneShotTimer(FramePersistDebounce, PersistFramesNow);
        _framePersistTimer.Stop();
        _framePersistTimer.Start();
    }

    /// <summary>Writes the current frames onto the active batch immediately. A no-op before any
    /// batch is started — frames drawn then are staged in memory and persisted by StartBatchAsync.</summary>
    private void PersistFramesNow()
    {
        _framePersistTimer?.Stop();
        if (_currentBatchId == null || _suppressPersist) return;

        // Snapshot before the lambda: PersistBatchSettingAsync is async void, so its body runs on
        // a later turn, by which time the operator may already be dragging these frames again.
        var count = Frames.Count;
        var spec = count > 0 ? MicroCapture.Processing.ImageProcessor.FormatFixedFrames(Frames) : null;
        var refW = count > 0 ? FrameReferenceWidth : 0;
        var refH = count > 0 ? FrameReferenceHeight : 0;

        PersistBatchSettingAsync(b =>
        {
            b.UseFixedFrames = count > 0;
            b.FixedFrames = spec;
            b.FixedFrameImageWidth = refW;
            b.FixedFrameImageHeight = refH;
            if (count > 0) b.SplitBookPages = false;
        });
    }

    /// <summary>Loads a batch's saved frames into the live editor, keeping the batch's own
    /// reference space so frames authored against a full-resolution calibration still render and
    /// re-persist consistently.</summary>
    private void HydrateFramesFromBatch(Batch? batch)
    {
        Frames.Clear();
        if (batch != null && batch.UseFixedFrames && !string.IsNullOrWhiteSpace(batch.FixedFrames))
        {
            foreach (var f in MicroCapture.Processing.ImageProcessor.ParseFixedFrames(batch.FixedFrames))
                Frames.Add(f);
            FrameReferenceWidth = batch.FixedFrameImageWidth;
            FrameReferenceHeight = batch.FixedFrameImageHeight;
        }
        else
        {
            FrameReferenceWidth = 0;
            FrameReferenceHeight = 0;
        }
        SelectedFrameIndex = Frames.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(FrameReferenceWidth));
        OnPropertyChanged(nameof(FrameReferenceHeight));
        OnPropertyChanged(nameof(FrameReferenceSize));
        OnPropertyChanged(nameof(FrameSummary));
        RebuildFrameList();
    }

    /// <summary>Re-evaluates whether frame geometry may be edited right now. Editing while a job
    /// is still queued would crop it with geometry it wasn't shot under, so editing is locked up
    /// front — as a disabled state the operator can see — rather than by rejecting a drag after
    /// the fact. A query failure must never lock the operator out.</summary>
    private async Task RefreshFrameEditPermissionAsync()
    {
        if (_currentBatchId == null) { AreFrameEditsAllowed = true; return; }
        try
        {
            var pending = await _dbContext.CaptureJobs.CountAsync(j =>
                j.BatchId == _currentBatchId &&
                (j.ProcessingStatus == "Pending" || j.ProcessingStatus == "InProgress"));
            var allowed = pending == 0;
            if (allowed != AreFrameEditsAllowed)
            {
                AreFrameEditsAllowed = allowed;
                if (!allowed)
                    StatusText = $"{pending} page(s) still processing under the current frames — frame editing is locked until they finish.";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RefreshFrameEditPermissionAsync] {ex}");
            AreFrameEditsAllowed = true;
        }
    }

    private void AddThumbnail(string jobId, string filePath, int pageNumber, bool isRecapture = false, int? frameIndex = null, (int X, int Y, int Width, int Height)? cropRect = null)
    {
        // frameIndex identifies which single fixed frame this job/thumbnail is for (each frame
        // is now its own independent CaptureJob — see CaptureAsync); null means an ordinary
        // auto-detect capture with no frame concept, i.e. exactly one thumbnail for the job.
        var thumbnail = new ThumbnailItem
        {
            JobId = jobId,
            PageNumber = pageNumber,
            FrameIndex = frameIndex ?? 0,
            BorderColor = frameIndex is { } fi ? new Avalonia.Media.SolidColorBrush(FixedFrameColorPalette.GetColor(fi)) : null,
            Status = isRecapture ? "Recapturing" : "Processing",
            FilePath = filePath
        };
        // Insert the placeholder row synchronously, on this (UI) thread, before returning — NOT
        // via Dispatcher.UIThread.Post. AddThumbnail is called right after EnqueueCaptureAsync
        // writes the job to the DB as "Pending", and BackgroundProcessingWorker's poll loop can
        // pick that job up and finish it within milliseconds. JobCompleted's handler (below)
        // matches on RecentCaptures.Where(t => t.JobId == result matching job) to know which
        // thumbnail to update — if that handler's own Dispatcher.Post ran before this method's
        // deferred Post got its turn, the match found nothing and the "Processing" status update
        // was silently lost forever, with no later event to correct it (confirmed root cause of
        // thumbnails that never advance past "Processing"). Inserting the row here, before this
        // method returns, guarantees it already exists by the time any worker callback for this
        // job can possibly run.
        // The cart is in page order, so a page normally belongs at the end — but an inserted page
        // belongs where its number puts it, or the strip would disagree with the page numbers
        // printed on its own tiles.
        var insertIndex = RecentCaptures.Count;
        for (var i = 0; i < RecentCaptures.Count; i++)
        {
            if (RecentCaptures[i].PageNumber <= pageNumber) continue;
            insertIndex = i;
            break;
        }
        RecentCaptures.Insert(insertIndex, thumbnail);
        if (insertIndex == RecentCaptures.Count - 1) CartAppended?.Invoke(this, EventArgs.Empty);
        var placeholders = new List<ThumbnailItem> { thumbnail };

        // Trim from the FRONT. The cart is in page order and new pages are appended, so the
        // oldest page is index 0 — this used to take index ^1 back when captures were inserted
        // at the front, and flipping the order left it removing the tile that had just been
        // added. Past 100 pages every new capture vanished the instant it appeared.
        while (RecentCaptures.Count > MaxCartThumbnails)
        {
            var oldest = RecentCaptures[0];
            oldest.Thumbnail?.Dispose();
            RecentCaptures.RemoveAt(0);
        }

        // Decoding the capture into a small preview bitmap can take a moment on a large TIFF —
        // do that off the UI thread and fill it in once ready, without delaying the placeholder
        // insertion above.
        Task.Run(() =>
        {
            try
            {
                // Learn the rig's true capture resolution from a real shot, so the frame overlay
                // can report sizes in output pixels rather than live-preview pixels — and so an
                // aspect mismatch between feed and capture gets flagged rather than silently
                // stretching every frame.
                if (MicroCapture.Processing.ImageDecodeHelper.GetPixelSize(filePath) is var (capW, capH))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => NoteCaptureDimensions(capW, capH));
                }

                // A fixed-frame job's thumbnail must show only that frame's own region — several
                // sibling jobs share this same source file, so decoding the whole thing here
                // would show every frame the same full-spread image (confirmed operator-visible
                // bug). Route through the OpenCV-backed cropper for those; plain auto-detect
                // captures keep the direct file-bytes decode.
                var bytes = cropRect is { } r
                    ? MicroCapture.Processing.ImageDecodeHelper.GetCroppedDisplayBytes(filePath, r.X, r.Y, r.Width, r.Height)
                    : File.ReadAllBytes(filePath);
                if (bytes == null)
                {
                    Console.Error.WriteLine(cropRect is { } rr
                        ? $"Thumbnail crop failed for '{filePath}' rect=({rr.X},{rr.Y},{rr.Width},{rr.Height}) — file missing/undecodable or rect fully outside image bounds."
                        : $"Thumbnail decode failed for '{filePath}' — file missing or undecodable.");
                    return;
                }

                foreach (var thumbnail in placeholders)
                {
                    using var stream = new MemoryStream(bytes);
                    var thumb = Bitmap.DecodeToWidth(stream, 120);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var old = thumbnail.Thumbnail;
                        thumbnail.Thumbnail = thumb;
                        old?.Dispose();
                    });

                    // Persist this page's thumbnail to disk, independent of the original
                    // capture file's own lifetime (BackgroundProcessingWorker deletes it once
                    // processing succeeds) — so LoadRecentCapturesFromBatchAsync can still show
                    // a thumbnail for this page on a later resume, even after that deletion.
                    try
                    {
                        var thumbPath = ThumbnailFileFor(_activeBatchCode, pageNumber);
                        Directory.CreateDirectory(ThumbnailDirectoryFor(_activeBatchCode));
                        thumb.Save(thumbPath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Could not persist thumbnail for page {pageNumber}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Thumbnail generation failed for '{filePath}': {ex}");
            }
        });
    }

    /// <summary>
    /// Permanently removes one or more captured pages from the batch. The confirm dialog tells
    /// the operator this cannot be undone, so it is a hard delete on every level: the database
    /// rows for each page and its whole recapture history are removed (not marked Superseded),
    /// the original capture file and every processed derivative and cached thumbnail are deleted
    /// from the batch folder, and the remaining pages are renumbered ONCE to a gap-free 1..N run
    /// so both the per-tile page numbers and the PAGE count stay correct. The manifest is
    /// republished so another machine opening the folder sees the same result.
    /// Called from MainWindow.axaml.cs's per-thumbnail delete button.
    /// </summary>
    public Task DeleteCaptureAsync(ThumbnailItem item) => DeleteCapturesAsync(new[] { item });

    /// <summary>Deletes a set of pages in one pass: every page's rows and files are removed
    /// first, then the batch is renumbered a single time. Renumbering per-page inside a loop was
    /// the bug behind "one image won't delete" — each renumber mutated the still-pending items'
    /// PageNumber out from under the next iteration, so the last file was targeted by a page
    /// number that no longer matched it.</summary>
    public async Task DeleteCapturesAsync(IReadOnlyList<ThumbnailItem> items)
    {
        if (items.Count == 0) return;

        // De-dupe by JobId — a fixed-frame page is one job with potentially several sibling
        // thumbnails, and Select All can hand us all of them.
        var jobIds = items.Select(i => i.JobId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var lastPageNumber = items.Count > 0 ? items.Max(i => i.PageNumber) : 0;

        var removedJobs = new List<MicroCapture.Core.Models.CaptureJob>();
        foreach (var jobId in jobIds)
            removedJobs.AddRange(await _queueService.PurgeCaptureAsync(jobId));

        // Which originals the removed jobs referenced. A fixed-frame shot produces several jobs
        // from ONE source image, so that original is only deleted once no SURVIVING job points
        // at it. A recapture attempt has its own "_R_{timestamp}" original, referenced by
        // exactly one now-removed job.
        var removedOriginals = removedJobs
            .Select(j => j.OriginalFilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stillReferencedOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentBatchId != null)
        {
            _dbContext.ChangeTracker.Clear();
            foreach (var path in await _dbContext.CaptureJobs
                         .Where(j => j.BatchId == _currentBatchId)
                         .Select(j => j.OriginalFilePath)
                         .ToListAsync())
            {
                if (!string.IsNullOrWhiteSpace(path)) stillReferencedOriginals.Add(Path.GetFullPath(path));
            }
        }

        // Processed derivatives: resolve them the same way export does — the job's recorded
        // ProcessedFilePath first (the authoritative answer, and how fixed-frame outputs like
        // "..._p000002.tif" in output/ are found — their names don't follow the base-name +
        // known-suffix shape EnumerateDerivatives matches), folder globs as a fallback for
        // older rows. Also clear the OCR sidecars beside each derivative.
        foreach (var job in removedJobs)
        {
            try
            {
                foreach (var derivative in MicroCapture.Processing.BatchExportService.GetProcessedFilesForJob(job))
                {
                    if (string.Equals(Path.GetFullPath(derivative), Path.GetFullPath(job.OriginalFilePath), StringComparison.OrdinalIgnoreCase))
                        continue; // the original is handled separately below
                    TryDeleteFile(derivative);
                    TryDeleteFile(MicroCapture.Processing.ProcessedFilePaths.OcrSidecarPath(derivative, ".txt"));
                    TryDeleteFile(MicroCapture.Processing.ProcessedFilePaths.OcrSidecarPath(derivative, ".tsv"));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Processed-file cleanup failed for job '{job.Id}': {ex}");
            }
        }

        // Originals: delete each one no surviving job still needs.
        foreach (var original in removedOriginals)
        {
            if (!stillReferencedOriginals.Contains(original))
                TryDeleteFile(original);
        }

        // Cached page thumbnails, by the page numbers the deleted items held BEFORE any
        // renumber (nothing has renumbered yet — that happens once, below).
        foreach (var pageNumber in items.Select(i => i.PageNumber).Distinct())
            TryDeleteFile(ThumbnailFileFor(_activeBatchCode, pageNumber));

        // Drop every deleted job's thumbnail(s) from the strip.
        var removedJobIds = jobIds.ToHashSet();
        foreach (var sibling in RecentCaptures.Where(t => removedJobIds.Contains(t.JobId)).ToList())
        {
            sibling.Thumbnail?.Dispose();
            RecentCaptures.Remove(sibling);
        }

        // Renumber ONCE, after everything is gone: remaining pages become a gap-free 1..N
        // matching the cart order, their thumbnail files are renamed, PageCount is recomputed.
        try
        {
            await RenumberBatchSequentiallyAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Renumber after delete failed: {ex}");
            PageCount = RecentCaptures.Count; // best-effort so the header isn't left stale
        }

        await PublishManifestAsync();

        // Deleting pages renumbered the batch — an insert point that pointed past the deleted
        // range is now stale. RefreshScanTile clamps it back into [1, PageCount+1].
        RefreshScanTile();

        StatusText = jobIds.Count == 1
            ? $"Page {lastPageNumber:D6} removed."
            : $"{jobIds.Count} pages removed.";
    }

    /// <summary>Best-effort file delete for capture cleanup — a missing file or a lock must never
    /// abort the surrounding delete/renumber, which the database rows have already committed.</summary>
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort; the DB rows are what actually removed the page */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>
    /// Called from MainWindow.axaml.cs when keyboard shortcuts are pressed.
    /// </summary>
    /// <summary>The key a foot pedal sends, once learned (see <see cref="TryHandleFootPedalKey"/>).
    /// Held here as well as in preferences so a match check doesn't hit disk.</summary>
    private string? _footPedalGesture = null;
    private bool _footPedalLoaded;

    /// <summary>Handles a key the fixed shortcut table didn't claim, so a single-key USB foot
    /// pedal works without the operator ever running the pedal vendor's config app.
    ///
    /// <para>If a pedal key is already learned and this is it, fire the shutter. Otherwise — the
    /// first unclaimed key seen while a batch is open and the live view is up — adopt it as the
    /// pedal and fire the shutter that same press, then remember it for good. Nav/modifier/menu
    /// keys are never adopted. Returns true if the press was consumed.</para></summary>
    public bool TryHandleFootPedalKey(string gesture, bool isBareModifierOrNav)
    {
        if (!_footPedalLoaded)
        {
            _footPedalGesture = string.IsNullOrWhiteSpace(_preferences.FootPedalKey) ? null : _preferences.FootPedalKey;
            _footPedalLoaded = true;
        }

        // TEMP diagnostic (status bar — WinExe has no console).
        StatusText = $"[Pedal] gesture='{gesture}' bareNav={isBareModifierOrNav} learned='{_footPedalGesture ?? "<none>"}' liveView={IsShowingLiveView} batch={_currentBatchId != null} canCapture={CaptureCommand.CanExecute(null)}";

        // The pedal is the shutter — same gate as Space.
        if (!IsShowingLiveView || _currentBatchId == null) return false;

        if (_footPedalGesture != null)
        {
            if (!string.Equals(gesture, _footPedalGesture, StringComparison.OrdinalIgnoreCase)) return false;
            if (CaptureCommand.CanExecute(null)) CaptureCommand.Execute(null);
            return true;
        }

        // Not learned yet — adopt this key, unless it's the kind of key that is never a pedal.
        if (isBareModifierOrNav) return false;

        _footPedalGesture = gesture;
        _preferences.FootPedalKey = gesture;
        _preferences.Save();
        StatusText = $"Foot pedal set to '{gesture}'. It now works like the spacebar for capture.";
        if (CaptureCommand.CanExecute(null)) CaptureCommand.Execute(null);
        return true;
    }

    public void HandleKeyShortcut(string key)
    {
        switch (key)
        {
            case "Space":
                // Space is the shutter — including a USB foot pedal that sends Space. It only
                // acts while the live camera view is the surface on top; with Crop Review or
                // calibration open it does nothing (CaptureAsync would reject it anyway, but
                // bailing here keeps a stray pedal press from posting a rejection message over
                // whatever the operator is doing in that panel).
                if (!IsShowingLiveView) break;
                if (CaptureCommand.CanExecute(null)) CaptureCommand.Execute(null);
                break;
            case "R":
                if (RecaptureCommand.CanExecute(null)) RecaptureCommand.Execute(null);
                break;
            case "A":
                ToggleAutoCapture();
                break;
            case "Delete":
                if (RemoveSelectedFrameCommand.CanExecute(null)) RemoveSelectedFrameCommand.Execute(null);
                break;
        }
    }

    public async Task ShutdownAsync()
    {
        // Give up the batch lock on the way out, so the next machine to open this folder isn't
        // told it's in use by a session that ended.
        _batchLockHeartbeat?.Stop();
        ReleaseCurrentBatchLock();

        // Flush any drag-end still sitting on the debounce, so frames adjusted moments before
        // closing aren't lost.
        PersistFramesNow();
        _worker?.Stop();
        try
        {
            await _cameraService.StopLiveViewAsync();
            await _cameraService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Shutdown warning: {ex.Message}";
        }
        finally
        {
            _cameraService.Dispose();
            _dbContext.Dispose();
        }
    }
}

/// <summary>
/// Represents one item in the thumbnail strip.
/// </summary>
/// <summary>One row of the FRAMES list: which drawn frame becomes which output page, in the
/// frame's own overlay color, so the order-to-filename mapping is visible without having to
/// remember the drawing order.</summary>
public partial class FrameListItem : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private Avalonia.Media.IBrush? _color;
    [ObservableProperty] private bool _isSelected;
}

public partial class ThumbnailItem : ObservableObject
{
    [ObservableProperty] private int _pageNumber;
    [ObservableProperty] private string _jobId = "";
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private string _status = "Captured";
    // OCR is on-demand (Run OCR / before a PDF export), not automatic on capture, so this is
    // tracked independently of Status rather than folded into it.
    [ObservableProperty] private string _ocrStatus = "Pending";
    [ObservableProperty] private string _filePath = "";

    /// <summary>The page the arrow keys are currently on. Deliberately separate from IsSelected:
    /// browsing through pages to look at them is not the same act as selecting pages to run a
    /// batch action over, and conflating the two made stepping through a batch silently build up
    /// a selection.</summary>
    [ObservableProperty] private bool _isCurrent;

    // Which fixed frame this thumbnail represents within its capture (0 for an ordinary,
    // non-fixed-frame capture — always exactly one thumbnail per job in that case).
    [ObservableProperty] private int _frameIndex;
    // Non-null only for fixed-frame captures — colors the thumbnail's border to match its
    // on-canvas frame. Null for ordinary captures, which keep the default neutral border.
    [ObservableProperty] private Avalonia.Media.IBrush? _borderColor;

    // Multi-select state for the batch action bar (Delete Selected / Apply Adjustments to
    // Selected) — toggled via ctrl/shift-click, independent of the plain-click "open Crop
    // Review" action.
    [ObservableProperty] private bool _isSelected;

    /// <summary>True for the first page rendered immediately after the inline scan tile. The
    /// leading "+" on that tile would set the insert point to where the scan tile already is,
    /// so the template hides it. Set by <c>RebuildStripPartition</c>.</summary>
    [ObservableProperty] private bool _isFirstAfterScanTile;
}

public partial class CameraControlItem : ObservableObject
{
    private readonly ICameraService _cameraService;
    private readonly Action<string> _report;
    private readonly SemaphoreSlim _settingLock = new(1, 1);
    [ObservableProperty] private bool _isBusy;
    public string Key { get; }
    public string DisplayName { get; }
    public IReadOnlyList<CameraSettingOption> Options { get; }
    [ObservableProperty] private CameraSettingOption? _selectedOption;

    public CameraControlItem(CameraSetting setting, ICameraService cameraService, Action<string> report)
    {
        Key = setting.Key;
        DisplayName = setting.DisplayName;
        Options = setting.Options;
        _cameraService = cameraService;
        _report = report;
        _selectedOption = Options.FirstOrDefault(option => option.Value == setting.Value) ?? Options.FirstOrDefault();
    }

    partial void OnSelectedOptionChanged(CameraSettingOption? value)
    {
        if (value == null) return;
        _ = ApplyAsync(value);
    }

    private async Task ApplyAsync(CameraSettingOption option)
    {
        if (IsBusy)
            return;

        await _settingLock.WaitAsync();

        try
        {
            IsBusy = true;

        await _cameraService.StopLiveViewAsync();

        await _cameraService.SetCameraSettingAsync(Key, option.Value);

        await Task.Delay(150);

        await _cameraService.StartLiveViewAsync();   
            _report($"Camera setting updated: {DisplayName} = {option.DisplayName}");
        }
        catch (Exception ex)
        {
            _report($"Could not update {DisplayName}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _settingLock.Release();
        }
    }
}
