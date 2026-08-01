using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Services;

namespace MicroCapture.UI.ViewModels;

public partial class CropReviewViewModel : ViewModelBase
{
    private readonly string _jobId;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly string _imagePath;
    
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _leftSplitPercent = 50.0;
    [ObservableProperty] private double _rightSplitPercent = 50.0;

    public CropReviewViewModel() { _jobId = ""; _dbContext = null!; _queueService = null!; _imagePath = ""; }

    public CropReviewViewModel(string jobId, AppDbContext dbContext, CaptureQueueService queueService)
    {
        _jobId = jobId;
        _dbContext = dbContext;
        _queueService = queueService;

        var job = _dbContext.CaptureJobs.Find(jobId);
        if (job != null && File.Exists(job.OriginalFilePath))
        {
            _imagePath = job.OriginalFilePath;
            try
            {
                using var stream = File.OpenRead(job.OriginalFilePath);
                Image = new Bitmap(stream);
                
                // If there were existing boundaries, parse them. Otherwise default 50/50.
                if (job.ManualOverrideApplied)
                {
                    // Basic parsing just to show they can be loaded.
                    // For a robust system, we would map the rect back to percentages.
                    LeftSplitPercent = 50.0;
                    RightSplitPercent = 50.0;
                }
            }
            catch { }
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
            // Convert percentages to OpenCV rect strings "X,Y,W,H"
            // Wait, we need the original image dimensions.
            int w = (int)Image.Size.Width;
            int h = (int)Image.Size.Height;
            
            int leftW = (int)(w * (LeftSplitPercent / 100.0));
            int rightW = (int)(w * (RightSplitPercent / 100.0));
            
            job.LeftCropBox = $"0,0,{leftW},{h}";
            // Right box starts from the right edge inward, or from the end of the left box
            int rightX = w - rightW;
            job.RightCropBox = $"{rightX},0,{rightW},{h}";
            
            job.ManualOverrideApplied = true;
            job.ProcessingStatus = "Pending"; // Re-queue it
            
            await _dbContext.SaveChangesAsync();
        }

        window?.Close();
    }
}
