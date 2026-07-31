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
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly ImageProcessor _processor;
    private CancellationTokenSource? _cts;

    public event EventHandler<ProcessingResult>? JobCompleted;
    public event EventHandler<string>? StatusChanged;

    public BackgroundProcessingWorker(AppDbContext dbContext, CaptureQueueService queueService)
    {
        _dbContext = dbContext;
        _queueService = queueService;
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
                var pendingJobs = await _queueService.GetPendingJobsAsync();

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
                    await _queueService.UpdateJobStatusAsync(job.Id, "processing", "InProgress");

                    // Determine output directory
                    var outputDir = Path.Combine(
                        Path.GetDirectoryName(job.OriginalFilePath) ?? ".",
                        "Processed");

                    var result = _processor.Process(job.OriginalFilePath, outputDir);

                    if (result.Success)
                    {
                        await _queueService.UpdateJobStatusAsync(job.Id, "processing", "Completed");
                        await _queueService.UpdateJobStatusAsync(job.Id, "qc", result.QcVerdict);
                    }
                    else
                    {
                        await _queueService.UpdateJobStatusAsync(job.Id, "processing", "Failed");
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
