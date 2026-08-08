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

    // Cached so Reset can re-apply the smart-detected starting point instead of falling back
    // to an unhelpful full-frame box or an unconditioned 50/50 split.
    private CropPoint[]? _detectedCorners;
    private double _detectedSplitPercent = 50.0;
    private CropPoint[]? _detectedLeftQuad;
    private CropPoint[]? _detectedRightQuad;

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

    // Live preview of the corrected image as it's edited. Split mode uses both (left/right);
    // single-page mode uses only PreviewImage.
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private Bitmap? _secondaryPreviewImage;

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    public CropReviewViewModel()
    {
        // Design-time constructor.
        _jobId = ""; _dbContext = null!; _queueService = null!; _imagePath = "";
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    }

    public CropReviewViewModel(string jobId, AppDbContext dbContext, CaptureQueueService queueService)
    {
        _jobId = jobId;
        _dbContext = dbContext;
        _queueService = queueService;
        _imagePath = string.Empty;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview();
        };

        var job = _dbContext.CaptureJobs.Find(jobId);
        if (job == null || !File.Exists(job.OriginalFilePath))
            return;

        _imagePath = job.OriginalFilePath;

        // Load the image and run detection on a background thread to avoid blocking the UI
        // when opening the dialog.
        Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(job.OriginalFilePath);
                var bmp = new Bitmap(stream);
                var batch = _dbContext.Batches.Find(job.BatchId);
                var isSplit = batch?.SplitBookPages == true;
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
                        var twoPage = processor.DetectSplitPageBoundaries(_imagePath);
                        if (twoPage.HasValue)
                        {
                            var (left, right) = twoPage.Value;
                            detectedLeftQuad = left.Quad ?? RectCorners(left.X, left.Y, left.Width, left.Height);
                            detectedRightQuad = right.Quad ?? RectCorners(right.X, right.Y, right.Width, right.Height);
                        }
                        else
                        {
                            detectedSplit = processor.DetectGutterSplitPercent(_imagePath);
                        }
                    }
                    else
                    {
                        var boundary = processor.DetectDocumentBoundary(_imagePath);
                        detectedCorners = boundary?.Quad ?? (boundary is { } b
                            ? RectCorners(b.X, b.Y, b.Width, b.Height)
                            : null);
                        detectedConfidence = boundary?.Confidence ?? 0.0;
                    }
                }

                var edgePoints = ImageProcessor.DetectEdgePoints(_imagePath);
                var previewRenderer = CropPreviewRenderer.Create(_imagePath);

                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        Image = bmp;
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

        // Snapshot under IsDewarpMode's current value, not re-read inside the background task —
        // the operator could toggle modes again before this frame finishes rendering.
        DewarpModel? dewarpSnapshot = IsDewarpMode
            ? new DewarpModel((CropPoint[])DewarpTopPoints.Clone(), (CropPoint[])DewarpBottomPoints.Clone())
            : null;

        Task.Run(() =>
        {
            var bytes = dewarpSnapshot is { } d ? renderer.RenderPreviewWithDewarp(corners, d) : renderer.RenderPreview(corners);
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

    [RelayCommand]
    private void Cancel(Window window)
    {
        window?.Close();
    }

    [RelayCommand]
    private async Task Save(Window window)
    {
        if (Image == null) { window?.Close(); return; }

        var job = await _dbContext.CaptureJobs.FindAsync(_jobId);
        if (job != null)
        {
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

            job.ManualOverrideApplied = true;

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

            // A reprocess must not leave stale derivatives eligible for export.
            var processedDirectory = Path.Combine(Path.GetDirectoryName(job.OriginalFilePath) ?? ".", "Processed");
            var baseName = Path.GetFileNameWithoutExtension(job.OriginalFilePath);
            if (Directory.Exists(processedDirectory))
            {
                foreach (var derivative in Directory.EnumerateFiles(processedDirectory, $"{baseName}*"))
                {
                    try { File.Delete(derivative); }
                    catch (IOException) { /* The worker will overwrite its own output on retry. */ }
                    catch (UnauthorizedAccessException) { /* Preserve the source job; report remains available. */ }
                }
            }

            await _dbContext.SaveChangesAsync();
            Saved?.Invoke(this, EventArgs.Empty);
        }

        window?.Close();
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
