using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;

namespace MicroCapture.Core.Services;

public class CaptureQueueService
{
    private readonly AppDbContext _dbContext;

    public CaptureQueueService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
        // Ensure database is created (for Phase 3)
        _dbContext.Database.EnsureCreated();
    }

    public async Task<CaptureJob> EnqueueCaptureAsync(string batchId, string originalFilePath, int pageNumber)
    {
        var job = new CaptureJob
        {
            BatchId = batchId,
            OriginalFilePath = originalFilePath,
            PageNumber = pageNumber,
            Timestamp = DateTime.UtcNow,
            ProcessingStatus = "Pending"
        };

        _dbContext.CaptureJobs.Add(job);
        await _dbContext.SaveChangesAsync();
        
        return job;
    }

    public async Task<List<CaptureJob>> GetPendingJobsAsync()
    {
        return await _dbContext.CaptureJobs
            .Include(j => j.Batch)
            .Where(j => j.ProcessingStatus == "Pending")
            .OrderBy(j => j.Timestamp)
            .ToListAsync();
    }

    public async Task UpdateJobStatusAsync(string jobId, string statusType, string newStatus)
    {
        var job = await _dbContext.CaptureJobs.FindAsync(jobId);
        if (job != null)
        {
            switch (statusType.ToLower())
            {
                case "processing": job.ProcessingStatus = newStatus; break;
                case "qc": job.QcStatus = newStatus; break;
                case "ocr": job.OcrStatus = newStatus; break;
                case "export": job.ExportStatus = newStatus; break;
            }
            await _dbContext.SaveChangesAsync();
        }
    }
}
