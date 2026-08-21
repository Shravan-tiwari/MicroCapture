using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.Processing;

namespace MicroCapture.UI.ViewModels;

public partial class CropReviewViewModel : ViewModelBase, IDisposable
{
    private readonly string _jobId;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly string _imagePath;
    private string _batchId = string.Empty;
    // Job IDs the "Apply adjustments to selected" filmstrip action opened this window for, if
    // any — when non-empty, Save's bulk-apply path targets exactly this set instead of the
    // whole batch. Empty for the ordinary single-page open (click a thumbnail).
    private readonly IReadOnlyList<string> _selectionForBulkApply;

    // Cached so Reset can re-apply the smart-detected starting point instead of falling back
    // to an unhelpful full-frame box or an unconditioned 50/50 split.
    private CropPoint[]? _detectedCorners;
    private double _detectedSplitPercent = 50.0;
    private CropPoint[]? _detectedLeftQuad;
    private CropPoint[]? _detectedRightQuad;

    // Method 4's raw traced boundary (top/bottom curve, left/right side edge, gutter line), in
    // the original image's own pixel coordinates — kept separately from the 4-corner quads above
    // so the overlay can draw what Method 4 actually found, while the draggable quad (derived
    // from these same traces, see BuildQuadFromMethod4) remains the thing the operator edits and
    // Save persists. Null when the job has a saved manual crop (no detection ran) or detection
    // failed to load/decode the image.
    [ObservableProperty] private CropPoint[]? _method4TopCurve;
    [ObservableProperty] private CropPoint[]? _method4BottomCurve;
    [ObservableProperty] private CropPoint[]? _method4LeftCurve;
    [ObservableProperty] private CropPoint[]? _method4RightCurve;
    [ObservableProperty] private CropPoint[]? _method4GutterLine;

    // A previously-saved manual dewarp curve, if any — seeded into DewarpTopPoints/BottomPoints
    // the first time the operator opens the curve editor this session. _dewarpTouched tracks
    // whether they actually did, so Save doesn't write dewarp data nobody looked at.
    private DewarpModel? _savedDewarp;
    private bool _dewarpTouched;

    // Cached once at load for fast, non-blocking corner-snapping and live preview during
    // interactive dragging — never re-computed per drag frame.
    private IReadOnlyList<CropPoint> _edgePoints = Array.Empty<CropPoint>();
    private CropPreviewRenderer? _previewRenderer;
    private readonly DispatcherTimer _previewTimer;

    private const double SnapRadiusPixels = 15.0;

    /// <summary>Raised after a successful Save & Reprocess, before the window closes — lets
    /// MainWindow show immediate "reprocessing" feedback on the matching thumbnail instead of
    /// leaving it looking unchanged for the ~1s the background worker takes to actually pick
    /// the job back up.</summary>
    public event EventHandler? Saved;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _splitPercent = 50.0;
    [ObservableProperty] private bool _isSplitBookPages;
    [ObservableProperty] private bool _isSinglePage;
    [ObservableProperty] private string _boundaryHintText = string.Empty;

    // Single-page mode: the 4 freely-draggable corners of the crop quad, in the original
    // image's own pixel coordinates (ordered top-left, top-right, bottom-right, bottom-left).
    [ObservableProperty] private CropPoint _topLeft;
    [ObservableProperty] private CropPoint _topRight;
    [ObservableProperty] private CropPoint _bottomRight;
    [ObservableProperty] private CropPoint _bottomLeft;

    // Split mode has two editing styles: a single shared line (simple, always available), or
    // two independent per-page quads when detection is confident enough to offer it (or the
    // operator switches to it manually). Each array is always exactly 4 corners, same order
    // as above.
    [ObservableProperty] private bool _isTwoQuadSplit;
    [ObservableProperty] private CropPoint[] _leftQuad = new CropPoint[4];
    [ObservableProperty] private CropPoint[] _rightQuad = new CropPoint[4];

    // XAML-facing computed flags for which split sub-editor should be visible. Dewarp mode
    // takes over the whole overlay when active — the crop shape underneath isn't touched, just
    // not rendered/draggable while the operator is adjusting curvature instead.
    public bool IsLineSplitMode => IsSplitBookPages && !IsTwoQuadSplit && !IsDewarpMode;
    public bool IsQuadSplitMode => IsSplitBookPages && IsTwoQuadSplit && !IsDewarpMode;

