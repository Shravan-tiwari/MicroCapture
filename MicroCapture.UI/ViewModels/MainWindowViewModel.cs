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
using MicroCapture.UI.Views;

namespace MicroCapture.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICameraService _cameraService;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly MicroCapture.Processing.BackgroundProcessingWorker? _worker;

    // --- State ---
    [ObservableProperty] private string _statusText = "Ready — Connect camera to begin";
    [ObservableProperty] private Bitmap? _liveViewImage;
    [ObservableProperty] private bool _isConnected;
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
    public int[] AvailableDpiOptions { get; } = { 150, 200, 300, 400, 600, 800, 1200 };

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
    public string[] AvailableCaptureFormats { get; } = { "TIFF", "JPG", "PNG" };

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
    private const int StableFramesRequired = 3; // ~1.5s at the existing 500ms check interval
    private const double LiveSharpnessThreshold = 40.0; // live-view frames are lower detail than a full capture, so a lower bar than the QC BlurThreshold (100)
    private const double StablePositionToleranceFraction = 0.03; // allowed drift between checks, as a fraction of frame size
    private const double ContentChangeThreshold = 18.0; // mean abs pixel difference (0-255) considered a genuinely different page
    private const double PositionSmoothingFactor = 0.35; // weight toward each new detection when updating the smoothed reference
    private int _stableFrameCount;
    // A smoothed (not raw) reference position: comparing each new detection against this
    // instead of the previous raw frame absorbs small per-frame jitter (hand tremor, minor
    // auto-exposure/focus hunting) without resetting stability progress, while a genuine
    // page swap still diverges from it quickly and resets normally.
    private (double X, double Y, double Width, double Height)? _smoothedRect;
    private byte[]? _lastDetectedSignature;
    private byte[]? _lastCapturedSignature;

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
    public void ToggleThumbnailSelection(ThumbnailItem item)
    {
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    public void ClearSelection()
    {
        foreach (var t in RecentCaptures) t.IsSelected = false;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
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
        _dbContext = dbPath == null ? new AppDbContext() : new AppDbContext(dbPath);
        _queueService = new CaptureQueueService(_dbContext);

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
            DocumentStatus = "Frames set — waiting for live view";
            return;
        }

        var signature = ConcatSignatures(check.Regions);
        var previous = _lastDetectedSignature;
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
            DocumentStatus = "✓ Frames set — focusing…";
            return;
        }

        var settled = previous != null && ContentDifference(signature, previous) < ContentChangeThreshold;
        _stableFrameCount = settled ? Math.Min(_stableFrameCount + 1, StableFramesRequired) : 1;
        if (_stableFrameCount < StableFramesRequired)
        {
            DocumentStatus = "✓ Frames set — hold still…";
            return;
        }

        if (_lastCapturedSignature != null && ContentDifference(signature, _lastCapturedSignature) < ContentChangeThreshold)
        {
            DocumentStatus = "✓ Captured — swap page to continue";
            return;
        }

        if (Volatile.Read(ref _captureInProgress) != 0)
        {
            DocumentStatus = "✓ Capturing…";
            return;
        }

        DocumentStatus = "✓ Capturing…";
        _lastCapturedSignature = signature;
        _stableFrameCount = 0;
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
            DocumentStatus = "Waiting for boundary";
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
            return;
        }

        // Compare against the smoothed reference from before this update, then blend it
        // toward the new detection — comparing against the raw previous frame instead would
        // make ordinary hand/camera jitter reset stability far too often.
        var previousSmoothed = _smoothedRect;
        var wasStable = previousSmoothed.HasValue && IsRectStable(previousSmoothed.Value, rect, check.ImageWidth, check.ImageHeight);
        _smoothedRect = previousSmoothed.HasValue ? LerpRect(previousSmoothed.Value, rect, PositionSmoothingFactor) : rect;
        _stableFrameCount = wasStable ? Math.Min(_stableFrameCount + 1, StableFramesRequired) : 1;

        if (_stableFrameCount < StableFramesRequired)
        {
            DocumentStatus = "✓ Boundary detected — hold still…";
            return;
        }

        var contentDiff = ContentDifference(check.ContentSignature, _lastCapturedSignature);
        var isSameAsLastCapture = _lastCapturedSignature != null && contentDiff < ContentChangeThreshold;
        if (isSameAsLastCapture)
        {
            DocumentStatus = "✓ Captured — swap page to continue";
            return;
        }

        if (Volatile.Read(ref _captureInProgress) != 0)
        {
            DocumentStatus = "✓ Capturing…";
            return;
        }

        DocumentStatus = "✓ Capturing…";
        _lastCapturedSignature = check.ContentSignature;
        _stableFrameCount = 0;
        _ = CaptureAsync();
    }

    /// <summary>Mean absolute difference between two content signatures (0-255 scale).
    /// Missing or mismatched signatures are treated as "definitely different" so a
    /// comparison failure never blocks a real capture.</summary>
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
        PageCount = batch.Captures.Count > 0 ? batch.Captures.Max(c => c.PageNumber) : 0;
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
        await LoadRecentCapturesFromBatchAsync(batch);
        await RefreshFrameEditPermissionAsync();
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
                project = new Project
                {
                    Name = projectCode,
                    Customer = "",
                    Description = "Auto-created from scanning session",
                    CreatedBy = Environment.UserName,
                    OutputDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        "MicroCapture", projectCode)
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
                _dbContext.Batches.Add(batch);
                await _dbContext.SaveChangesAsync();

                _currentBatchId = batch.Id;
                PageCount = 0;
                RecentCaptures.Clear();
                AreFrameEditsAllowed = true;
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
        var latestPerPage = batch.Captures
            .Where(job => job.ProcessingStatus != "Superseded")
            .GroupBy(job => job.PageNumber)
            .Select(group => group.OrderByDescending(job => job.Timestamp).First())
            .OrderByDescending(job => job.PageNumber)
            .Take(100);

        foreach (var job in latestPerPage)
        {
            // Prefer the persisted per-page thumbnail (survives the original capture file
            // being deleted once processing succeeds — see AddThumbnail/BackgroundProcessingWorker)
            // over re-decoding the original, which is only reachable for jobs still
            // Pending/InProgress at resume time. Falls back to the original for jobs captured
            // before persisted thumbnails existed (no thumbnail file on disk yet).
            var thumbPath = MicroCapture.Processing.ThumbnailPaths.FileFor(_outputDirectory, batch.BatchCode, job.PageNumber);
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
        var firstPageNumber = PageCount + 1;
        PageCount += frameCount > 0 ? frameCount : 1;
        var pageStr = firstPageNumber.ToString("D6");
        var prefix = $"{_activeProjectCode}_{_activeBatchCode}_{pageStr}";

        StatusText = $"Capturing page{(frameCount > 0 ? "s" : "")} ...";
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            var filePath = await _cameraService.CaptureAsync(_outputDirectory, prefix);

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
                    var frameJob = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, pageNumber, SelectedCaptureFormat, SelectedDpi, cropBox);
                    AddThumbnail(frameJob.Id, filePath, pageNumber, frameIndex: i, cropRect: (px, py, pw, ph));
                }
            }
            else
            {
                var job = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount, SelectedCaptureFormat, SelectedDpi);
                AddThumbnail(job.Id, filePath, PageCount);
            }

            // A job is now queued under the current geometry — lock frame editing until it lands.
            _ = RefreshFrameEditPermissionAsync();

            // Require the page's content to actually change before auto-capture (or the
            // readiness indicator) can trigger again for this same physical page.
            _lastCapturedSignature = _lastDetectedSignature;
            _stableFrameCount = 0;

            StatusText = $"Page{(frameCount > 0 ? "s" : "")} captured — {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Capture failed: {ex.Message}";
            PageCount = firstPageNumber - 1; // Revert count
        }
        }
        finally { Volatile.Write(ref _captureInProgress, 0); }
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
        try
        {
            // Supersede all pages in this frame set
            for (var p = firstPageInSet; p <= PageCount; p++)
                await _queueService.SupersedePageAsync(_currentBatchId, p);

            var filePath = await _cameraService.CaptureAsync(_outputDirectory, prefix);

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
                    var frameJob = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, pageNumber, SelectedCaptureFormat, SelectedDpi, cropBox);
                    AddThumbnail(frameJob.Id, filePath, pageNumber, isRecapture: true, frameIndex: i, cropRect: (px, py, pw, ph));
                }
            }
            else
            {
                var job = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount, SelectedCaptureFormat, SelectedDpi);
                AddThumbnail(job.Id, filePath, PageCount, isRecapture: true);
            }
            _ = RefreshFrameEditPermissionAsync();

            _lastCapturedSignature = _lastDetectedSignature;
            _stableFrameCount = 0;

            StatusText = $"Page{(pagesInSet > 1 ? "s" : "")} {pageStr} recaptured";
        }
        catch (Exception ex)
        {
            StatusText = $"Recapture failed: {ex.Message}";
        }
        }
        finally { Volatile.Write(ref _captureInProgress, 0); }
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
        try
        {
            var parsed = Enum.Parse<MicroCapture.Core.Interfaces.FocusStep>(step);
            await _cameraService.NudgeFocusAsync(parsed);
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
        try
        {
            await _cameraService.TriggerAutoFocusAsync();
            StatusText = "Autofocus triggered.";
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
        var result = await MicroCapture.UI.Views.FinalizeBatchDialog.RunAsync(owner, _dbContext, batch, _outputDirectory);
        if (result == null) return;

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
    private void ApplyAdjustmentsToSelected()
    {
        var selectedIds = RecentCaptures.Where(t => t.IsSelected).Select(t => t.JobId).Distinct().ToList();
        if (selectedIds.Count == 0) return;
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

        foreach (var item in selected)
        {
            // DeleteCaptureAsync already removes every sibling thumbnail sharing this JobId
            // (fixed-frame captures), so re-checking IsSelected per iteration avoids acting on
            // an item RecentCaptures no longer contains.
            if (RecentCaptures.Contains(item))
                await DeleteCaptureAsync(item);
        }
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    // Non-null while Crop Review is open — MainWindow.axaml hosts a CropReviewWindow view bound
    // to this, replacing the live camera view in place (same pattern as CalibrationViewModel/
    // LensCalibrationViewModel's own inline panels) instead of opening a separate popup window.
    [ObservableProperty] private CropReviewViewModel? _activeCropReview;

    private void OpenCropReview(string jobId, IReadOnlyList<string>? selectionForBulkApply, bool openInAdjustMode = false)
    {
        if (string.IsNullOrEmpty(jobId)) return;

        ActiveCropReview?.Dispose();

        var cropReviewViewModel = new CropReviewViewModel(jobId, _dbContext, _queueService, selectionForBulkApply);
        if (openInAdjustMode) cropReviewViewModel.IsAdjustMode = true;
        cropReviewViewModel.Saved += (_, _) =>
        {
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
                            var thumbPath = MicroCapture.Processing.ThumbnailPaths.FileFor(_outputDirectory, _activeBatchCode, thumbnail.PageNumber);
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
        RecentCaptures.Insert(0, thumbnail);
        var placeholders = new List<ThumbnailItem> { thumbnail };

        // Keep last 100 thumbnails to avoid memory buildup
        while (RecentCaptures.Count > 100)
        {
            var old = RecentCaptures[^1];
            old.Thumbnail?.Dispose();
            RecentCaptures.RemoveAt(RecentCaptures.Count - 1);
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
                        var thumbPath = MicroCapture.Processing.ThumbnailPaths.FileFor(_outputDirectory, _activeBatchCode, pageNumber);
                        Directory.CreateDirectory(MicroCapture.Processing.ThumbnailPaths.DirectoryFor(_outputDirectory, _activeBatchCode));
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
    /// Removes a mistakenly captured page: marks it Superseded (excluded from processing and
    /// export, same as a recapture) and drops it from the thumbnail strip. The original file
    /// is left on disk — consistent with how a recapture already preserves prior attempts —
    /// but any processed derivative is deleted since it would otherwise sit unused forever.
    /// Called from MainWindow.axaml.cs's delete button on each thumbnail.
    /// </summary>
    public async Task DeleteCaptureAsync(ThumbnailItem item)
    {
        await _queueService.DeleteCaptureAsync(item.JobId);

        // Deleting the most recently captured page is effectively "undo that shot" — the next
        // real capture should reuse its page number, not leave a permanent gap. Deleting an
        // earlier page in the batch is different: PageCount must stay put, since decrementing
        // it would make the next capture collide with a page number that's still in use.
        // (A gap from deleting a non-tail page is harmless — export renumbers sequentially.)
        if (item.PageNumber == PageCount)
            PageCount--;

        try
        {
            // Processed derivatives now live in the main capture folder alongside the (still-
            // retained) original, not a separate "Processed" subfolder — target that folder, but
            // skip the original itself so this cleanup never removes the file the doc comment
            // above promises to leave on disk. Use the boundary-aware derivative matcher, not a
            // raw "{baseName}*" glob: a recapture's own original ("{baseName}_R_{timestamp}.jpg")
            // is a literal prefix-match of the page it recaptures and must never be deleted here.
            var processedDir = MicroCapture.Processing.ProcessedFilePaths.OutputDirectoryFor(item.FilePath);
            foreach (var derivative in MicroCapture.Processing.ProcessedFilePaths.EnumerateDerivatives(processedDir, item.FilePath))
            {
                if (string.Equals(Path.GetFullPath(derivative), Path.GetFullPath(item.FilePath), StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(derivative); }
                catch (IOException) { /* best-effort cleanup; the DB status is what actually excludes it */ }
                catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Processed-derivative cleanup failed for '{item.FilePath}': {ex}");
        }

        // Each thumbnail has its own JobId now (one frame == one independent job — see
        // CaptureAsync), so this only ever matches the single row being deleted.
        foreach (var sibling in RecentCaptures.Where(t => t.JobId == item.JobId).ToList())
        {
            sibling.Thumbnail?.Dispose();
            RecentCaptures.Remove(sibling);
        }
        StatusText = $"Page {item.PageNumber:D6} removed.";
    }

    /// <summary>
    /// Called from MainWindow.axaml.cs when keyboard shortcuts are pressed.
    /// </summary>
    public void HandleKeyShortcut(string key)
    {
        switch (key)
        {
            case "Space":
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
