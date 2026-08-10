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

    // The background worker constructs a new CaptureQueueService (and AppDbContext) on
    // every poll iteration, always against the same database file. The schema is only
    // ever behind on the first construction seen for a given DbPath, so gate the
    // EnsureCreated/ALTER TABLE work behind a per-path cache instead of re-running
    // PRAGMA/table_info checks every second forever. Keyed by path (not a single global
    // flag) so tests/tools that legitimately point at multiple distinct database files
    // within one process still get correctly initialized schemas for each.
    private static readonly object SchemaCheckSync = new();
    private static readonly HashSet<string> _schemaCheckedPaths = new();

    /// <summary>Number of times the schema-compatibility work has actually executed for a
    /// given DbPath in this process. Exposed for regression testing (see tools/SmokeTest).</summary>
    public static int SchemaCheckRunCount { get; private set; }

    public CaptureQueueService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
        lock (SchemaCheckSync)
        {
            if (!_schemaCheckedPaths.Add(dbContext.DbPath)) return;
            // Older installations were created with EnsureCreated, so EF migrations were
            // never recorded. Upgrade those databases in place before any query includes
            // the newer crop/book-splitting columns.
            _dbContext.Database.EnsureCreated();
            EnsureCompatibleSchema();
            SchemaCheckRunCount++;
        }
    }

    private void EnsureCompatibleSchema()
    {
        EnsureColumn("Batches", "BatchCode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("Batches", "SplitBookPages", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("CaptureJobs", "ManualOverrideApplied", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("CaptureJobs", "LeftCropBox", "TEXT NULL");
        EnsureColumn("CaptureJobs", "RightCropBox", "TEXT NULL");
        EnsureColumn("Batches", "UseFixedFrames", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("Batches", "FixedFrames", "TEXT NULL");
        EnsureColumn("Batches", "FixedFrameImageWidth", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("Batches", "FixedFrameImageHeight", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("Batches", "PreferredExportFormat", "TEXT NOT NULL DEFAULT 'PDF'");
        EnsureColumn("Batches", "Dpi", "INTEGER NOT NULL DEFAULT 300");
        EnsureColumn("Batches", "DewarpEnabled", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("CaptureJobs", "DewarpManualOverrideApplied", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("CaptureJobs", "DewarpCurve", "TEXT NULL");
        EnsureColumn("Batches", "BinarizeEnabled", "INTEGER NOT NULL DEFAULT 0");
    }

    private void EnsureColumn(string table, string column, string definition)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                pragmaCmd.ExecuteNonQuery();
            }
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

    /// <summary>The batch's final page set: latest non-superseded, successfully processed
    /// capture per page number — i.e. exactly what export will ship. Shared by export and
    /// on-demand OCR so both always operate on the same set.</summary>
    public static List<CaptureJob> GetCompletedJobsForBatch(IEnumerable<CaptureJob> captures) =>
        captures
            .Where(j => j.ProcessingStatus == "Completed" && j.ExportStatus != "Superseded")
            .GroupBy(j => j.PageNumber)
            .Select(group => group.OrderByDescending(j => j.Timestamp).First())
            .OrderBy(j => j.PageNumber)
            .ToList();

    /// <summary>Queries and returns the batch's final page set. See <see cref="GetCompletedJobsForBatch"/>.</summary>
    public async Task<List<CaptureJob>> GetCompletedJobsForBatchAsync(string batchId)
    {
        var captures = await _dbContext.CaptureJobs
            .AsNoTracking()
            .Where(j => j.BatchId == batchId)
            .ToListAsync();
        return GetCompletedJobsForBatch(captures);
    }

    /// <summary>Recovers work interrupted by an application or power failure.</summary>
    public async Task<int> RecoverInterruptedJobsAsync()
    {
        var interrupted = await _dbContext.CaptureJobs
            .Where(job => job.ProcessingStatus == "InProgress")
            .ToListAsync();
        foreach (var job in interrupted)
            job.ProcessingStatus = "Pending";
        if (interrupted.Count > 0)
            await _dbContext.SaveChangesAsync();
        return interrupted.Count;
    }

    /// <summary>Supersedes previous attempts for a page before recording a recapture.</summary>
    public async Task SupersedePageAsync(string batchId, int pageNumber)
    {
        var priorAttempts = await _dbContext.CaptureJobs
            .Where(job => job.BatchId == batchId && job.PageNumber == pageNumber && job.ProcessingStatus != "Superseded")
            .ToListAsync();
        foreach (var job in priorAttempts)
        {
            job.ProcessingStatus = "Superseded";
            job.ExportStatus = "Superseded";
        }
        if (priorAttempts.Count > 0)
            await _dbContext.SaveChangesAsync();
    }

    /// <summary>Removes a mistakenly captured page from the batch. Reuses the same "Superseded"
    /// mechanism a recapture already relies on — the row stays in the database (audit trail
    /// intact) but is excluded from the pending-processing queue and from export, exactly like
    /// a superseded recapture attempt. Nothing downstream needs a separate "deleted" concept.</summary>
    public async Task<CaptureJob?> DeleteCaptureAsync(string jobId)
    {
        var job = await _dbContext.CaptureJobs.FindAsync(jobId);
        if (job == null) return null;
        job.ProcessingStatus = "Superseded";
        job.ExportStatus = "Superseded";
        await _dbContext.SaveChangesAsync();
        return job;
    }

    public async Task UpdateJobStatusAsync(string jobId, string statusType, string newStatus)
    {
        var job = await _dbContext.CaptureJobs.FindAsync(jobId);
        if (job != null)
        {
            // A recapture can supersede this job while it is mid-flight. Once superseded,
            // no later status write (e.g. the worker finishing the now-stale attempt) may
            // resurrect it, or it becomes eligible for export alongside its replacement.
            if (job.ProcessingStatus == "Superseded")
                return;

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
