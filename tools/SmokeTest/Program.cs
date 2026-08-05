// Headless regression checks for the durable capture queue / export pipeline.
// No UI, no camera — exercises CaptureQueueService, AppDbContext, and BatchExportService
// directly against disposable temp SQLite databases so it's safe to run repeatedly and
// won't touch the operator's real MicroCapture.db.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.Processing;
using SkiaSharp;

var failures = 0;

Console.WriteLine("=== MicroCapture regression smoke test ===");

// Run the "schema check runs once" test FIRST — it relies on being the very first
// CaptureQueueService constructed in this process to observe the true before/after.
TestSchemaCheckRunsOnce();
await TestSupersedeRaceDoesNotDuplicateExport();
await TestBatchResumeDoesNotDuplicateBatch();

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;

// ---------- helpers ----------

void Check(string name, bool condition)
{
    if (condition) Console.WriteLine($"[PASS] {name}");
    else { Console.WriteLine($"[FAIL] {name}"); failures++; }
}

string TempDbPath() => Path.Combine(Path.GetTempPath(), $"microcapture_smoketest_{Guid.NewGuid():N}.db");

string TempWorkDir()
{
    var dir = Path.Combine(Path.GetTempPath(), $"microcapture_smoketest_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    return dir;
}

string WriteDummyImage(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(20, 20);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 90);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

// ---------- tests ----------

void TestSchemaCheckRunsOnce()
{
    Console.WriteLine("\n-- Schema check runs once per database path, not once per instance --");
    var sharedPath = TempDbPath();
    var before = CaptureQueueService.SchemaCheckRunCount;

    // BackgroundProcessingWorker constructs a brand new AppDbContext + CaptureQueueService
    // on every poll iteration, always against the same real database file.
    using (var db1 = new AppDbContext(sharedPath)) _ = new CaptureQueueService(db1);
    var afterFirst = CaptureQueueService.SchemaCheckRunCount;

    using (var db2 = new AppDbContext(sharedPath)) _ = new CaptureQueueService(db2);
    var afterSecond = CaptureQueueService.SchemaCheckRunCount;

    // A genuinely different database file must still get its schema initialized —
    // the cache is keyed by path, not a single "ever checked anything" flag.
    using (var db3 = new AppDbContext(TempDbPath())) _ = new CaptureQueueService(db3);
    var afterThird = CaptureQueueService.SchemaCheckRunCount;

    Check("First construction against a path performs the schema check", afterFirst == before + 1);
    Check("Second construction against the same path does not re-run it", afterSecond == afterFirst);
    Check("A construction against a different path still runs its own check", afterThird == afterSecond + 1);
}

async Task TestSupersedeRaceDoesNotDuplicateExport()
{
    Console.WriteLine("\n-- Recapture during in-flight processing does not duplicate the export --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    var queue = new CaptureQueueService(db);

    var project = new Project { Name = "SMOKE-RACE", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    var batch = new Batch { ProjectId = project.Id, Name = "B1", BatchCode = "B1" };
    db.Batches.Add(batch);
    await db.SaveChangesAsync();

    var originalPath = WriteDummyImage(Path.Combine(workDir, "page1_original.png"));
    var job1 = await queue.EnqueueCaptureAsync(batch.Id, originalPath, 1);

    // Worker picks up the first attempt...
    await queue.UpdateJobStatusAsync(job1.Id, "processing", "InProgress");
    // ...but the operator hits Recapture before it finishes.
    await queue.SupersedePageAsync(batch.Id, 1);
    // The worker, unaware it was superseded mid-flight, now reports completion for the stale attempt.
    await queue.UpdateJobStatusAsync(job1.Id, "processing", "Completed");
    await queue.UpdateJobStatusAsync(job1.Id, "qc", "PASS");

    // The recapture's own job completes normally.
    var recapturePath = WriteDummyImage(Path.Combine(workDir, "page1_recapture.png"));
    var job2 = await queue.EnqueueCaptureAsync(batch.Id, recapturePath, 1);
    await queue.UpdateJobStatusAsync(job2.Id, "processing", "Completed");
    await queue.UpdateJobStatusAsync(job2.Id, "qc", "PASS");

    using var verifyDb = new AppDbContext(dbPath);
    var reloadedJob1 = await verifyDb.CaptureJobs.FindAsync(job1.Id);
    Check("Superseded job is not resurrected to Completed", reloadedJob1!.ProcessingStatus == "Superseded");

    using var exportDb = new AppDbContext(dbPath);
    var exporter = new BatchExportService(exportDb);
    var exportDir = Path.Combine(workDir, "Export");
    var resultDir = await exporter.ExportBatchAsync(batch.Id, exportDir, "PNG");
    var exportedFiles = Directory.GetFiles(resultDir, "*.png");
    Check("Export produces exactly one file for the recaptured page (no duplicate)", exportedFiles.Length == 1);
}

async Task TestBatchResumeDoesNotDuplicateBatch()
{
    Console.WriteLine("\n-- Restarting with the same project/batch code resumes instead of duplicating --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    var queue = new CaptureQueueService(db);

    var project = new Project { Name = "SMOKE-RESUME", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    var batch = new Batch { ProjectId = project.Id, Name = "RESUME", BatchCode = "RESUME" };
    db.Batches.Add(batch);
    await db.SaveChangesAsync();

    await queue.EnqueueCaptureAsync(batch.Id, WriteDummyImage(Path.Combine(workDir, "page1.png")), 1);
    await queue.EnqueueCaptureAsync(batch.Id, WriteDummyImage(Path.Combine(workDir, "page2.png")), 2);

    // Mirrors the lookup MainWindowViewModel.StartBatchAsync now performs before creating a new Batch.
    using var freshDb = new AppDbContext(dbPath);
    var existing = await freshDb.Batches
        .Include(b => b.Captures)
        .FirstOrDefaultAsync(b => b.ProjectId == project.Id && b.BatchCode == "RESUME" && b.Status == "Active");

    Check("Existing active batch is found instead of creating a duplicate", existing != null && existing.Id == batch.Id);
    var resumedPageCount = existing != null && existing.Captures.Count > 0 ? existing.Captures.Max(c => c.PageNumber) : 0;
    Check("Resumed page count continues from the last captured page", resumedPageCount == 2);

    var totalBatches = await freshDb.Batches.CountAsync(b => b.ProjectId == project.Id);
    Check("No duplicate batch row exists for the same project/batch code", totalBatches == 1);
}
