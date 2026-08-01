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
/// Crash-safe: jobs remain "Pending" until processing completes and DB is updated.
/// </summary>
public class BackgroundProcessingWorker
{
    private readonly ImageProcessor _processor;
    private CancellationTokenSource? _cts;

    public event EventHandler<ProcessingResult>? JobCompleted;
    public event EventHandler<string>? StatusChanged;

    public BackgroundProcessingWorker(AppDbContext dbContext, CaptureQueueService queueService)
    {
        // DbContext is not thread-safe. The UI owns the context passed here; this worker
        // creates an independent context for each poll so batch creation and capture do
        // not race the processing loop.
        _processor = new ImageProcessor();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => ProcessLoop(_cts.Token));
        StatusChanged?.Invoke(this, "Background processing started.");
    }

    public void Stop()
    {
        _cts?.Cancel();
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

                        // Perform OCR on all output files
                        try
                        {
                            var ocrProcessor = new OcrProcessor();
                            foreach (var outputPath in result.OutputFilePaths)
                            {
                                string txtPath = ocrProcessor.ProcessImage(outputPath);
                            }
                            await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Completed");
                            result.OcrStatus = "Completed";
                        }
                        catch (Exception ex)
                        {
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
