using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MicroCapture.Core.Data;
using MicroCapture.Core.Services;

namespace MicroCapture.Processing;

/// <summary>Runs OCR on demand for a whole batch's finalized page set, instead of automatically
/// per capture. Reuses a single <see cref="OcrProcessor"/> across the run (its own preflight
/// checks are cached process-wide) and only touches jobs whose <c>OcrStatus</c> isn't already
/// "Completed", so re-running after a partial failure or a later export is cheap.</summary>
public class BatchOcrService
{
    private readonly AppDbContext _dbContext;
    private OcrProcessor? _ocrProcessor;

    public BatchOcrService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Runs OCR for every completed, non-superseded page in the batch that doesn't
    /// already have OcrStatus "Completed". Reports (done, total) after each page. Never throws
    /// for an individual page's OCR failure — that page's OcrStatus is set to "Failed"/"Skipped"
    /// and the run continues; only a genuine batch-lookup failure throws.</summary>
    public async Task RunOcrForBatchAsync(string batchId, IProgress<(int Done, int Total)>? progress = null, CancellationToken token = default)
    {
        var queueService = new CaptureQueueService(_dbContext);
        var jobs = (await queueService.GetCompletedJobsForBatchAsync(batchId))
            .Where(j => j.OcrStatus != "Completed")
            .ToList();

        var total = jobs.Count;
        var done = 0;
        progress?.Report((done, total));
        if (total == 0) return;

        OcrProcessor? ocrProcessor;
        try
        {
            ocrProcessor = _ocrProcessor ??= new OcrProcessor();
        }
        catch (Exception initEx)
        {
            Console.Error.WriteLine($"OCR initialization failed: {initEx}");
            foreach (var job in jobs)
            {
                await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Skipped");
                done++;
                progress?.Report((done, total));
            }
            return;
        }

        var allowManaged = string.Equals(Environment.GetEnvironmentVariable("MICROCAPTURE_ALLOW_MANAGED_TESS"), "1");

        foreach (var job in jobs)
        {
            token.ThrowIfCancellationRequested();
            if (!ocrProcessor.CliAvailable && !allowManaged)
            {
                Console.Error.WriteLine($"OCR skipped for job {job.Id}: tesseract CLI not available and managed wrapper disabled");
                await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Skipped");
            }
            else
            {
                try
                {
                    var files = BatchExportService.GetProcessedFilesForJob(job);
                    foreach (var file in files)
                        ocrProcessor.ProcessImage(file);
                    await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Completed");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"OCR exception for job {job.Id}: {ex}");
                    await queueService.UpdateJobStatusAsync(job.Id, "ocr", "Failed");
                }
            }

            done++;
            progress?.Report((done, total));
        }
    }
}
