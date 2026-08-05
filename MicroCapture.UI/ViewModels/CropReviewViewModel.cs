using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.Processing;

namespace MicroCapture.UI.ViewModels;

public partial class CropReviewViewModel : ViewModelBase
{
    private readonly string _jobId;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly string _imagePath;

    // Cached so Reset can re-apply the smart-detected starting point instead of
    // falling back to the (usually unhelpful) full-frame / exact-50% default.
    private DocumentBoundary? _detectedBoundary;
    private double _detectedSplitPercent = 50.0;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _splitPercent = 50.0;
    [ObservableProperty] private bool _isSplitBookPages;
    [ObservableProperty] private bool _isSinglePage;
    [ObservableProperty] private int _cropX;
    [ObservableProperty] private int _cropY;
    [ObservableProperty] private int _cropWidth;
    [ObservableProperty] private int _cropHeight;
    [ObservableProperty] private string _boundaryHintText = string.Empty;

    public CropReviewViewModel() { _jobId = ""; _dbContext = null!; _queueService = null!; _imagePath = ""; }

    public CropReviewViewModel(string jobId, AppDbContext dbContext, CaptureQueueService queueService)
    {
        _jobId = jobId;
        _dbContext = dbContext;
        _queueService = queueService;
        _imagePath = string.Empty;

        Console.WriteLine($"[CropReviewViewModel] Initializing for job {jobId}");
        var job = _dbContext.CaptureJobs.Find(jobId);
        if (job == null)
        {
            Console.WriteLine($"[CropReviewViewModel] Job {jobId} not found in database");
            return;
        }

        Console.WriteLine($"[CropReviewViewModel] Job found. OriginalFilePath: {job.OriginalFilePath}");
        if (!File.Exists(job.OriginalFilePath))
        {
            Console.WriteLine($"[CropReviewViewModel] Image file does not exist at: {job.OriginalFilePath}");
            return;
        }

        _imagePath = job.OriginalFilePath;

        // Load the image on a background thread to avoid blocking the UI when opening the dialog.
        Task.Run(() =>
        {
            try
            {
                Console.WriteLine($"[CropReviewViewModel] Background opening image file: {_imagePath}");
                using var stream = File.OpenRead(job.OriginalFilePath);
                var bmp = new Bitmap(stream);
                var batch = _dbContext.Batches.Find(job.BatchId);
                var isSplit = batch?.SplitBookPages == true;
                var hasSavedCrop = job.ManualOverrideApplied && !string.IsNullOrWhiteSpace(job.LeftCropBox);

                // Run the same smart detection the standard pipeline already uses, so the
                // review box starts from a sensible boundary/gutter instead of the full raw
                // frame or an unconditioned 50/50 split. Only needed when there's nothing
                // previously saved to restore instead.
                DocumentBoundary? detected = null;
                double detectedSplit = 50.0;
                if (!hasSavedCrop)
                {
                    var processor = new ImageProcessor();
                    if (isSplit)
                        detectedSplit = processor.DetectGutterSplitPercent(_imagePath);
                    else
                        detected = processor.DetectDocumentBoundary(_imagePath);
                }

                // Marshal results back to UI thread
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        Image = bmp;
                        Console.WriteLine($"[CropReviewViewModel] Image loaded successfully. Size: {Image.Size}");
                        IsSplitBookPages = isSplit;
                        IsSinglePage = !IsSplitBookPages;
                        Console.WriteLine($"[CropReviewViewModel] IsSplitBookPages: {IsSplitBookPages}, IsSinglePage: {IsSinglePage}");

                        _detectedBoundary = detected;
                        _detectedSplitPercent = detectedSplit;

                        if (hasSavedCrop)
                        {
                            CropWidth = (int)Image.Size.Width;
                            CropHeight = (int)Image.Size.Height;
                            if (IsSplitBookPages)
                                LoadSplitPercent(job.LeftCropBox!, (int)Image.Size.Width);
                            else
                                LoadCrop(job.LeftCropBox!);
                            BoundaryHintText = "Previously saved crop restored — adjust if needed.";
                        }
                        else if (IsSplitBookPages)
                        {
                            SplitPercent = detectedSplit;
                            BoundaryHintText = "Estimated split point — drag to adjust.";
                        }
                        else if (detected is { } boundary)
                        {
                            CropX = boundary.X;
                            CropY = boundary.Y;
                            CropWidth = boundary.Width;
                            CropHeight = boundary.Height;
                            BoundaryHintText = "Auto-detected boundary — adjust if needed.";
                        }
                        else
                        {
                            CropX = 0;
                            CropY = 0;
                            CropWidth = (int)Image.Size.Width;
                            CropHeight = (int)Image.Size.Height;
                            BoundaryHintText = "No boundary detected — showing full image.";
                        }
                    }
                    catch (Exception uiEx)
                    {
                        Console.WriteLine($"[CropReviewViewModel] Setting image on UI thread failed: {uiEx}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CropReviewViewModel] Background crop review image load failed: {ex}");
            }
        });
    }

    [RelayCommand]
    private void Reset()
    {
        if (Image == null) return;

        if (IsSplitBookPages)
        {
            SplitPercent = _detectedSplitPercent;
        }
        else if (_detectedBoundary is { } boundary)
        {
            CropX = boundary.X;
            CropY = boundary.Y;
            CropWidth = boundary.Width;
            CropHeight = boundary.Height;
        }
        else
        {
            CropX = 0;
            CropY = 0;
            CropWidth = (int)Image.Size.Width;
            CropHeight = (int)Image.Size.Height;
        }
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
            var imageWidth = (int)Image.Size.Width;
            var imageHeight = (int)Image.Size.Height;
            if (IsSplitBookPages)
            {
                var leftWidth = Math.Clamp((int)(imageWidth * (SplitPercent / 100.0)), 1, imageWidth - 1);
                job.LeftCropBox = $"0,0,{leftWidth},{imageHeight}";
                job.RightCropBox = $"{leftWidth},0,{imageWidth - leftWidth},{imageHeight}";
            }
            else
            {
                var x = Math.Clamp(CropX, 0, imageWidth - 1);
                var y = Math.Clamp(CropY, 0, imageHeight - 1);
                var width = Math.Clamp(CropWidth, 1, imageWidth - x);
                var height = Math.Clamp(CropHeight, 1, imageHeight - y);
                job.LeftCropBox = $"{x},{y},{width},{height}";
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

    private void LoadCrop(string crop)
    {
        var values = crop.Split(',');
        if (values.Length != 4 || !int.TryParse(values[0], out var x) || !int.TryParse(values[1], out var y) ||
            !int.TryParse(values[2], out var width) || !int.TryParse(values[3], out var height)) return;
        CropX = x; CropY = y; CropWidth = width; CropHeight = height;
    }

    /// <summary>Recovers the previously chosen split ratio from the saved left-crop width
    /// so reopening review on a split page doesn't silently reset it to 50/50.</summary>
    private void LoadSplitPercent(string leftCrop, int imageWidth)
    {
        if (imageWidth <= 0) return;
        var values = leftCrop.Split(',');
        if (values.Length != 4 || !int.TryParse(values[2], out var leftWidth)) return;
        SplitPercent = Math.Clamp(leftWidth * 100.0 / imageWidth, 1.0, 99.0);
    }
}
