using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.Processing;

namespace MicroCapture.UI.ViewModels;

/// <summary>Post-capture touch-up window: rotate/flip/tone/color/sharpen adjustments only.
/// Manual crop-quad/split-line/dewarp-curve editing has been removed — it was unreliable in
/// practice, and boundary/curve correction is now handled entirely by the automatic Method 4
/// pipeline (see ImageProcessor.Process/AltBoundaryPipeline.cs), which always re-detects the
/// page boundary on reprocess. Saving from here re-queues the job through that same automatic
/// path (or applies a delta directly to the derivative when the original is gone — see
/// IsPostExportAdjustOnly).</summary>
public partial class CropReviewViewModel : ViewModelBase, IDisposable
{
    private readonly string _jobId;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly string _imagePath;
    /// <summary>The file this session is actually editing — the raw original normally, or the
    /// processed derivative when <see cref="IsPostExportAdjustOnly"/> is true. Exposed so a
    /// caller can re-decode the up-to-date thumbnail straight from disk after a post-export Save
    /// (which writes directly to this same path), without needing its own copy of the path.</summary>
    public string ImagePath => _imagePath;
    private string _batchId = string.Empty;

    // True when this job's raw original was already deleted (batch exported — see
    // BatchExportService.DeleteOriginals) and _imagePath points at the processed derivative
    // instead. There is no un-cropped source left to re-detect or re-crop against, so this mode
    // is restricted to tone/rotation touch-ups applied directly on top of the derivative's
    // current pixels — see Save's own branch and ImageProcessor.ReapplyAdjustmentsToDerivative.
    [ObservableProperty] private bool _isPostExportAdjustOnly;

    public string SaveButtonLabel => IsPostExportAdjustOnly ? "Save Touch-Up" : "Save & Reprocess";
    // Job IDs the "Apply adjustments to selected" filmstrip action opened this window for, if
    // any — when non-empty, Save's bulk-apply path targets exactly this set instead of the
    // whole batch. Empty for the ordinary single-page open (click a thumbnail).
    private readonly IReadOnlyList<string> _selectionForBulkApply;

    private CropPreviewRenderer? _previewRenderer;
    private readonly DispatcherTimer _previewTimer;

    /// <summary>Raised after a successful Save & Reprocess, before the window closes — lets
    /// MainWindow show immediate "reprocessing" feedback on the matching thumbnail instead of
    /// leaving it looking unchanged for the ~1s the background worker takes to actually pick
    /// the job back up.</summary>
    public event EventHandler? Saved;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private string _boundaryHintText = string.Empty;

    // The full-frame corners passed to the preview renderer — this window no longer offers
    // crop-quad editing, so the preview always renders against the whole image.
    private CropPoint[] _fullFrameCorners = Array.Empty<CropPoint>();

    // ───────────── ADJUST MODE (rotate/flip/tone/color/sharpen) ─────────────

    [ObservableProperty] private int _rotationDegrees;
    [ObservableProperty] private bool _flipHorizontal;
    [ObservableProperty] private bool _flipVertical;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _sharpness;
    [ObservableProperty] private double _whiteBalance;

    // Cached so Reset can restore exactly what was loaded/saved.
    private bool _loadedHasManualAdjustments;
    private int _loadedRotationDegrees;
    private bool _loadedFlipHorizontal;
    private bool _loadedFlipVertical;
    private double _loadedBrightness;
    private double _loadedContrast;
    private double _loadedSaturation;
    private double _loadedSharpness;
    private double _loadedWhiteBalance;

    partial void OnRotationDegreesChanged(int value) => SchedulePreviewUpdate();
    partial void OnFlipHorizontalChanged(bool value) => SchedulePreviewUpdate();
    partial void OnFlipVerticalChanged(bool value) => SchedulePreviewUpdate();
    partial void OnBrightnessChanged(double value) => SchedulePreviewUpdate();
    partial void OnContrastChanged(double value) => SchedulePreviewUpdate();
    partial void OnSaturationChanged(double value) => SchedulePreviewUpdate();
    partial void OnSharpnessChanged(double value) => SchedulePreviewUpdate();
    partial void OnWhiteBalanceChanged(double value) => SchedulePreviewUpdate();

    [RelayCommand]
    private void RotateClockwise() => RotationDegrees = AdjustmentGeometry.NormalizeRotation(RotationDegrees + 90);

