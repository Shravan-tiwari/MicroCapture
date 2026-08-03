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

namespace MicroCapture.UI.ViewModels;

public partial class CropReviewViewModel : ViewModelBase
{
    private readonly string _jobId;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly string _imagePath;
    
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _splitPercent = 50.0;
    [ObservableProperty] private bool _isSplitBookPages;
    [ObservableProperty] private bool _isSinglePage;
    [ObservableProperty] private int _cropX;
    [ObservableProperty] private int _cropY;
    [ObservableProperty] private int _cropWidth;
    [ObservableProperty] private int _cropHeight;

    public CropReviewViewModel() { _jobId = ""; _dbContext = null!; _queueService = null!; _imagePath = ""; }

    public CropReviewViewModel(string jobId, AppDbContext dbContext, CaptureQueueService queueService)
    {
        _jobId = jobId;
        _dbContext = dbContext;
        _queueService = queueService;
        _imagePath = string.Empty;

        var job = _dbContext.CaptureJobs.Find(jobId);
        if (job != null && File.Exists(job.OriginalFilePath))
        {
            _imagePath = job.OriginalFilePath;
            try
            {
                using var stream = File.OpenRead(job.OriginalFilePath);
                Image = new Bitmap(stream);
                CropWidth = (int)Image.Size.Width;
                CropHeight = (int)Image.Size.Height;
                var batch = _dbContext.Batches.Find(job.BatchId);
                IsSplitBookPages = batch?.SplitBookPages == true;
                IsSinglePage = !IsSplitBookPages;
                if (job.ManualOverrideApplied && !string.IsNullOrWhiteSpace(job.LeftCropBox))
                    LoadCrop(job.LeftCropBox);
            }
            catch (Exception ex) { Console.Error.WriteLine($"Crop review image load failed: {ex}"); }
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
}
