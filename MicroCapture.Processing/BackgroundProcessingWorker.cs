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

                    // Processed derivatives now live in the same folder as the original capture
                    // (not a separate "Processed" subfolder) — originals are retained on disk
                    // until batch export, so there's no reason to segregate the derivative
                    // anymore, and Crop Review's re-crop-from-original still works unchanged
                    // since the original stays right where the derivative is written.
                    var outputDir = ProcessedFilePaths.OutputDirectoryFor(job.OriginalFilePath);

                    bool splitPages = job.Batch?.SplitBookPages ?? false;
                    bool useFixedFrames = job.Batch?.UseFixedFrames == true && !string.IsNullOrWhiteSpace(job.Batch?.FixedFrames);
                    bool dewarpEnabled = job.Batch?.DewarpEnabled ?? false;
                    bool binarizeEnabled = job.Batch?.BinarizeEnabled ?? false;
                    // job.Dpi (not job.Batch?.Dpi) — DPI is stamped onto each capture at the
                    // moment it's taken (see MainWindowViewModel.CaptureAsync), so a batch-wide
                    // DPI change after this page was captured can never retroactively change what
                    // this specific page renders at.
                    var metadata = new TiffMetadata(job.Dpi, job.Batch?.Operator, job.Timestamp);
                    // Batch.CameraCalibration snapshots whichever lens calibration was active
                    // at Start Batch (see Batch.CameraCalibrationId's own comment) — parse its
                    // stored camera-matrix/distortion-coefficient strings back into the DTO
                    // ImageProcessor actually consumes. A batch with no calibration performed
                    // yet (CameraCalibration is null) just runs without lens undistortion.
                    var calibrationEntity = job.Batch?.CameraCalibration;
                    LensCalibration? lensCalibration = calibrationEntity != null
                        ? ImageProcessor.ParseLensCalibration($"{calibrationEntity.CameraMatrix};{calibrationEntity.DistCoeffs};{calibrationEntity.ImageWidth},{calibrationEntity.ImageHeight}")
                        : null;
                    // The rig's real physically-measured DPI (see ImageProcessor.MeasuredDpi).
                    // If calibration exists and gives a real measurement, use it. Otherwise fall
                    // back to BaselineDpi (150) — the same "no real measurement, so treat the
                    // capture as already at the reference resolution" fallback MeasuredDpi itself
                    // uses. This must NOT be job.Dpi/job.Batch?.Dpi (the operator's TARGET output
                    // DPI): ResizeForDpi's scale is targetDpi/measuredDpi, so using the target as
                    // its own "measured" baseline collapses the scale to 1.0 and silently skips
                    // resampling — confirmed root cause of uncalibrated batches not upsampling at
                    // all regardless of the DPI the operator selected.
                    double measuredDpi;
                    if (calibrationEntity?.TargetWidthInches > 0 && calibrationEntity?.TargetHeightInches > 0
                        && calibrationEntity?.MeasuredPixelWidth.HasValue == true && calibrationEntity?.MeasuredPixelHeight.HasValue == true)
                    {
                        measuredDpi = ImageProcessor.MeasuredDpi(calibrationEntity);
                    }
                    else
                    {
                        measuredDpi = ImageProcessor.BaselineDpi;
                    }
                    bool bleedthroughEnabled = job.Batch?.BleedthroughEnabled ?? false;
                    var result = useFixedFrames
                        ? _processor.ProcessFixedFrames(job.OriginalFilePath, outputDir, job.Batch!.FixedFrames!, metadata, dewarpEnabled, job.DewarpCurve, job.DewarpManualOverrideApplied, binarizeEnabled, lensCalibration, bleedthroughEnabled, job.HasManualAdjustments, job.RotationDegrees, job.FlipHorizontal, job.FlipVertical, job.Brightness, job.Contrast, job.Saturation, job.Sharpness, job.WhiteBalance, frameReferenceWidth: job.Batch!.FixedFrameImageWidth, frameReferenceHeight: job.Batch!.FixedFrameImageHeight, measuredDpi: measuredDpi, captureFormat: job.CaptureFormat)
                        : _processor.Process(job.OriginalFilePath, outputDir, splitPages, job.ManualOverrideApplied, job.LeftCropBox, job.RightCropBox, metadata, dewarpEnabled, job.DewarpCurve, job.DewarpManualOverrideApplied, binarizeEnabled, lensCalibration, bleedthroughEnabled, job.HasManualAdjustments, job.RotationDegrees, job.FlipHorizontal, job.FlipVertical, job.Brightness, job.Contrast, job.Saturation, job.Sharpness, job.WhiteBalance, measuredDpi: measuredDpi, captureFormat: job.CaptureFormat);

                    if (result.Success && result.OutputFilePaths.Count > 0)
                    {
                        await queueService.UpdateJobStatusAsync(job.Id, "processing", "Completed");
                        await queueService.UpdateJobStatusAsync(job.Id, "qc", result.QcVerdict);
                        // Exact reference to the new main-folder output(s), so downstream
                        // readers (BatchExportService.GetProcessedFilesForJob) don't need to
                        // glob a "Processed" subfolder that no longer exists.
                        await queueService.SetProcessedFilePathAsync(job.Id, string.Join(";", result.OutputFilePaths));
                        // The original capture file is deliberately NOT deleted here — Crop
                        // Review needs it to still exist for as long as the batch might be
                        // re-cropped from scratch (see CropReviewViewModel, which loads from
                        // job.OriginalFilePath and silently fails if it's gone). Originals are
                        // cleaned up later, once the batch has actually been exported — see
                        // BatchExportService/FinalizeBatchViewModel.
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
