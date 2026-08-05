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
using OpenCvSharp;
using SkiaSharp;

var failures = 0;

Console.WriteLine("=== MicroCapture regression smoke test ===");

// Run the "schema check runs once" test FIRST — it relies on being the very first
// CaptureQueueService constructed in this process to observe the true before/after.
TestSchemaCheckRunsOnce();
await TestSupersedeRaceDoesNotDuplicateExport();
await TestBatchResumeDoesNotDuplicateBatch();
TestDocumentBoundaryDetection();
TestGutterSplitDetection();
TestManualOverrideLegacyRectCrop();
TestManualOverrideQuadCrop();
TestConvexityClampRejectsSelfIntersection();
TestEdgePointDetection();
TestManualOverrideSplitCrop();
await TestSplitCropReviewSaveThenExport();

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

/// <summary>A sharp white rectangle on a dark background — analogous to a photographed page
/// against a dark mat, for exercising ImageProcessor's contour-based boundary detection.</summary>
string WriteBoundaryTestImage(string path, int imageWidth, int imageHeight, SKRectI rect)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(20, 20, 20));
    using var paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), paint);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>A light two-page spread with a distinct darker vertical band standing in for a
/// book's spine shadow, for exercising ImageProcessor's gutter detection.</summary>
string WriteGutterTestImage(string path, int imageWidth, int imageHeight, int gutterCenterX, int gutterBandWidth)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(200, 200, 200));
    using var paint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(gutterCenterX - gutterBandWidth / 2f, 0, gutterCenterX + gutterBandWidth / 2f, imageHeight), paint);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>A plain solid-color image with no features — used where the crop-shape math
/// itself is what's under test, not detection.</summary>
string WriteSolidImage(string path, int width, int height)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(width, height);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(128, 128, 128));
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
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

void TestDocumentBoundaryDetection()
{
    Console.WriteLine("\n-- Crop review boundary detection finds a known rectangle --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    var knownRect = new SKRectI(50, 50, 750, 550); // width=700, height=500 -> 73% of frame area

    var path = WriteBoundaryTestImage(Path.Combine(workDir, "boundary.png"), imageWidth, imageHeight, knownRect);
    var detected = new ImageProcessor().DetectDocumentBoundary(path);

    Check("A confident boundary is detected", detected.HasValue);
    if (detected is { } boundary)
    {
        // ImageProcessor pads the detected contour by CropPadding (10px, default) before
        // clamping to the image bounds — allow generous slack for Canny/dilate edge slop
        // on top of that on what is otherwise a perfectly sharp synthetic rectangle.
        Check("Detected X is close to the known rectangle", Math.Abs(boundary.X - (knownRect.Left - 10)) <= 20);
        Check("Detected Y is close to the known rectangle", Math.Abs(boundary.Y - (knownRect.Top - 10)) <= 20);
        Check("Detected width is close to the known rectangle", Math.Abs(boundary.Width - (knownRect.Width + 20)) <= 30);
        Check("Detected height is close to the known rectangle", Math.Abs(boundary.Height - (knownRect.Height + 20)) <= 30);
    }
}

void TestGutterSplitDetection()
{
    Console.WriteLine("\n-- Book-split gutter detection finds a known spine position --");
    var workDir = TempWorkDir();
    const int imageWidth = 1000, imageHeight = 400;
    const int gutterCenterX = 430; // 43% of width — intentionally off-center from 50/50

    var path = WriteGutterTestImage(Path.Combine(workDir, "gutter.png"), imageWidth, imageHeight, gutterCenterX, gutterBandWidth: 30);
    var splitPercent = new ImageProcessor().DetectGutterSplitPercent(path);

    Check("Detected split lands near the known gutter position (not a lazy 50/50)",
        Math.Abs(splitPercent - 43.0) <= 3.0);
}

void TestManualOverrideLegacyRectCrop()
{
    Console.WriteLine("\n-- Manual override with a legacy \"x,y,w,h\" rect string crops to the expected size --");
    var workDir = TempWorkDir();
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "source_rect.png"), 400, 300);
    var outDir = Path.Combine(workDir, "Processed");

    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: true, leftCrop: "50,50,200,150");

    Check("Processing succeeds", result.Success);
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Legacy rect crop produces the expected output size (200x150)", output.Width == 200 && output.Height == 150);
    }
}

