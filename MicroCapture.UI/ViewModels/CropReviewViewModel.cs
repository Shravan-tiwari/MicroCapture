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

    // Cached once at load for fast, non-blocking corner-snapping and live preview during
    // interactive dragging — never re-computed per drag frame.
    private IReadOnlyList<CropPoint> _edgePoints = Array.Empty<CropPoint>();
    private CropPreviewRenderer? _previewRenderer;
    private readonly DispatcherTimer _previewTimer;

    private const double SnapRadiusPixels = 15.0;

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
                var imageWidth = (int)bmp.Size.Width;
                var imageHeight = (int)bmp.Size.Height;

                CropPoint[]? savedCorners = null;
                CropPoint[]? detectedCorners = null;
                double detectedSplit = 50.0;

                if (hasSavedCrop)
                {
                    savedCorners = ImageProcessor.ParseCropShape(job.LeftCropBox!, imageWidth, imageHeight);
                }
                else
                {
                    var processor = new ImageProcessor();
                    if (isSplit)
                    {
                        detectedSplit = processor.DetectGutterSplitPercent(_imagePath);
                    }
                    else
                    {
                        var boundary = processor.DetectDocumentBoundary(_imagePath);
                        detectedCorners = boundary?.Quad ?? (boundary is { } b
                            ? RectCorners(b.X, b.Y, b.Width, b.Height)
                            : null);
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

                        if (hasSavedCrop && savedCorners != null)
                        {
                            if (isSplit)
                            {
                                // Derive the split ratio from the saved left box's own width —
                                // works whether it was saved as a legacy rect or a quad.
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
                        else if (isSplit)
                        {
                            SplitPercent = detectedSplit;
                            BoundaryHintText = "Estimated split point — drag the line to adjust.";
                        }
                        else if (detectedCorners != null)
                        {
                            SetCorners(detectedCorners);
                            BoundaryHintText = "Auto-detected boundary — drag a corner to adjust.";
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

        if (IsSplitBookPages)
        {
            var splitX = ImageWidth * (SplitPercent / 100.0);
            var left = RectCorners(0, 0, splitX, ImageHeight);
            var right = RectCorners(splitX, 0, ImageWidth - splitX, ImageHeight);
            RenderPreviewInto(left, isPrimary: true);
            RenderPreviewInto(right, isPrimary: false);
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

        Task.Run(() =>
        {
            var bytes = renderer.RenderPreview(corners);
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

    [RelayCommand]
    private void Reset()
    {
        if (Image == null) return;

        if (IsSplitBookPages)
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
            if (IsSplitBookPages)
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
        }

        window?.Close();
    }

    // Explicit invariant-culture formatting: the delimiter is a comma, and several Windows
    // locales use ',' as the decimal separator — culture-sensitive formatting here would
    // silently corrupt every saved crop on those systems.
    private static string FormatCorners(params CropPoint[] corners) => string.Join(",", corners.SelectMany(c =>
        new[] { c.X.ToString("F1", CultureInfo.InvariantCulture), c.Y.ToString("F1", CultureInfo.InvariantCulture) }));

    public void Dispose()
    {
        _previewTimer.Stop();
        _previewRenderer?.Dispose();
        _previewRenderer = null;
    }
}
