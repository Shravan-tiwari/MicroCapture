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
    private CancellationTokenSource? _cts;

    public event EventHandler<ProcessingResult>? JobCompleted;
    public event EventHandler<string>? StatusChanged;

    public BackgroundProcessingWorker()
    {
        // The background worker creates its own AppDbContext instances for polling.
        // This avoids DbContext thread-safety issues while still processing the same
        // persisted local database created by the UI layer.
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
                using var dbContext = new AppDbContext();
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
                    var result = _processor.Process(job.OriginalFilePath, outputDir, splitPages, job.ManualOverrideApplied, job.LeftCropBox, job.RightCropBox);

                    if (result.Success && result.OutputFilePaths.Count > 0)
                    {
                        await queueService.UpdateJobStatusAsync(job.Id, "processing", "Completed");
                        await queueService.UpdateJobStatusAsync(job.Id, "qc", result.QcVerdict);

                        // Perform OCR on all output files. If Tesseract/tessdata is not available, skip OCR gracefully.
                        try
                        {
                            // Attempt to construct the OCR processor. This may throw if tessdata or native libs are missing.
                            OcrProcessor? ocrProcessor = null;
                            try
                            {
                                ocrProcessor = new OcrProcessor();
                            }
                            catch (Exception initEx)
                            {
                                // If initialization fails, log and skip OCR for this job (and future jobs).
                                Console.Error.WriteLine($"OCR initialization failed: {initEx}");
                                StatusChanged?.Invoke(this, $"OCR unavailable: {initEx.Message}");
                                await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Skipped");
                                result.OcrStatus = "Skipped";
                                ocrProcessor = null;
                            }

                            if (ocrProcessor != null)
                            {
                                // If tesseract CLI is not available and managed wrapper is not explicitly allowed,
                                // skip OCR to avoid native interop crashes.
                                var allowManaged = string.Equals(Environment.GetEnvironmentVariable("MICROCAPTURE_ALLOW_MANAGED_TESS"), "1");
                                if (!ocrProcessor.CliAvailable && !allowManaged)
                                {
                                    Console.Error.WriteLine($"OCR skipped for job {job.Id}: tesseract CLI not available and managed wrapper disabled");
                                    StatusChanged?.Invoke(this, "OCR unavailable: tesseract CLI not found");
                                    await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Skipped");
                                    result.OcrStatus = "Skipped";
                                }
                                else
                                {
                                    foreach (var outputPath in result.OutputFilePaths)
                                    {
                                        string txtPath = ocrProcessor.ProcessImage(outputPath);
                                    }
                                    await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Completed");
                                    result.OcrStatus = "Completed";
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log full exception for diagnostics (stack trace and inner exceptions)
                            Console.Error.WriteLine($"OCR exception for job {job.Id}: {ex}");
                            StatusChanged?.Invoke(this, $"OCR error: {ex.Message}");
                            await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Failed");
                            result.OcrStatus = "Failed";
                        }
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