void TestManualOverrideQuadCrop()
{
    Console.WriteLine("\n-- Manual override with a new 8-number quad string perspective-warps to the expected size --");
    var workDir = TempWorkDir();
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "source_quad.png"), 400, 300);
    var outDir = Path.Combine(workDir, "Processed");

    // A deliberately skewed (non-axis-aligned) quad, like a keystoned photo:
    // TL=(40,40) TR=(340,60) BR=(360,260) BL=(20,240). Expected output size is the quad's
    // own longest top/bottom edge (~341) and longest left/right edge (~201) — see WarpQuad.
    const string quad = "40,40,340,60,360,260,20,240";
    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: true, leftCrop: quad);

    Check("Processing succeeds", result.Success);
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Quad crop width matches the quad's own geometry (~341px)", Math.Abs(output.Width - 341) <= 2);
        Check("Quad crop height matches the quad's own geometry (~201px)", Math.Abs(output.Height - 201) <= 2);
    }
}

void TestConvexityClampRejectsSelfIntersection()
{
    Console.WriteLine("\n-- Convexity clamp prevents a corner drag from creating self-intersection --");
    var square = new[] { new CropPoint(0, 0), new CropPoint(100, 0), new CropPoint(100, 100), new CropPoint(0, 100) };
    Check("Starting square is convex", CropGeometry.IsConvex(square));

    // Dragging the top-left corner far past the opposite (bottom-right) corner would make
    // the quad self-intersect into a "bowtie" — the clamp must stop it well short of that.
    var wildDrag = new CropPoint(500, 500);
    var clamped = CropGeometry.ClampCornerToConvex(square, 0, wildDrag);
    var trial = new[] { clamped, square[1], square[2], square[3] };
    Check("Clamped result keeps the quad convex", CropGeometry.IsConvex(trial));
    Check("Clamped result actually moved (the drag wasn't just rejected outright)",
        clamped.X != square[0].X || clamped.Y != square[0].Y);

    // A small, reasonable drag that stays convex should pass through completely unchanged.
    var reasonableDrag = new CropPoint(10, 10);
    var unclamped = CropGeometry.ClampCornerToConvex(square, 0, reasonableDrag);
    Check("A reasonable drag that stays convex is returned unchanged",
        unclamped.X == reasonableDrag.X && unclamped.Y == reasonableDrag.Y);
}

void TestEdgePointDetection()
{
    Console.WriteLine("\n-- Edge-point detection finds points near a known rectangle edge --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    var knownRect = new SKRectI(50, 50, 750, 550);
    var path = WriteBoundaryTestImage(Path.Combine(workDir, "edges.png"), imageWidth, imageHeight, knownRect);

    var points = ImageProcessor.DetectEdgePoints(path);
    Check("Edge points were found", points.Count > 0);

    var nearTopEdge = points.Count(p => Math.Abs(p.Y - knownRect.Top) <= 5 && p.X > knownRect.Left && p.X < knownRect.Right);
    Check("Edge points cluster near the known top edge", nearTopEdge > 10);
}

void TestManualOverrideSplitCrop()
{
    Console.WriteLine("\n-- Manual override split (both leftCrop and rightCrop) produces two distinct halves --");
    var workDir = TempWorkDir();
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "source_split.png"), 1000, 400);
    var outDir = Path.Combine(workDir, "Processed");

    // Mirrors exactly what CropReviewViewModel.Save() writes for a 46% split.
    const int imageWidth = 1000, imageHeight = 400;
    var leftWidth = (int)(imageWidth * 0.46);
    var leftCrop = $"0,0,{leftWidth},{imageHeight}";
    var rightCrop = $"{leftWidth},0,{imageWidth - leftWidth},{imageHeight}";

    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: true, manualOverride: true, leftCrop: leftCrop, rightCrop: rightCrop);

    Check("Processing succeeds", result.Success);
    Check("Exactly two output files are produced", result.OutputFilePaths.Count == 2);
    if (result.OutputFilePaths.Count == 2)
    {
        Check("First output is the left half", result.OutputFilePaths[0].Contains("_1_left"));
        Check("Second output is the right half", result.OutputFilePaths[1].Contains("_2_right"));
        using var left = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        using var right = Cv2.ImRead(result.OutputFilePaths[1], ImreadModes.Unchanged);
        Check("Left half width matches the requested split (~460px)", Math.Abs(left.Width - leftWidth) <= 2);
        Check("Right half width matches the requested split (~540px)", Math.Abs(right.Width - (imageWidth - leftWidth)) <= 2);
    }
}

