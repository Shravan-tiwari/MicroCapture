using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MicroCapture.Core.Data;
using MicroCapture.Core.Services;

namespace MicroCapture.Processing;

/// <summary>
/// Background worker that polls the CaptureQueue for pending jobs 
/// and runs the image processing pipeline on each one.
/// Crash-safe: interrupted InProgress jobs are returned to Pending on worker startup.
/// </summary>
public class BackgroundProcessingWorker
{
    private readonly ImageProcessor _processor;
    private readonly string? _dbPath;
    private CancellationTokenSource? _cts;

    public event EventHandler<ProcessingResult>? JobCompleted;
    public event EventHandler<string>? StatusChanged;

    /// <param name="dbPath">Overrides the database file this worker polls — used by tests so
    /// they can exercise this exact class without touching the operator's real database
    /// (AppDbContext's own default path). Null (the real app's usage) keeps existing
    /// behavior exactly.</param>
    public BackgroundProcessingWorker(string? dbPath = null)
    {
        // The background worker creates its own AppDbContext instances for polling.
        // This avoids DbContext thread-safety issues while still processing the same
        // persisted local database created by the UI layer.
        _dbPath = dbPath;
        _processor = new ImageProcessor();
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        Task.Run(() => ProcessLoop(_cts.Token));
        StatusChanged?.Invoke(this, "Background processing started.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        StatusChanged?.Invoke(this, "Background processing stopped.");
    }

    private async Task ProcessLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var dbContext = _dbPath == null ? new AppDbContext() : new AppDbContext(_dbPath);
                var queueService = new CaptureQueueService(dbContext);
                await queueService.RecoverInterruptedJobsAsync();
                var pendingJobs = await queueService.GetPendingJobsAsync();

                if (pendingJobs.Count == 0)
                {
                    // No work — wait and retry
                    await Task.Delay(1000, token);
                    continue;
                }

                foreach (var job in pendingJobs)
                {
                    if (token.IsCancellationRequested) break;

                    // Mark as InProgress
                    await queueService.UpdateJobStatusAsync(job.Id, "processing", "InProgress");

                    // Determine output directory
                    var outputDir = Path.Combine(
                        Path.GetDirectoryName(job.OriginalFilePath) ?? ".",
                        "Processed");

                    bool splitPages = job.Batch?.SplitBookPages ?? false;
                    bool useFixedFrames = job.Batch?.UseFixedFrames == true && !string.IsNullOrWhiteSpace(job.Batch?.FixedFrames);
                    bool dewarpEnabled = job.Batch?.DewarpEnabled ?? false;
                    bool binarizeEnabled = job.Batch?.BinarizeEnabled ?? false;
                    var metadata = new TiffMetadata(job.Batch?.Dpi ?? 150, job.Batch?.Operator, job.Timestamp);
                    // Batch.CameraCalibration snapshots whichever lens calibration was active
                    // at Start Batch (see Batch.CameraCalibrationId's own comment) — parse its
                    // stored camera-matrix/distortion-coefficient strings back into the DTO
                    // ImageProcessor actually consumes. A batch with no calibration performed
                    // yet (CameraCalibration is null) just runs without lens undistortion.
                    var calibrationEntity = job.Batch?.CameraCalibration;
                    LensCalibration? lensCalibration = calibrationEntity != null
                        ? ImageProcessor.ParseLensCalibration($"{calibrationEntity.CameraMatrix};{calibrationEntity.DistCoeffs};{calibrationEntity.ImageWidth},{calibrationEntity.ImageHeight}")
                        : null;
                    bool bleedthroughEnabled = job.Batch?.BleedthroughEnabled ?? false;
                    var result = useFixedFrames
                        ? _processor.ProcessFixedFrames(job.OriginalFilePath, outputDir, job.Batch!.FixedFrames!, metadata, dewarpEnabled, job.DewarpCurve, job.DewarpManualOverrideApplied, binarizeEnabled, lensCalibration, bleedthroughEnabled, job.HasManualAdjustments, job.RotationDegrees, job.FlipHorizontal, job.FlipVertical, job.Brightness, job.Contrast, job.Saturation, job.Sharpness, job.WhiteBalance)
                        : _processor.Process(job.OriginalFilePath, outputDir, splitPages, job.ManualOverrideApplied, job.LeftCropBox, job.RightCropBox, metadata, dewarpEnabled, job.DewarpCurve, job.DewarpManualOverrideApplied, binarizeEnabled, lensCalibration, bleedthroughEnabled, job.HasManualAdjustments, job.RotationDegrees, job.FlipHorizontal, job.FlipVertical, job.Brightness, job.Contrast, job.Saturation, job.Sharpness, job.WhiteBalance);

                    if (result.Success && result.OutputFilePaths.Count > 0)
                    {
                        await queueService.UpdateJobStatusAsync(job.Id, "processing", "Completed");
                        await queueService.UpdateJobStatusAsync(job.Id, "qc", result.QcVerdict);
                        // OCR no longer runs automatically here — it's expensive (a subprocess
                        // per file, up to ~30s each) and often wasted work on pages that get
                        // recaptured or deleted before the batch is finalized. It now runs
                        // on-demand via BatchOcrService, either an explicit "Run OCR" action or
                        // automatically right before a PDF export. OcrStatus stays "Pending"
                        // (its DB default) until that happens.
                    }
                    else
                    {
                        await queueService.UpdateJobStatusAsync(job.Id, "processing", "Failed");
                    }

                    JobCompleted?.Invoke(this, result);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Processing error: {ex.Message}");
                await Task.Delay(2000, token);
            }
        }
    }
}
