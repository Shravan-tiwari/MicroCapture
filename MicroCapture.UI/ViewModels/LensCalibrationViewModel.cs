using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Interfaces;
using MicroCapture.Core.Models;
using MicroCapture.Processing;

namespace MicroCapture.UI.ViewModels;

/// <summary>Drives the one-time camera lens intrinsics/distortion calibration flow: repeatedly
/// capture a physical ChArUco board at varied tilts/positions, then compute and save the
/// calibration via <see cref="LensCalibrationService"/>. Unlike <see cref="FrameCalibrationViewModel"/>
/// (which edits a single already-captured still image), this owns the repeated-capture loop
/// itself — there's no single "the calibration image" here, so the natural analogue of that
/// class's constructor-takes-an-image-path shape doesn't fit; this takes the camera service
/// instead and calls it directly per "Capture" click.</summary>
public partial class LensCalibrationViewModel : ViewModelBase
{
    // Matches the real-world sample set this feature was validated against (20 "left turn" /
    // 20 "right turn" board tilt poses) — a reasonable target for good coverage, not a hard
    // requirement (Calibrate itself only requires 3+ usable images).
    public const int RecommendedCaptureCount = 12;

    // Cv2.CalibrateCamera's own RMS reprojection error, in pixels — literature commonly cites
    // under ~0.5px as good, under ~1.0px as acceptable; above that the operator is nudged to
    // recapture with more/more-varied shots rather than silently accepting a poor calibration.
    public const double ReprojectionErrorWarningThresholdPx = 1.0;

    private readonly AppDbContext _dbContext;
    private readonly ICameraService _cameraService;
    private readonly string _outputDirectory;
    private readonly string? _cameraLabel;
    private readonly System.Collections.Generic.List<string> _capturedImagePaths = new();

    /// <summary>Raised after a successful Save.</summary>
    public event EventHandler? Saved;

    /// <summary>Raised when the operator cancels.</summary>
    public event EventHandler? Cancelled;

    [ObservableProperty] private int _goodCaptureCount;
    [ObservableProperty] private string _statusText = "Point the camera at the calibration board and capture several shots at varied tilts/positions.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private double _reprojectionErrorPx;
    [ObservableProperty] private bool _resultLooksGood;
    [ObservableProperty] private int _imagesUsedInResult;

    public ObservableCollection<string> Log { get; } = new();

    private LensCalibrationService.CalibrationOutcome? _outcome;

    public LensCalibrationViewModel()
    {
        // Design-time constructor.
        _dbContext = null!;
        _cameraService = null!;
        _outputDirectory = ".";
    }

    public LensCalibrationViewModel(AppDbContext dbContext, ICameraService cameraService, string outputDirectory, string? cameraLabel)
    {
        _dbContext = dbContext;
        _cameraService = cameraService;
        _outputDirectory = outputDirectory;
        _cameraLabel = cameraLabel;
        Directory.CreateDirectory(_outputDirectory);
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "Capturing...";
            var path = await _cameraService.CaptureAsync(_outputDirectory, $"lenscalib_{DateTime.Now:yyyyMMddHHmmssfff}");
            var markerCount = await Task.Run(() => LensCalibrationService.CountDetectableMarkers(path));

            if (markerCount >= LensCalibrationService.MinMarkersPerImage)
            {
                _capturedImagePaths.Add(path);
                GoodCaptureCount = _capturedImagePaths.Count;
                Log.Insert(0, $"Capture {GoodCaptureCount}: OK ({markerCount} markers detected)");
                StatusText = GoodCaptureCount >= RecommendedCaptureCount
                    ? $"{GoodCaptureCount} good captures — enough for a solid calibration, or keep going for more coverage."
                    : $"{GoodCaptureCount} good capture(s) — aim for {RecommendedCaptureCount}+ across varied tilts/positions before computing.";
            }
            else
            {
                Log.Insert(0, $"Capture rejected: only {markerCount} marker(s) detected (need {LensCalibrationService.MinMarkersPerImage}+) — reposition the board and retry.");
                StatusText = "Board not clearly detected in that shot — reposition and retry.";
            }
        }
        catch (Exception ex)
        {
            Log.Insert(0, $"Capture failed: {ex.Message}");
            StatusText = "Capture failed — see log below.";
        }
        finally
        {
            IsBusy = false;
            ComputeCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCompute() => !IsBusy && _capturedImagePaths.Count >= 3;

    [RelayCommand(CanExecute = nameof(CanCompute))]
    private async Task ComputeAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = $"Computing calibration from {_capturedImagePaths.Count} image(s)...";
            var paths = _capturedImagePaths.ToList();
            var outcome = await Task.Run(() => LensCalibrationService.Calibrate(paths));
            _outcome = outcome;

            foreach (var w in outcome.Warnings) Log.Insert(0, w);

            HasResult = true;
            ImagesUsedInResult = outcome.ImagesUsed;
            ReprojectionErrorPx = outcome.ReprojectionErrorPx;
            ResultLooksGood = outcome.Success && outcome.ReprojectionErrorPx <= ReprojectionErrorWarningThresholdPx;

            StatusText = outcome.Success
                ? ResultLooksGood
                    ? $"Calibration computed from {outcome.ImagesUsed}/{outcome.ImagesTotal} images — reprojection error {outcome.ReprojectionErrorPx:F2}px. Looks good — Save when ready."
                    : $"Calibration computed from {outcome.ImagesUsed}/{outcome.ImagesTotal} images — reprojection error {outcome.ReprojectionErrorPx:F2}px, higher than ideal (<{ReprojectionErrorWarningThresholdPx:F1}px). Consider capturing more/more-varied shots before saving."
                : "Calibration failed — see log below for why.";
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanSave() => !IsBusy && _outcome is { Success: true };

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_outcome is not { Success: true, Calibration: { } calib }) return;

        IsBusy = true;
        try
        {
            // Only one calibration should be "active" (the one new batches snapshot at Start
            // Batch) at a time — deactivate whatever was active before this save.
            var previouslyActive = await _dbContext.CameraCalibrations.Where(c => c.IsActive).ToListAsync();
            foreach (var previous in previouslyActive) previous.IsActive = false;

            // FormatLensCalibration's "fx,fy,cx,cy;k1,...;w,h" shape is the single source of
            // truth for this format — split it rather than re-deriving the same formatting
            // logic here, so this row and BackgroundProcessingWorker's later reconstruction of
            // it can never drift apart.
            var formatted = ImageProcessor.FormatLensCalibration(calib).Split(';');

            var row = new CameraCalibration
            {
                Label = string.IsNullOrWhiteSpace(_cameraLabel) ? $"Calibration {DateTime.Now:yyyy-MM-dd HH:mm}" : _cameraLabel!,
                CalibratedUtc = DateTime.UtcNow,
                ImageWidth = calib.CalibratedWidth,
                ImageHeight = calib.CalibratedHeight,
                CameraMatrix = formatted[0],
                DistCoeffs = formatted[1],
                ReprojectionErrorPx = _outcome.Value.ReprojectionErrorPx,
                IsActive = true,
            };
            _dbContext.CameraCalibrations.Add(row);
            await _dbContext.SaveChangesAsync();

            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Insert(0, $"Save failed: {ex.Message}");
            StatusText = "Save failed — see log below.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);
}