async Task TestSplitCropReviewSaveThenExport()
{
    Console.WriteLine("\n-- Full flow: split Crop Review save -> reprocess -> export produces two distinct cropped pages --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    var queue = new CaptureQueueService(db);

    var project = new Project { Name = "SMOKE-SPLIT", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    var batch = new Batch { ProjectId = project.Id, Name = "SPLIT", BatchCode = "SPLIT", SplitBookPages = true };
    db.Batches.Add(batch);
    await db.SaveChangesAsync();

    const int imageWidth = 1000, imageHeight = 400;
    var originalPath = WriteSolidImage(Path.Combine(workDir, "spread.png"), imageWidth, imageHeight);
    var job = await queue.EnqueueCaptureAsync(batch.Id, originalPath, 1);

    // Mirrors CropReviewViewModel.Save() for a 46% split.
    var leftWidth = (int)(imageWidth * 0.46);
    job.LeftCropBox = $"0,0,{leftWidth},{imageHeight}";
    job.RightCropBox = $"{leftWidth},0,{imageWidth - leftWidth},{imageHeight}";
    job.ManualOverrideApplied = true;
    job.ProcessingStatus = "Pending";
    await db.SaveChangesAsync();

    // Mirrors what BackgroundProcessingWorker does for a Pending job whose batch has SplitBookPages set.
    var outputDir = Path.Combine(Path.GetDirectoryName(job.OriginalFilePath) ?? ".", "Processed");
    var processResult = new ImageProcessor().Process(job.OriginalFilePath, outputDir, splitPages: batch.SplitBookPages, manualOverride: job.ManualOverrideApplied, leftCrop: job.LeftCropBox, rightCrop: job.RightCropBox);
    Check("Worker-equivalent reprocessing succeeds", processResult.Success);
    Check("Worker-equivalent reprocessing writes two files", processResult.OutputFilePaths.Count == 2);
    await queue.UpdateJobStatusAsync(job.Id, "processing", processResult.Success ? "Completed" : "Failed");

    using var exportDb = new AppDbContext(dbPath);
    var exporter = new BatchExportService(exportDb);
    var exportDir = Path.Combine(workDir, "Export");
    var resultDir = await exporter.ExportBatchAsync(batch.Id, exportDir, "PNG");
    var exportedFiles = Directory.GetFiles(resultDir, "*.png").OrderBy(f => f).ToArray();

    Check("Export produces exactly two pages for the one split capture", exportedFiles.Length == 2);
    if (exportedFiles.Length == 2)
    {
        using var exportedLeft = Cv2.ImRead(exportedFiles[0], ImreadModes.Unchanged);
        using var exportedRight = Cv2.ImRead(exportedFiles[1], ImreadModes.Unchanged);
        Check("Exported left page is narrower than the full original (actually cropped, not the raw spread)",
            exportedLeft.Width < imageWidth);
        Check("Exported right page is narrower than the full original (actually cropped, not the raw spread)",
            exportedRight.Width < imageWidth);
    }
}