    [RelayCommand]
    private void RotateCounterclockwise() => RotationDegrees = AdjustmentGeometry.NormalizeRotation(RotationDegrees - 90);

    [RelayCommand]
    private void ToggleFlipHorizontal() => FlipHorizontal = !FlipHorizontal;

    [RelayCommand]
    private void ToggleFlipVertical() => FlipVertical = !FlipVertical;

    [RelayCommand]
    private void ApplyPresetDocument() => ApplyPreset(AdjustmentGeometry.Document);

    [RelayCommand]
    private void ApplyPresetPhoto() => ApplyPreset(AdjustmentGeometry.Photo);

    [RelayCommand]
    private void ApplyPresetGrayscale() => ApplyPreset(AdjustmentGeometry.Grayscale);

    [RelayCommand]
    private void ApplyPresetBlackAndWhite() => ApplyPreset(AdjustmentGeometry.BlackAndWhite);

    /// <summary>Seeds the sliders from a named preset — the operator can still nudge them
    /// afterward, this is a starting point, not a locked action. Rotation/flip are untouched:
    /// presets are a tone/color concept, geometry is independent.</summary>
    private void ApplyPreset(AdjustmentPreset preset)
    {
        Brightness = preset.Brightness;
        Contrast = preset.Contrast;
        Saturation = preset.Saturation;
    }

    private bool HasNonDefaultAdjustments =>
        RotationDegrees != 0 || FlipHorizontal || FlipVertical || Brightness != 0 ||
        Contrast != 0 || Saturation != 0 || Sharpness != 0 || WhiteBalance != 0;

    [ObservableProperty] private Bitmap? _previewImage;

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    // Set whenever the page's source image can't be loaded (job not found, or its original
    // capture file is gone — e.g. this page belongs to a batch that was already exported,
    // which deletes originals — see BatchExportService.DeleteOriginals). Previously a failure
    // here just left the whole panel silently blank forever with no feedback at all; the XAML
    // shows this text in the image area whenever Image is still null.
    [ObservableProperty] private string? _loadErrorMessage;

    public CropReviewViewModel()
    {
        // Design-time constructor.
        _jobId = ""; _dbContext = null!; _queueService = null!; _imagePath = "";
        _selectionForBulkApply = Array.Empty<string>();
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    }

    public CropReviewViewModel(string jobId, AppDbContext dbContext, CaptureQueueService queueService, IReadOnlyList<string>? selectionForBulkApply = null)
    {
        _jobId = jobId;
        _dbContext = dbContext;
        _queueService = queueService;
        _imagePath = string.Empty;
        _selectionForBulkApply = selectionForBulkApply ?? Array.Empty<string>();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview();
        };