    partial void OnIsSplitBookPagesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLineSplitMode));
        OnPropertyChanged(nameof(IsQuadSplitMode));
    }

    partial void OnIsTwoQuadSplitChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLineSplitMode));
        OnPropertyChanged(nameof(IsQuadSplitMode));
    }

    // Book-curve control points, in the *downscaled crop preview's* own pixel space (see
    // CropPreviewRenderer.Scale) — not the original image's coordinates, since dewarp is
    // defined relative to the already-cropped page. Always exactly DewarpModel.ControlPointCount
    // long. X stays pinned to each point's slot; only Y is operator-adjustable (see
    // ResolveDewarpPointDrag).
    [ObservableProperty] private CropPoint[] _dewarpTopPoints = new CropPoint[DewarpModel.ControlPointCount];
    [ObservableProperty] private CropPoint[] _dewarpBottomPoints = new CropPoint[DewarpModel.ControlPointCount];
    [ObservableProperty] private bool _isDewarpMode;
    [ObservableProperty] private int _dewarpSpaceWidth;
    [ObservableProperty] private int _dewarpSpaceHeight;
    // The undewarped crop the operator drags control points against — deliberately separate
    // from PreviewImage (which shows the live *corrected* result) so the drag surface itself
    // never moves under the cursor while it's being edited.
    [ObservableProperty] private Bitmap? _dewarpBackdropImage;

    public string DewarpEditButtonLabel => IsDewarpMode ? "Done Adjusting Curve" : "Adjust Curve";

    partial void OnIsDewarpModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLineSplitMode));
        OnPropertyChanged(nameof(IsQuadSplitMode));
        OnPropertyChanged(nameof(DewarpEditButtonLabel));
        SchedulePreviewUpdate();
    }

    partial void OnDewarpTopPointsChanged(CropPoint[] value) => SchedulePreviewUpdate();
    partial void OnDewarpBottomPointsChanged(CropPoint[] value) => SchedulePreviewUpdate();

    // ───────────── ADJUST MODE (rotate/flip/tone/color/sharpen) ─────────────

    [ObservableProperty] private bool _isAdjustMode;
    [ObservableProperty] private int _rotationDegrees;
    [ObservableProperty] private bool _flipHorizontal;
    [ObservableProperty] private bool _flipVertical;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _saturation;
    [ObservableProperty] private double _sharpness;
    [ObservableProperty] private double _whiteBalance;

    // Cached so Reset (in Adjust mode) can restore exactly what was loaded/saved, same pattern
    // as _detectedCorners for crop mode.
    private bool _loadedHasManualAdjustments;
    private int _loadedRotationDegrees;
    private bool _loadedFlipHorizontal;
    private bool _loadedFlipVertical;
    private double _loadedBrightness;
    private double _loadedContrast;
    private double _loadedSaturation;
    private double _loadedSharpness;
    private double _loadedWhiteBalance;

    public string AdjustEditButtonLabel => IsAdjustMode ? "Done Adjusting" : "Adjust";

    partial void OnIsAdjustModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AdjustEditButtonLabel));
        SchedulePreviewUpdate();
    }

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

    [RelayCommand]
    private void ToggleAdjustMode()
    {
        if (Image == null) return;
        IsAdjustMode = !IsAdjustMode;
    }

    private bool HasNonDefaultAdjustments =>
        RotationDegrees != 0 || FlipHorizontal || FlipVertical || Brightness != 0 ||
        Contrast != 0 || Saturation != 0 || Sharpness != 0 || WhiteBalance != 0;

    // Live preview of the corrected image as it's edited. Split mode uses both (left/right);
    // single-page mode uses only PreviewImage.
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private Bitmap? _secondaryPreviewImage;

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
            LoadErrorMessage = "This page's original image is no longer available (the batch may already be finalized/exported).";
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

        // Decode and show the image immediately, before running any boundary detection. Method 4
        // detection below (DetectSpreadBoundaryMethod4/DetectSinglePageBoundaryMethod4) plus
        // DetectEdgePoints and CropPreviewRenderer.Create are OpenCV passes that can take
        // several seconds — or hang — on a difficult image. Previously Image was only ever set
        // at the end of that whole pipeline, on the UI-thread Post below, so the window stayed
        // completely blank the entire time no matter how long the operator waited, with no
        // partial content to indicate anything was happening. Decoding just the bitmap is fast
        // and independent of detection, so it goes first and is posted to the UI thread right
        // away.
        try
        {
            using var quickStream = File.OpenRead(job.OriginalFilePath);
            var quickBmp = new Bitmap(quickStream);
            Dispatcher.UIThread.Post(() =>
            {
                Image = quickBmp;
                ImageWidth = (int)quickBmp.Size.Width;
                ImageHeight = (int)quickBmp.Size.Height;
                BoundaryHintText = "Detecting page boundary…";
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CropReviewViewModel] Quick image decode failed: {ex}");
            LoadErrorMessage = $"Could not load this page: {ex.Message}";
            return;
        }

        // Now run detection on a background thread — this fills in the crop overlay/curves
        // once ready, without having delayed the image itself above.
        Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(job.OriginalFilePath);
                var bmp = new Bitmap(stream);
                var batch = _dbContext.Batches.Find(job.BatchId);
                var hasSavedCrop = job.ManualOverrideApplied && !string.IsNullOrWhiteSpace(job.LeftCropBox);
                var savedDewarp = job.DewarpManualOverrideApplied && !string.IsNullOrWhiteSpace(job.DewarpCurve)
                    ? ImageProcessor.ParseDewarpCurve(job.DewarpCurve!)
                    : null;
                var imageWidth = (int)bmp.Size.Width;
                var imageHeight = (int)bmp.Size.Height;

                CropPoint[]? savedCorners = null;
                CropPoint[]? savedRightCorners = null;
                bool? savedIsTwoQuad = null;
                CropPoint[]? detectedCorners = null;
                CropPoint[]? detectedLeftQuad = null;
                CropPoint[]? detectedRightQuad = null;
                double detectedSplit = 50.0;
                double detectedConfidence = 0.0;
                double highConfidenceThreshold = 0.5;
                CropPoint[]? topCurve = null;
                CropPoint[]? bottomCurve = null;
                CropPoint[]? leftCurve = null;
                CropPoint[]? rightCurve = null;
                CropPoint[]? gutterLine = null;

                // What the batch's own automatic pipeline (ImageProcessor.Process) would decide
                // for THIS specific image, not just the batch-level checkbox — see
                // DetectAutoSplit's own remarks. A saved crop's own shape (one box vs. two)
                // still wins for a job the operator already reviewed; this only drives what gets
                // detected/previewed for a job that hasn't been reviewed yet.
                var isSplit = hasSavedCrop
                    ? !string.IsNullOrWhiteSpace(job.RightCropBox)
                    : new ImageProcessor().DetectAutoSplit(_imagePath, batch?.SplitBookPages == true);

                if (hasSavedCrop)
                {
                    savedCorners = ImageProcessor.ParseCropShape(job.LeftCropBox!, imageWidth, imageHeight);
                    if (isSplit && !string.IsNullOrWhiteSpace(job.RightCropBox))
                    {
                        savedRightCorners = ImageProcessor.ParseCropShape(job.RightCropBox!, imageWidth, imageHeight);
                        // 8 comma-separated numbers means this was saved as an independent quad
                        // (two-quad mode was used); 4 means a plain rect/strip (line mode).
                        savedIsTwoQuad = job.LeftCropBox!.Split(',').Length == 8;
                    }
                }
                else
                {
                    var processor = new ImageProcessor();
                    highConfidenceThreshold = processor.CropConfidenceThreshold;
                    if (isSplit)
                    {
                        var spread = processor.DetectSpreadBoundaryMethod4(_imagePath);
                        if (spread.HasValue)
                        {
                            var b = spread.Value;
                            topCurve = ToCurve(b.TopFinal);
                            bottomCurve = ToCurve(b.BottomFinal);
                            leftCurve = ToCurve(b.Left.Columns, b.Left.RowLo, columnIsX: false);
                            rightCurve = ToCurve(b.Right.Columns, b.Right.RowLo, columnIsX: false);
                            gutterLine = b.Gutter.Line.Select(p => new CropPoint(p.Column, p.Row)).ToArray();

                            var gutterMidCol = (b.Gutter.TopNotch.Column + b.Gutter.BottomNotch.Column) / 2.0;
                            detectedLeftQuad = BuildQuadFromMethod4(topCurve, bottomCurve, leftCurve, rightCurve, imageWidth, xMax: gutterMidCol);
                            detectedRightQuad = BuildQuadFromMethod4(topCurve, bottomCurve, leftCurve, rightCurve, imageWidth, xMin: gutterMidCol);
                        }
                        else
                        {
                            detectedSplit = processor.DetectGutterSplitPercent(_imagePath);
                        }
                    }
                    else
                    {
                        var single = processor.DetectSinglePageBoundaryMethod4(_imagePath);
                        if (single.HasValue)
                        {
                            var b = single.Value;
                            topCurve = ToCurve(b.TopFinal);
                            bottomCurve = ToCurve(b.BottomFinal);
                            leftCurve = ToCurve(b.Left.Columns, b.Left.RowLo, columnIsX: false);
                            rightCurve = ToCurve(b.Right.Columns, b.Right.RowLo, columnIsX: false);
                            detectedCorners = BuildQuadFromMethod4(topCurve, bottomCurve, leftCurve, rightCurve, imageWidth);
                            detectedConfidence = highConfidenceThreshold;
                        }
                    }
                }

                var edgePoints = ImageProcessor.DetectEdgePoints(_imagePath);
                var previewRenderer = CropPreviewRenderer.Create(_imagePath);

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        Image = bmp;
                        LoadErrorMessage = null;
                        ImageWidth = imageWidth;
                        ImageHeight = imageHeight;
                        IsSplitBookPages = isSplit;
                        IsSinglePage = !isSplit;
                        _edgePoints = edgePoints;
                        _previewRenderer = previewRenderer;
                        _detectedCorners = detectedCorners;
                        _detectedSplitPercent = detectedSplit;
                        _detectedLeftQuad = detectedLeftQuad;
                        _detectedRightQuad = detectedRightQuad;
                        _savedDewarp = savedDewarp;
                        Method4TopCurve = topCurve;
                        Method4BottomCurve = bottomCurve;
                        Method4LeftCurve = leftCurve;
                        Method4RightCurve = rightCurve;
                        Method4GutterLine = gutterLine;

                        if (hasSavedCrop && savedCorners != null)
                        {
                            if (isSplit && savedIsTwoQuad == true && savedRightCorners != null)
                            {
                                IsTwoQuadSplit = true;
                                LeftQuad = savedCorners;
                                RightQuad = savedRightCorners;
                            }
                            else if (isSplit)
                            {
                                // Derive the split ratio from the saved left box's own width —
                                // works whether it was saved as a legacy rect or an axis-aligned quad.
                                IsTwoQuadSplit = false;
                                var leftWidth = savedCorners[1].X - savedCorners[0].X;
                                SplitPercent = imageWidth > 0
                                    ? Math.Clamp(leftWidth / imageWidth * 100.0, 1.0, 99.0)
                                    : 50.0;
                            }
                            else
                            {
                                SetCorners(savedCorners);
                            }
                            BoundaryHintText = "Previously saved crop restored — adjust if needed.";
                        }
                        else if (isSplit && detectedLeftQuad != null && detectedRightQuad != null)
                        {
                            IsTwoQuadSplit = true;
                            LeftQuad = detectedLeftQuad;
                            RightQuad = detectedRightQuad;
                            BoundaryHintText = "Auto-detected two separate pages — drag a corner to adjust.";
                        }
                        else if (isSplit)
                        {
                            IsTwoQuadSplit = false;
                            SplitPercent = detectedSplit;
                            BoundaryHintText = "Estimated split point — drag the line to adjust.";
                        }
                        else if (detectedCorners != null)
                        {
                            SetCorners(detectedCorners);
                            // Medium-confidence detections are still pre-filled — a suggestion
                            // beats a blank full-frame box — but flagged so the operator knows
                            // to actually check it rather than trusting it the way a
                            // high-confidence detection earns.
                            BoundaryHintText = detectedConfidence >= highConfidenceThreshold
                                ? "Auto-detected boundary — drag a corner to adjust."
                                : "Suggested boundary (lower confidence) — please check it carefully.";
                        }
                        else
                        {
                            SetCorners(RectCorners(0, 0, imageWidth, imageHeight));
                            BoundaryHintText = "No boundary detected — showing full image.";
                        }

                        RenderPreview();
                    }
                    catch (Exception uiEx)
                    {
                        Console.Error.WriteLine($"[CropReviewViewModel] Applying loaded state failed: {uiEx}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CropReviewViewModel] Background crop review load failed: {ex}");
                Dispatcher.UIThread.Post(() => LoadErrorMessage = $"Could not load this page: {ex.Message}");
            }
        });
    }

    private void SetCorners(CropPoint[] corners)
    {
        TopLeft = corners[0];
        TopRight = corners[1];
        BottomRight = corners[2];
        BottomLeft = corners[3];
    }

    private static CropPoint[] RectCorners(double x, double y, double w, double h) =>
        new[] { new CropPoint(x, y), new CropPoint(x + w, y), new CropPoint(x + w, y + h), new CropPoint(x, y + h) };

    /// <summary>Converts a Method 4 per-column trace (<c>AltSpreadBoundary.TopFinal</c>/
    /// <c>BottomFinal</c>, one row-value per column starting at column 0) into dense
    /// <see cref="CropPoint"/>s in original-image pixel space, for the overlay to draw as a
    /// polyline.</summary>
    private static CropPoint[] ToCurve(double[] rowPerColumn) =>
        rowPerColumn.Select((row, col) => new CropPoint(col, row)).ToArray();

    /// <summary>Converts a Method 4 side-edge trace (<c>AltSideEdgeTrace.Columns</c>, one
    /// x-position per row starting at <paramref name="rowLo"/>) into dense pixel-space points.
    /// <paramref name="columnIsX"/> is always false here (kept for symmetry with
    /// <see cref="ToCurve(double[])"/> — the side trace is naturally row-indexed, x is the
    /// value), named so a call site reads as "x is the column value, not the loop index".</summary>
    private static CropPoint[] ToCurve(double[] xPerRow, int rowLo, bool columnIsX) =>
        xPerRow.Select((x, i) => new CropPoint(x, rowLo + i)).ToArray();

    /// <summary>Collapses Method 4's raw traced curves down to the 4-corner quad the existing
    /// draggable-crop UI expects, by sampling the top/bottom curves and left/right side traces
    /// at the requested column range's endpoints. <paramref name="xMin"/>/<paramref name="xMax"/>
    /// restrict the sample to one half of a spread (left half: xMax = gutter column, right half:
    /// xMin = gutter column); omitted for a single page, which uses the traces' own full extent.
    /// This is a starting point for editing, not the shape shown to the operator — the overlay
    /// draws the real curves separately (see Method4TopCurve etc.).</summary>
    private static CropPoint[] BuildQuadFromMethod4(
        CropPoint[]? topCurve, CropPoint[]? bottomCurve, CropPoint[]? leftCurve, CropPoint[]? rightCurve,
        int imageWidth, double? xMin = null, double? xMax = null)
    {
        var lo = Math.Clamp((int)Math.Round(xMin ?? 0), 0, imageWidth - 1);
        var hi = Math.Clamp((int)Math.Round(xMax ?? (imageWidth - 1)), lo, imageWidth - 1);

        double TopAt(int x) => topCurve != null && x < topCurve.Length ? topCurve[x].Y : 0;
        double BottomAt(int x) => bottomCurve != null && x < bottomCurve.Length ? bottomCurve[x].Y : 0;

        // Side traces are row-indexed (one x-value per row), not column-indexed — use their own
        // median x as a stable left/right bound rather than trying to look up a single row.
        var leftX = leftCurve is { Length: > 0 } lc ? lc.Average(p => p.X) : lo;
        var rightX = rightCurve is { Length: > 0 } rc ? rc.Average(p => p.X) : hi;
        leftX = Math.Max(leftX, lo);
        rightX = Math.Min(rightX, hi);
        if (rightX <= leftX) { leftX = lo; rightX = hi; }

        var topLeftY = TopAt((int)Math.Round(leftX));
        var topRightY = TopAt((int)Math.Round(rightX));
        var bottomLeftY = BottomAt((int)Math.Round(leftX));
        var bottomRightY = BottomAt((int)Math.Round(rightX));

        return new[]
        {
            new CropPoint(leftX, topLeftY),
            new CropPoint(rightX, topRightY),
            new CropPoint(rightX, bottomRightY),
            new CropPoint(leftX, bottomLeftY),
        };
    }

    /// <summary>Called by the window while a corner is being dragged: snaps to a nearby real
    /// edge when one is close enough, then clamps the result so the quad can never become
    /// concave or self-intersecting. Also schedules a debounced live-preview update.</summary>
    public CropPoint ResolveCornerDrag(int cornerIndex, CropPoint rawPosition)
    {
        var snapped = TrySnapToEdge(rawPosition);
        var corners = new[] { TopLeft, TopRight, BottomRight, BottomLeft };
        var resolved = CropGeometry.ClampCornerToConvex(corners, cornerIndex, snapped);
        SchedulePreviewUpdate();
        return resolved;
    }

    /// <summary>Same as <see cref="ResolveCornerDrag"/>, for a corner of one of the two
    /// independent per-page quads in split mode.</summary>
    public CropPoint ResolveSplitCornerDrag(bool isLeftPage, int cornerIndex, CropPoint rawPosition)
    {
        var snapped = TrySnapToEdge(rawPosition);
        var corners = isLeftPage ? LeftQuad : RightQuad;
        var resolved = CropGeometry.ClampCornerToConvex(corners, cornerIndex, snapped);
        SchedulePreviewUpdate();
        return resolved;
    }

    [RelayCommand]
    private void ToggleSplitEditMode()
    {
        if (!IsSplitBookPages || Image == null) return;

        if (IsTwoQuadSplit)
        {
            // Switching to line mode: derive an approximate split percent from the current quads.
            var rightEdgeOfLeft = (LeftQuad[1].X + LeftQuad[2].X) / 2.0;
            SplitPercent = ImageWidth > 0 ? Math.Clamp(rightEdgeOfLeft / ImageWidth * 100.0, 1.0, 99.0) : 50.0;
            IsTwoQuadSplit = false;
            BoundaryHintText = "Split line — drag to adjust, or switch to page-by-page.";
        }
        else
        {
            // Switching to page-by-page mode: seed both quads from the current split line.
            var splitX = ImageWidth * (SplitPercent / 100.0);
            LeftQuad = RectCorners(0, 0, splitX, ImageHeight);
            RightQuad = RectCorners(splitX, 0, ImageWidth - splitX, ImageHeight);
            IsTwoQuadSplit = true;
            BoundaryHintText = "Two independent pages — drag a corner to adjust.";
        }
        SchedulePreviewUpdate();
    }

    /// <summary>The crop shape dewarp is edited and previewed against — the left/single page's
    /// quad, since a saved manual curve currently applies to just that one shape (the pipeline
    /// still auto-detects an independent curve for a split spread's right half).</summary>
    private CropPoint[] CurrentPrimaryCorners() =>
        IsSplitBookPages && IsTwoQuadSplit ? LeftQuad
        : IsSplitBookPages ? RectCorners(0, 0, ImageWidth * (SplitPercent / 100.0), ImageHeight)
        : new[] { TopLeft, TopRight, BottomRight, BottomLeft };

    [RelayCommand]
    private void ToggleDewarpEditMode()
    {
        if (Image == null) return;
        if (!IsDewarpMode) SeedDewarpPoints();
        IsDewarpMode = !IsDewarpMode;
    }

    /// <summary>Seeds the curve editor from (in priority order) a previously-saved manual
    /// curve, an auto-detected one, or — when neither is available — a flat pair of lines the
    /// operator can drag from scratch. Runs against a fresh crop-preview render so the control
    /// points land in that render's own pixel space (see CropPreviewRenderer.Scale), which is
    /// what Save later converts back to full-resolution coordinates.</summary>
    private void SeedDewarpPoints()
    {
        var renderer = _previewRenderer;
        if (renderer == null) return;

        var bytes = renderer.RenderPreview(CurrentPrimaryCorners());
        if (bytes == null) return;

        try
        {
            using var ms = new MemoryStream(bytes);
            var backdrop = new Bitmap(ms);
            var old = DewarpBackdropImage;
            DewarpBackdropImage = backdrop;
            old?.Dispose();
            DewarpSpaceWidth = (int)backdrop.Size.Width;
            DewarpSpaceHeight = (int)backdrop.Size.Height;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CropReviewViewModel] Dewarp preview backdrop failed: {ex}");
            return;
        }

        _dewarpTouched = true;
        var model = _savedDewarp ?? ImageProcessor.DetectDewarpCurveFromBytes(bytes);
        if (model is { } m)
        {
            DewarpTopPoints = m.TopControlPoints;
            DewarpBottomPoints = m.BottomControlPoints;
        }
        else
        {
            BoundaryHintText = "No confident curve detected — drag the lines to trace the page's actual bend.";
            DewarpTopPoints = EvenlySpacedRow(DewarpSpaceWidth, DewarpSpaceHeight * 0.12);
            DewarpBottomPoints = EvenlySpacedRow(DewarpSpaceWidth, DewarpSpaceHeight * 0.92);
        }
    }

    private static CropPoint[] EvenlySpacedRow(int width, double y)
    {
        var points = new CropPoint[DewarpModel.ControlPointCount];
        for (var i = 0; i < points.Length; i++)
            points[i] = new CropPoint(width <= 1 ? 0 : (double)i / (points.Length - 1) * (width - 1), y);
        return points;
    }

    /// <summary>Called by the window while a dewarp control point is being dragged: pins X to
    /// the point's own slot (only curvature height is operator-adjustable) and clamps Y to the
    /// preview's bounds.</summary>
    public CropPoint ResolveDewarpPointDrag(bool isTop, int index, CropPoint rawPosition)
    {
        var points = isTop ? DewarpTopPoints : DewarpBottomPoints;
        var pinnedX = index >= 0 && index < points.Length ? points[index].X : rawPosition.X;
        var y = DewarpSpaceHeight > 0 ? Math.Clamp(rawPosition.Y, 0, DewarpSpaceHeight) : rawPosition.Y;
        SchedulePreviewUpdate();
        return new CropPoint(pinnedX, y);
    }

    private CropPoint TrySnapToEdge(CropPoint dragged)
    {
        if (_edgePoints.Count == 0) return dragged;

        var nearestDistSq = double.MaxValue;
        CropPoint? nearest = null;
        foreach (var candidate in _edgePoints)
        {
            var dx = candidate.X - dragged.X;
            var dy = candidate.Y - dragged.Y;
            var distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq) { nearestDistSq = distSq; nearest = candidate; }
        }

        return nearest is { } point && nearestDistSq <= SnapRadiusPixels * SnapRadiusPixels ? point : dragged;
    }

    private void SchedulePreviewUpdate()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RenderPreview()
    {
        if (_previewRenderer == null) return;

        if (IsAdjustMode)
        {
            RenderPreviewInto(CurrentPrimaryCorners(), isPrimary: true);
            SecondaryPreviewImage = null;
            return;
        }

        if (IsDewarpMode)
        {
            RenderPreviewInto(CurrentPrimaryCorners(), isPrimary: true);
            SecondaryPreviewImage = null;
            return;
        }

        if (IsSplitBookPages && IsTwoQuadSplit)
        {
            RenderPreviewInto(LeftQuad, isPrimary: true);
            RenderPreviewInto(RightQuad, isPrimary: false);
        }
        else if (IsSplitBookPages)
        {
            var splitX = ImageWidth * (SplitPercent / 100.0);
            RenderPreviewInto(RectCorners(0, 0, splitX, ImageHeight), isPrimary: true);
            RenderPreviewInto(RectCorners(splitX, 0, ImageWidth - splitX, ImageHeight), isPrimary: false);
        }
        else
        {
            RenderPreviewInto(new[] { TopLeft, TopRight, BottomRight, BottomLeft }, isPrimary: true);
            SecondaryPreviewImage = null;
        }
    }

    private void RenderPreviewInto(CropPoint[] corners, bool isPrimary)
    {
        var renderer = _previewRenderer;
        if (renderer == null) return;

        // Snapshot under IsDewarpMode/IsAdjustMode's current values, not re-read inside the
        // background task — the operator could toggle modes or drag a slider again before this
        // frame finishes rendering.
        DewarpModel? dewarpSnapshot = IsDewarpMode
            ? new DewarpModel((CropPoint[])DewarpTopPoints.Clone(), (CropPoint[])DewarpBottomPoints.Clone())
            : null;
        var adjustSnapshot = IsAdjustMode
            ? (RotationDegrees, FlipHorizontal, FlipVertical, Brightness, Contrast, Saturation, Sharpness, WhiteBalance)
            : ((int, bool, bool, double, double, double, double, double)?)null;

        Task.Run(() =>
        {
            byte[]? bytes;
            if (adjustSnapshot is { } a)
                bytes = renderer.RenderPreviewWithAdjustments(corners, a.Item1, a.Item2, a.Item3, a.Item4, a.Item5, a.Item6, a.Item7, a.Item8);
            else if (dewarpSnapshot is { } d)
                bytes = renderer.RenderPreviewWithDewarp(corners, d);
            else
                bytes = renderer.RenderPreview(corners);
            if (bytes == null) return;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new Bitmap(ms);
                    if (isPrimary)
                    {
                        var old = PreviewImage;
                        PreviewImage = bitmap;
                        old?.Dispose();
                    }
                    else
                    {
                        var old = SecondaryPreviewImage;
                        SecondaryPreviewImage = bitmap;
                        old?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CropReviewViewModel] Preview decode failed: {ex}");
                }
            });
        });
    }

    partial void OnSplitPercentChanged(double value) => SchedulePreviewUpdate();
    partial void OnLeftQuadChanged(CropPoint[] value) => SchedulePreviewUpdate();
    partial void OnRightQuadChanged(CropPoint[] value) => SchedulePreviewUpdate();

    [RelayCommand]
    private void Reset()
    {
        if (Image == null) return;

        if (IsAdjustMode)
        {
            // Restores whatever was loaded/last-saved for this job, not necessarily zero —
            // matches the crop/dewarp Reset's own behavior of returning to the last-known-good
            // state rather than an arbitrary default.
            RotationDegrees = _loadedRotationDegrees;
            FlipHorizontal = _loadedFlipHorizontal;
            FlipVertical = _loadedFlipVertical;
            Brightness = _loadedBrightness;
            Contrast = _loadedContrast;
            Saturation = _loadedSaturation;
            Sharpness = _loadedSharpness;
            WhiteBalance = _loadedWhiteBalance;
            SchedulePreviewUpdate();
            return;
        }

        if (IsSplitBookPages && IsTwoQuadSplit)
        {
            if (_detectedLeftQuad != null && _detectedRightQuad != null)
            {
                LeftQuad = _detectedLeftQuad;
                RightQuad = _detectedRightQuad;
            }
        }
        else if (IsSplitBookPages)
        {
            SplitPercent = _detectedSplitPercent;
        }
        else
        {
            SetCorners(_detectedCorners ?? RectCorners(0, 0, ImageWidth, ImageHeight));
        }
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

        var job = await _dbContext.CaptureJobs.FindAsync(_jobId);
        if (job != null)
        {
            var previousLeftBox = job.LeftCropBox;
            var previousRightBox = job.RightCropBox;
            var wasAlreadyManual = job.ManualOverrideApplied;

            if (IsSplitBookPages && IsTwoQuadSplit)
            {
                job.LeftCropBox = FormatCorners(LeftQuad);
                job.RightCropBox = FormatCorners(RightQuad);
            }
            else if (IsSplitBookPages)
            {
                var leftWidth = Math.Clamp((int)(ImageWidth * (SplitPercent / 100.0)), 1, ImageWidth - 1);
                job.LeftCropBox = $"0,0,{leftWidth},{ImageHeight}";
                job.RightCropBox = $"{leftWidth},0,{ImageWidth - leftWidth},{ImageHeight}";
            }
            else
            {
                job.LeftCropBox = FormatCorners(TopLeft, TopRight, BottomRight, BottomLeft);
                job.RightCropBox = null;
            }

            // Only pin this job to the legacy manual-crop pipeline (see ImageProcessor.Process's
            // manualOverride branch) if the operator actually changed the crop geometry from
            // what was already there — a job that already had a saved manual crop stays manual
            // regardless (re-saving without touching it is still an explicit confirm of a manual
            // shape), but a fresh, never-reviewed job that gets opened and saved untouched should
            // stay on the automatic Method 4 path, not silently lose it forever.
            var geometryChanged = job.LeftCropBox != previousLeftBox || job.RightCropBox != previousRightBox;
            job.ManualOverrideApplied = wasAlreadyManual || geometryChanged;

            job.RotationDegrees = RotationDegrees;
            job.FlipHorizontal = FlipHorizontal;
            job.FlipVertical = FlipVertical;
            job.Brightness = Brightness;
            job.Contrast = Contrast;
            job.Saturation = Saturation;
            job.Sharpness = Sharpness;
            job.WhiteBalance = WhiteBalance;
            job.HasManualAdjustments = _loadedHasManualAdjustments || HasNonDefaultAdjustments;

            // Dewarp control points live in the crop-preview renderer's downscaled pixel space
            // (see SeedDewarpPoints) — convert back to full-resolution coordinates, matching
            // what the pipeline applies against the actual cropped page.
            if (_dewarpTouched && _previewRenderer != null)
            {
                var scale = _previewRenderer.Scale;
                var fullResModel = new DewarpModel(
                    ScalePoints(DewarpTopPoints, 1.0 / scale),
                    ScalePoints(DewarpBottomPoints, 1.0 / scale));
                job.DewarpCurve = ImageProcessor.FormatDewarpCurve(fullResModel);
                job.DewarpManualOverrideApplied = true;
            }

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

    // Explicit invariant-culture formatting: the delimiter is a comma, and several Windows
    // locales use ',' as the decimal separator — culture-sensitive formatting here would
    // silently corrupt every saved crop on those systems.
    private static string FormatCorners(params CropPoint[] corners) => string.Join(",", corners.SelectMany(c =>
        new[] { c.X.ToString("F1", CultureInfo.InvariantCulture), c.Y.ToString("F1", CultureInfo.InvariantCulture) }));

    private static CropPoint[] ScalePoints(CropPoint[] points, double factor) =>
        points.Select(p => new CropPoint(p.X * factor, p.Y * factor)).ToArray();

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
