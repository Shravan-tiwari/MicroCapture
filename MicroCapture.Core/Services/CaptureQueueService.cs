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
        // Older installations were created with EnsureCreated, so EF migrations were
        // never recorded. Upgrade those databases in place before any query includes
        // the newer crop/book-splitting columns.
        _dbContext.Database.EnsureCreated();
        EnsureCompatibleSchema();
    }

    private void EnsureCompatibleSchema()
    {
        EnsureColumn("Batches", "BatchCode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("Batches", "SplitBookPages", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("CaptureJobs", "ManualOverrideApplied", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("CaptureJobs", "LeftCropBox", "TEXT NULL");
        EnsureColumn("CaptureJobs", "RightCropBox", "TEXT NULL");
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            {
                using var check = connection.CreateCommand();
                check.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = check.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
            alter.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
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