        // ChangeTracker.Clear() first: this _dbContext is the same long-lived instance that
        // tracked this exact job when it was first enqueued (CaptureQueueService.
        // EnqueueCaptureAsync), so a plain Find() here can return that original in-memory
        // instance untouched by anything the background worker's separate context wrote since
        // — see OpenRecentBatchesAsync's identical fix for the full explanation.
        _dbContext.ChangeTracker.Clear();
        var job = _dbContext.CaptureJobs.Find(jobId);
        if (job == null)
        {
            LoadErrorMessage = "This page could not be found — it may have been deleted.";
            return;
        }
        if (!File.Exists(job.OriginalFilePath))
        {
            // The raw original is gone (batch exported — see BatchExportService.DeleteOriginals),
            // but the processed derivative usually still exists. Fall back to editing that
            // directly: tone/rotation touch-ups only, no re-crop (there's no un-cropped source
            // left to crop from) — see IsPostExportAdjustOnly and Save's own branch.
            var derivative = job.ProcessedFilePath?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(File.Exists);
            if (derivative == null)
            {
                LoadErrorMessage = "This page's image is no longer available (the batch may already be finalized/exported).";
                return;
            }

            IsPostExportAdjustOnly = true;
            _imagePath = derivative;
            _batchId = job.BatchId;
            // Sliders start at zero here, not at job's stored totals: post-export adjustments
            // are a fresh delta applied on top of whatever is already baked into the derivative
            // (see ReapplyAdjustmentsToDerivative's own doc comment), not a restatement of the
            // cumulative total from the original processing run.
            _loadedHasManualAdjustments = false;
            _loadedRotationDegrees = 0;
            _loadedFlipHorizontal = false;
            _loadedFlipVertical = false;
            _loadedBrightness = 0;
            _loadedContrast = 0;
            _loadedSaturation = 0;
            _loadedSharpness = 0;
            _loadedWhiteBalance = 0;

            try
            {
                var bytes = ImageDecodeHelper.GetDisplayBytes(derivative);
                if (bytes == null) throw new InvalidOperationException("Could not decode derivative.");
                using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                Image = bmp;
                ImageWidth = (int)bmp.Size.Width;
                ImageHeight = (int)bmp.Size.Height;
                _fullFrameCorners = RectCorners(0, 0, ImageWidth, ImageHeight);
                _previewRenderer = CropPreviewRenderer.Create(derivative);
                BoundaryHintText = "Original photo already deleted (batch exported) — tone/rotation touch-ups only.";
                RenderPreview();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CropReviewViewModel] Post-export derivative load failed: {ex}");
                LoadErrorMessage = $"Could not load this page: {ex.Message}";
            }
            return;
        }

        _imagePath = job.OriginalFilePath;
        _batchId = job.BatchId;
        _loadedHasManualAdjustments = job.HasManualAdjustments;
        _loadedRotationDegrees = job.RotationDegrees;
        _loadedFlipHorizontal = job.FlipHorizontal;
        _loadedFlipVertical = job.FlipVertical;
        _loadedBrightness = job.Brightness;
        _loadedContrast = job.Contrast;
        _loadedSaturation = job.Saturation;
        _loadedSharpness = job.Sharpness;
        _loadedWhiteBalance = job.WhiteBalance;
        RotationDegrees = job.RotationDegrees;
        FlipHorizontal = job.FlipHorizontal;
        FlipVertical = job.FlipVertical;
        Brightness = job.Brightness;
        Contrast = job.Contrast;
        Saturation = job.Saturation;
        Sharpness = job.Sharpness;
        WhiteBalance = job.WhiteBalance;

        try
        {
            using var quickStream = File.OpenRead(job.OriginalFilePath);
            var quickBmp = new Bitmap(quickStream);
            var imageWidth = (int)quickBmp.Size.Width;
            var imageHeight = (int)quickBmp.Size.Height;
            Image = quickBmp;
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
            _fullFrameCorners = RectCorners(0, 0, imageWidth, imageHeight);
            _previewRenderer = CropPreviewRenderer.Create(job.OriginalFilePath);
            BoundaryHintText = "Adjust rotation, flip, and tone/color — reprocessing re-detects the page boundary automatically.";
            RenderPreview();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CropReviewViewModel] Image decode failed: {ex}");
            LoadErrorMessage = $"Could not load this page: {ex.Message}";
        }
    }

    private static CropPoint[] RectCorners(double x, double y, double w, double h) =>
        new[] { new CropPoint(x, y), new CropPoint(x + w, y), new CropPoint(x + w, y + h), new CropPoint(x, y + h) };

    private void SchedulePreviewUpdate()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RenderPreview()
    {
        var renderer = _previewRenderer;
        if (renderer == null) return;

        var corners = _fullFrameCorners;
        var adjustSnapshot = (RotationDegrees, FlipHorizontal, FlipVertical, Brightness, Contrast, Saturation, Sharpness, WhiteBalance);

        Task.Run(() =>
        {
            var bytes = renderer.RenderPreviewWithAdjustments(corners,
                adjustSnapshot.RotationDegrees, adjustSnapshot.FlipHorizontal, adjustSnapshot.FlipVertical,
                adjustSnapshot.Brightness, adjustSnapshot.Contrast, adjustSnapshot.Saturation,
                adjustSnapshot.Sharpness, adjustSnapshot.WhiteBalance);
            if (bytes == null) return;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new Bitmap(ms);
                    var old = PreviewImage;
                    PreviewImage = bitmap;
                    old?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CropReviewViewModel] Preview decode failed: {ex}");
                }
            });
        });
    }

    [RelayCommand]
    private void Reset()
    {
        if (Image == null) return;

        // Restores whatever was loaded/last-saved for this job, not necessarily zero.
        RotationDegrees = _loadedRotationDegrees;
        FlipHorizontal = _loadedFlipHorizontal;
        FlipVertical = _loadedFlipVertical;
        Brightness = _loadedBrightness;
        Contrast = _loadedContrast;
        Saturation = _loadedSaturation;
        Sharpness = _loadedSharpness;
        WhiteBalance = _loadedWhiteBalance;
        SchedulePreviewUpdate();
    }

    /// <summary>Raised on Cancel, and after a successful Save (alongside <see cref="Saved"/>) —
    /// tells the host (MainWindowViewModel, when this view model is embedded in place of the
    /// live camera view) to swap back to the live view. Named independently of Saved since a
    /// listener may care about "review is done, close it" without also handling the
    /// reprocessing side-effect Saved exists for.</summary>
    public event EventHandler? ReviewClosed;

    [RelayCommand]
    private void Cancel()
    {
        ReviewClosed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (Image == null) { ReviewClosed?.Invoke(this, EventArgs.Empty); return; }

        if (IsPostExportAdjustOnly)
        {
            // No original left to re-queue for the normal background-worker reprocess — apply
            // the adjustment delta directly to the derivative file in place, synchronously,
            // right here. Nothing to persist on the CaptureJob row itself: RotationDegrees/
            // Brightness/etc. describe a from-scratch recipe against an original that no longer
            // exists, and this mode's sliders are a delta on top of the file's current pixels,
            // not a value that means anything stored back onto the job for a future reprocess.
            if (HasNonDefaultAdjustments)
            {
                MicroCapture.Processing.ImageProcessor.ReapplyAdjustmentsToDerivative(
                    _imagePath, RotationDegrees, FlipHorizontal, FlipVertical, Brightness, Contrast, Saturation, Sharpness, WhiteBalance);
            }
            Saved?.Invoke(this, EventArgs.Empty);
            ReviewClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var job = await _dbContext.CaptureJobs.FindAsync(_jobId);
        if (job != null)
        {
            // Manual crop-quad/split-line/dewarp-curve editing has been removed from this
            // window — this Save path only ever writes rotate/flip/tone/color/sharpen deltas
            // now. LeftCropBox/RightCropBox/DewarpCurve/ManualOverrideApplied/
            // DewarpManualOverrideApplied are intentionally left untouched here: a job re-queued
            // from this window falls through to ImageProcessor.Process's automatic (Method 4)
            // path exactly like a fresh capture, unless some OTHER mechanism (e.g. fixed-frame
            // capture's own EnqueueCaptureAsync call) already marked it manual — that mechanism
            // is untouched by this change and still owns those fields.
            job.RotationDegrees = RotationDegrees;
            job.FlipHorizontal = FlipHorizontal;
            job.FlipVertical = FlipVertical;
            job.Brightness = Brightness;
            job.Contrast = Contrast;
            job.Saturation = Saturation;
            job.Sharpness = Sharpness;
            job.WhiteBalance = WhiteBalance;
            job.HasManualAdjustments = _loadedHasManualAdjustments || HasNonDefaultAdjustments;

            job.ProcessingStatus = "Pending"; // Re-queue it
            job.QcStatus = "Pending";
            job.OcrStatus = "Pending";
            job.ExportStatus = "Pending";

            // A reprocess must not leave stale derivatives eligible for export. Processed
            // derivatives now live in the same folder as the (retained-until-export) original,
            // not a separate "Processed" subfolder — target that main folder, but skip the
            // original itself, which must survive a reprocess exactly like it survives every
            // other step before export. Use the boundary-aware derivative matcher, not a raw
            // "{baseName}*" glob: another job's recapture original can share this job's base
            // name as a literal string prefix and must never be swept up here.
            var processedDirectory = ProcessedFilePaths.OutputDirectoryFor(job.OriginalFilePath);
            foreach (var derivative in ProcessedFilePaths.EnumerateDerivatives(processedDirectory, job.OriginalFilePath))
            {
                if (string.Equals(Path.GetFullPath(derivative), Path.GetFullPath(job.OriginalFilePath), StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(derivative); }
                catch (IOException) { /* The worker will overwrite its own output on retry. */ }
                catch (UnauthorizedAccessException) { /* Preserve the source job; report remains available. */ }
            }

            await _dbContext.SaveChangesAsync();

            // Opened for a multi-selection, Save covers the whole selection.
            //
            // It used to write only the page on screen, leaving the other selected pages
            // untouched unless the operator also found and pressed "Apply to Selected" — so
            // choosing "Adjust Selected", confirming it would affect N pages, adjusting, and
            // saving changed exactly one of them. The scope was already agreed at the cart, so
            // asking again here (or requiring a second button) contradicts what the operator was
            // told would happen.
            if (_selectionForBulkApply.Count > 0)
            {
                var others = _selectionForBulkApply.Where(id => id != _jobId).ToList();
                if (others.Count > 0)
                    BulkApplyToJobs(_dbContext.CaptureJobs.Where(j => others.Contains(j.Id)));
            }

            Saved?.Invoke(this, EventArgs.Empty);
        }

        ReviewClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True only when this window was opened for a real multi-selection (via the
    /// filmstrip's "Apply adjustments to selected" action) — gates whether Apply-to-Selection
    /// is offered at all, distinct from Apply-to-All which is always available once a batch is
    /// known.</summary>
    public bool HasSelectionForBulkApply => _selectionForBulkApply.Count > 0;

    /// <summary>Raised when a bulk action (Apply to All/Selection) needs the operator to
    /// confirm scope + count before committing — per the UX research, silently applying to a
    /// wide, unreviewed set of pages is the most damaging failure mode in a production tool.
    /// The window subscribes and shows a confirm dialog, then calls back with the answer.</summary>
    public event EventHandler<BulkApplyConfirmRequest>? ConfirmBulkApplyRequested;

    [RelayCommand]
    private void ApplyToAll()
    {
        if (string.IsNullOrEmpty(_batchId)) return;
        var count = _dbContext.CaptureJobs.Count(j => j.BatchId == _batchId && j.Id != _jobId);
        if (count == 0) return;
        ConfirmBulkApplyRequested?.Invoke(this, new BulkApplyConfirmRequest(
            $"Apply these adjustments to {count} other page{(count == 1 ? "" : "s")} in this batch?",
            confirmed => { if (confirmed) BulkApplyToJobs(_dbContext.CaptureJobs.Where(j => j.BatchId == _batchId && j.Id != _jobId)); }));
    }

    /// <summary>Applies to the rest of the selection without closing, for an operator who wants
    /// to see it land before committing. Save does this too, so this is a convenience rather
    /// than a required step — which is exactly what it used to be mistaken for.</summary>
    [RelayCommand]
    private void ApplyToSelection()
    {
        if (_selectionForBulkApply.Count == 0) return;
        var targetIds = _selectionForBulkApply.Where(id => id != _jobId).ToList();
        if (targetIds.Count == 0) return;
        ConfirmBulkApplyRequested?.Invoke(this, new BulkApplyConfirmRequest(
            $"Apply these adjustments to {targetIds.Count} selected page{(targetIds.Count == 1 ? "" : "s")}?",
            confirmed => { if (confirmed) BulkApplyToJobs(_dbContext.CaptureJobs.Where(j => targetIds.Contains(j.Id))); }));
    }

    /// <summary>Bulk-sets the current slider values onto every targeted job and re-queues them
    /// for processing — the same "reset ProcessingStatus to Pending, let the background worker
    /// pick it back up" mechanism Save already uses for one page, just applied to N rows in one
    /// transaction. Does not touch crop/dewarp fields — this only ever applies the tone/color/
    /// geometry adjustment stack, never someone else's crop shape.</summary>
    private void BulkApplyToJobs(IQueryable<CaptureJob> targets)
    {
        foreach (var target in targets)
        {
            target.RotationDegrees = RotationDegrees;
            target.FlipHorizontal = FlipHorizontal;
            target.FlipVertical = FlipVertical;
            target.Brightness = Brightness;
            target.Contrast = Contrast;
            target.Saturation = Saturation;
            target.Sharpness = Sharpness;
            target.WhiteBalance = WhiteBalance;
            target.HasManualAdjustments = true;
            target.ProcessingStatus = "Pending";
            target.QcStatus = "Pending";
            target.OcrStatus = "Pending";
            target.ExportStatus = "Pending";
        }
        _dbContext.SaveChanges();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _previewTimer.Stop();
        _previewRenderer?.Dispose();
        _previewRenderer = null;
    }
}

/// <summary>A bulk-apply confirmation prompt raised by <see cref="CropReviewViewModel"/> for
/// the window to display — <see cref="OnAnswered"/> must be called exactly once with the
/// operator's answer for the bulk action to actually run (or be abandoned).</summary>
public sealed class BulkApplyConfirmRequest
{
    public string Message { get; }
    public Action<bool> OnAnswered { get; }

    public BulkApplyConfirmRequest(string message, Action<bool> onAnswered)
    {
        Message = message;
        OnAnswered = onAnswered;
    }
}
