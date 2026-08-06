// Headless regression checks for the durable capture queue / export pipeline.
// No UI, no camera — exercises CaptureQueueService, AppDbContext, and BatchExportService
// directly against disposable temp SQLite databases so it's safe to run repeatedly and
// won't touch the operator's real MicroCapture.db.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.Processing;
using OpenCvSharp;
using SkiaSharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using MicroCapture.UI.ViewModels;
using MicroCapture.UI.Views;

var failures = 0;

Console.WriteLine("=== MicroCapture regression smoke test ===");

// Headless Avalonia platform (no real display) — gives a working Dispatcher/window
// implementation so the tests below can drive the actual MainWindowViewModel/
// CropReviewViewModel/Window classes exactly as the real app does, not a hand-rolled
// stand-in. SetupWithoutStarting() initializes platform services only — it does not run
// App.OnFrameworkInitializationCompleted, so none of App's real camera-selection/MainWindow
// creation logic fires here.
AppBuilder.Configure<MicroCapture.UI.App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
    .SetupWithoutStarting();

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
TestTwoPageBoundaryDetection();
TestIndependentSkewedQuadsPerPage();
await TestTwoQuadCropReviewSaveReloadReSaveThenExport();
TestLowContrastDetection();
TestShadowedPageDetection();
TestBorderTouchingPageIsNotOverPadded();
TestMediumConfidenceCropIsStillApplied();
TestLowConfidenceCropIsSkippedAndFlagged();
TestFixedFramesRoundTrip();
TestProcessFixedFramesProducesNOutputs();
TestFixedFramesBypassesConfidenceGating();
await TestBackgroundWorkerBranchesToFixedFramesWhenBatchFlagSet();
TestTiffDisplayDecodeRoundTrip();
await TestDeleteCaptureExcludesFromExport();
TestMockCameraStyleFrameAutoCrops();
await TestManualCropReviewFlowOnMockCameraStyleFrame();
await TestRealUiFlowCaptureCropSaveThumbnailAndExport();

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;

// ---------- helpers ----------

void Check(string name, bool condition)
{
    if (condition) Console.WriteLine($"[PASS] {name}");
    else { Console.WriteLine($"[FAIL] {name}"); failures++; }
}

/// <summary>Blocks the calling thread until <paramref name="condition"/> is true, actively
/// pumping the headless dispatcher's job queue on every iteration. Plain `await` cannot be
/// used to wait on anything here: once <c>AppBuilder...SetupWithoutStarting()</c> installs a
/// dispatcher-bound SynchronizationContext on this thread, any `await` whose continuation
/// needs that same context (which includes ordinary `Task.Delay`/EF Core calls made from
/// deep inside application code, not just explicit Dispatcher.Post) can only resume once
/// something calls Dispatcher.UIThread.RunJobs() — but this thread is the only thing that
/// ever would, and it's the one sitting suspended at the `await`. Polling with a real,
/// non-async Thread.Sleep sidesteps that entirely: nothing here is ever suspended waiting on
/// the dispatcher, so there is no cycle to deadlock on.</summary>
void PumpUntil(Func<bool> condition, int timeoutMs = 15000, int pollMs = 20)
{
    var start = Environment.TickCount64;
    while (!condition())
    {
        Dispatcher.UIThread.RunJobs();
        if (condition()) return;
        if (Environment.TickCount64 - start > timeoutMs)
            throw new TimeoutException($"PumpUntil timed out after {timeoutMs}ms.");
        Thread.Sleep(pollMs);
    }
}

/// <summary>Starts an async application call (a RelayCommand, an EF Core save, anything) and
/// pumps the dispatcher until it finishes, instead of `await`-blocking this thread on it
/// directly — see <see cref="PumpUntil"/> for why a direct `await` here would deadlock.
/// Awaiting the already-completed `task` at the end is safe: the compiler-generated state
/// machine takes a synchronous fast path for an already-completed task and never actually
/// suspends, so no context capture happens at that point.</summary>
async Task RunPumped(Func<Task> start, int timeoutMs = 15000)
{
    var task = start();
    PumpUntil(() => task.IsCompleted, timeoutMs);
    await task;
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

/// <summary>Two separate sharp white rectangles on a dark background, side by side with a
/// gap between them — analogous to an open book photographed with a visible gutter shadow,
/// for exercising ImageProcessor's two-page split detection.</summary>
string WriteTwoPageTestImage(string path, int imageWidth, int imageHeight, SKRectI leftRect, SKRectI rightRect)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(20, 20, 20));
    using var paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(leftRect.Left, leftRect.Top, leftRect.Right, leftRect.Bottom), paint);
    canvas.DrawRect(new SKRect(rightRect.Left, rightRect.Top, rightRect.Right, rightRect.Bottom), paint);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>A subtle-contrast rectangle on a similarly-toned background — analogous to a
/// washed-out or low-contrast photographed page, for exercising adaptive Canny thresholding.</summary>
string WriteLowContrastTestImage(string path, int imageWidth, int imageHeight, SKRectI rect, byte backgroundGray, byte pageGray)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(backgroundGray, backgroundGray, backgroundGray));
    using var paint = new SKPaint { Color = new SKColor(pageGray, pageGray, pageGray), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), paint);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>A bright page on a dark background with a semi-transparent dark band overlapping
/// one edge of the page — analogous to a cast shadow falling across part of a photographed
/// page — for exercising illumination normalization / shadow suppression.</summary>
string WriteShadowedTestImage(string path, int imageWidth, int imageHeight, SKRectI rect, int shadowBandWidth)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(20, 20, 20));
    using var pagePaint = new SKPaint { Color = new SKColor(230, 230, 230), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), pagePaint);
    // A cast shadow band inside the left edge of the page — a gradual darkening, not a hard
    // second edge, so a naive detector might mistake its inner boundary for the page edge.
    using var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 110), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Left + shadowBandWidth, rect.Bottom), shadowPaint);
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

void TestLowContrastDetection()
{
    Console.WriteLine("\n-- Adaptive thresholding still detects a subtle-contrast page --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    var knownRect = new SKRectI(50, 50, 750, 550);
    // Only a 30-level gray delta between page and background — a fixed Canny(50,200)
    // threshold pair can miss an edge this weak; median-based adaptive thresholds scale to
    // the image's own contrast instead.
    var path = WriteLowContrastTestImage(Path.Combine(workDir, "low_contrast.png"), imageWidth, imageHeight, knownRect, backgroundGray: 150, pageGray: 180);

    var detected = new ImageProcessor().DetectDocumentBoundary(path);
    Check("A low-contrast page boundary is still detected", detected.HasValue);
    if (detected is { } boundary)
        Check("Low-contrast detection is reasonably close to the known rectangle", Math.Abs(boundary.Width - knownRect.Width) <= 60);
}

void TestShadowedPageDetection()
{
    Console.WriteLine("\n-- Illumination normalization keeps a shadowed page from fragmenting detection --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    var knownRect = new SKRectI(50, 50, 750, 550);
    var path = WriteShadowedTestImage(Path.Combine(workDir, "shadowed.png"), imageWidth, imageHeight, knownRect, shadowBandWidth: 100);

    var detected = new ImageProcessor().DetectDocumentBoundary(path);
    Check("A page with a partial cast shadow is still detected", detected.HasValue);
    if (detected is { } boundary)
    {
        // The key assertion: detection should still find the page's TRUE full extent
        // (including the shadowed portion), not shrink to exclude it because the shadow's
        // inner edge got mistaken for the page boundary.
        Check("Detected width still covers the full page, not just the unshadowed portion",
            boundary.Width >= knownRect.Width - 60);
    }
}

void TestBorderTouchingPageIsNotOverPadded()
{
    Console.WriteLine("\n-- A page touching the frame edge doesn't get phantom extra padding on the far side --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    // A 2px margin, not a pixel-exact 0 — OpenCV's Sobel/Canny use border-reflection by
    // default, so content starting at literal pixel 0 can have no detectable gradient there
    // at all (the "reflected" neighbor looks identical to the real one). A couple of pixels
    // of true margin gives Sobel genuine contrast to work with while dilation still pushes
    // the detected rect out to the frame edge — which is the realistic case anyway; a
    // document framed with truly zero margin at the sensor's exact boundary pixel is rare.
    var knownRect = new SKRectI(2, 50, 300, 550);
    var path = WriteBoundaryTestImage(Path.Combine(workDir, "border_touch.png"), imageWidth, imageHeight, knownRect);

    // A larger CropPadding widens the gap between "correct" (~330) and the old bug's
    // behavior (~360, since the left side's unusable padding used to leak into the width)
    // well beyond normal Canny/dilate detection noise.
    var processor = new ImageProcessor { CropPadding = 30 };
    var detected = processor.DetectDocumentBoundary(path);
    Check("Border-touching page is detected", detected.HasValue);
    if (detected is { } boundary)
    {
        var rightEdge = boundary.X + boundary.Width;
        Check("Detected left edge stays at the frame border (no room to pad)", boundary.X <= 5);
        Check("Right edge reflects only its own padding, not the left side's unused padding too",
            Math.Abs(rightEdge - (knownRect.Right + 30)) <= 20);
    }
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

void TestTwoPageBoundaryDetection()
{
    Console.WriteLine("\n-- Two-page split detection finds two separate known rectangles --");
    var workDir = TempWorkDir();
    const int imageWidth = 1000, imageHeight = 600;
    var leftRect = new SKRectI(50, 50, 470, 550);   // width=420, height=500 -> 35% of frame area
    var rightRect = new SKRectI(530, 50, 950, 550); // same size, clear gap between them

    var path = WriteTwoPageTestImage(Path.Combine(workDir, "two_page.png"), imageWidth, imageHeight, leftRect, rightRect);
    var detected = new ImageProcessor().DetectSplitPageBoundaries(path);

    Check("Two separate pages are detected", detected.HasValue);
    if (detected is { } pages)
    {
        Check("Detected left page is actually on the left", pages.Left.X < pages.Right.X);
        Check("Detected left page X is close to the known rectangle", Math.Abs(pages.Left.X - (leftRect.Left - 10)) <= 20);
        Check("Detected left page width is close to the known rectangle", Math.Abs(pages.Left.Width - (leftRect.Width + 20)) <= 30);
        Check("Detected right page X is close to the known rectangle", Math.Abs(pages.Right.X - (rightRect.Left - 10)) <= 20);
        Check("Detected right page width is close to the known rectangle", Math.Abs(pages.Right.Width - (rightRect.Width + 20)) <= 30);
    }

    // A single merged spread (no visible gap) must NOT be misreported as two confident pages —
    // it should fail gracefully so the caller falls back to the simple split-line flow.
    Console.WriteLine("-- Two-page detection declines a single merged spread rather than guessing --");
    var mergedPath = WriteBoundaryTestImage(Path.Combine(workDir, "merged_spread.png"), imageWidth, imageHeight, new SKRectI(50, 50, 950, 550));
    var mergedDetection = new ImageProcessor().DetectSplitPageBoundaries(mergedPath);
    Check("A single merged contour is not misreported as two pages", !mergedDetection.HasValue);
}

async Task TestTwoQuadCropReviewSaveReloadReSaveThenExport()
{
    Console.WriteLine("\n-- Two-quad split: save, reopen (restore), re-save, reprocess, export reflects the tight crop --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    var queue = new CaptureQueueService(db);

    var project = new Project { Name = "SMOKE-TWOQUAD", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    var batch = new Batch { ProjectId = project.Id, Name = "TWOQUAD", BatchCode = "TWOQUAD", SplitBookPages = true };
    db.Batches.Add(batch);
    await db.SaveChangesAsync();

    const int imageWidth = 1000, imageHeight = 500;
    var originalPath = WriteSolidImage(Path.Combine(workDir, "spread.png"), imageWidth, imageHeight);
    var job = await queue.EnqueueCaptureAsync(batch.Id, originalPath, 1);

    // Step 1: first Crop Review save — a tight independent quad on each side (mirrors
    // dragging the two-quad editor, as CropReviewViewModel.Save() formats them).
    var leftQuad = "20,30,460,10,470,470,10,490";
    var rightQuad = "540,15,980,25,975,480,545,460";
    job.LeftCropBox = leftQuad;
    job.RightCropBox = rightQuad;
    job.ManualOverrideApplied = true;
    job.ProcessingStatus = "Pending";
    await db.SaveChangesAsync();

    // Step 2: worker reprocesses it (mirrors BackgroundProcessingWorker exactly).
    var outputDir = Path.Combine(Path.GetDirectoryName(job.OriginalFilePath) ?? ".", "Processed");
    var firstResult = new ImageProcessor().Process(job.OriginalFilePath, outputDir, splitPages: batch.SplitBookPages, manualOverride: job.ManualOverrideApplied, leftCrop: job.LeftCropBox, rightCrop: job.RightCropBox);
    Check("First reprocess succeeds", firstResult.Success && firstResult.OutputFilePaths.Count == 2);
    await queue.UpdateJobStatusAsync(job.Id, "processing", "Completed");

    // Step 3: reopen Crop Review — mirrors CropReviewViewModel's restore logic exactly.
    using var reopenDb = new AppDbContext(dbPath);
    var reopenedJob = await reopenDb.CaptureJobs.FindAsync(job.Id);
    var hasSavedCrop = reopenedJob!.ManualOverrideApplied && !string.IsNullOrWhiteSpace(reopenedJob.LeftCropBox);
    var savedIsTwoQuad = hasSavedCrop && reopenedJob.LeftCropBox!.Split(',').Length == 8;
    var restoredLeft = ImageProcessor.ParseCropShape(reopenedJob.LeftCropBox!, imageWidth, imageHeight);
    var restoredRight = ImageProcessor.ParseCropShape(reopenedJob.RightCropBox!, imageWidth, imageHeight);
    Check("Reopening restores two-quad mode", savedIsTwoQuad);
    Check("Restored left quad matches what was saved", Math.Abs(restoredLeft[0].X - 20) < 0.5 && Math.Abs(restoredLeft[0].Y - 30) < 0.5);

    // Step 4: hit Save & Reprocess again with the restored (unedited) quads — exactly what
    // happens if an operator reopens review and re-saves without changing anything.
    reopenedJob.LeftCropBox = FormatCornersForTest(restoredLeft);
    reopenedJob.RightCropBox = FormatCornersForTest(restoredRight);
    reopenedJob.ProcessingStatus = "Pending";
    // Mirror Save()'s stale-derivative cleanup.
    var baseName = Path.GetFileNameWithoutExtension(reopenedJob.OriginalFilePath);
    foreach (var derivative in Directory.EnumerateFiles(outputDir, $"{baseName}*")) File.Delete(derivative);
    await reopenDb.SaveChangesAsync();

    // Step 5: worker reprocesses the re-save — using a fresh context/queue, exactly like
    // BackgroundProcessingWorker.ProcessLoop does on every real poll iteration (reusing the
    // original stale-tracked `queue` here would be a test-harness bug, not a real one: its
    // locally-tracked entity wouldn't see reopenDb's "Pending" write below the ORM layer).
    var secondResult = new ImageProcessor().Process(reopenedJob.OriginalFilePath, outputDir, splitPages: batch.SplitBookPages, manualOverride: reopenedJob.ManualOverrideApplied, leftCrop: reopenedJob.LeftCropBox, rightCrop: reopenedJob.RightCropBox);
    Check("Re-save reprocess succeeds", secondResult.Success && secondResult.OutputFilePaths.Count == 2);
    using (var workerDb = new AppDbContext(dbPath))
    {
        await new CaptureQueueService(workerDb).UpdateJobStatusAsync(job.Id, "processing", "Completed");
    }

    // Step 6: export and verify the exported pages are tightly cropped, not the raw spread.
    using var exportDb = new AppDbContext(dbPath);
    var exporter = new BatchExportService(exportDb);
    var exportDir = Path.Combine(workDir, "Export");
    var resultDir = await exporter.ExportBatchAsync(batch.Id, exportDir, "PNG");
    var exportedFiles = Directory.GetFiles(resultDir, "*.png").OrderBy(f => f).ToArray();

    Check("Export produces exactly two pages", exportedFiles.Length == 2);
    if (exportedFiles.Length == 2)
    {
        using var exportedLeft = Cv2.ImRead(exportedFiles[0], ImreadModes.Unchanged);
        using var exportedRight = Cv2.ImRead(exportedFiles[1], ImreadModes.Unchanged);
        // The full spread is 1000x500; a tight ~450x460 quad crop should be far smaller —
        // if this fails, the export is reflecting the raw/uncropped capture, not the saved crop.
        Check("Exported left page reflects the tight crop, not the full 1000-wide spread", exportedLeft.Width < 600);
        Check("Exported right page reflects the tight crop, not the full 1000-wide spread", exportedRight.Width < 600);
    }
}

string FormatCornersForTest(CropPoint[] corners) => string.Join(",", corners.SelectMany(c =>
    new[] { c.X.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), c.Y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) }));

void TestIndependentSkewedQuadsPerPage()
{
    Console.WriteLine("\n-- Split manual override with two independently skewed quads (not just strips) --");
    var workDir = TempWorkDir();
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "source_two_quads.png"), 1000, 500);
    var outDir = Path.Combine(workDir, "Processed");

    // Two independently skewed quads (as page-by-page corner editing would produce) —
    // deliberately different shapes on each side, unlike a shared straight split line.
    const string leftQuad = "20,30,460,10,470,470,10,490";     // slightly tilted left page
    const string rightQuad = "540,15,980,25,975,480,545,460"; // differently tilted right page

    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: true, manualOverride: true, leftCrop: leftQuad, rightCrop: rightQuad);

    Check("Processing succeeds", result.Success);
    Check("Exactly two output files are produced", result.OutputFilePaths.Count == 2);
    if (result.OutputFilePaths.Count == 2)
    {
        using var left = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        using var right = Cv2.ImRead(result.OutputFilePaths[1], ImreadModes.Unchanged);
        Check("Left and right outputs are genuinely different shapes (independent quads, not one shared line)",
            left.Width != right.Width || left.Height != right.Height);
        Check("Left output is a real, non-trivial image", left.Width > 100 && left.Height > 100);
        Check("Right output is a real, non-trivial image", right.Width > 100 && right.Height > 100);
    }
}

void TestMediumConfidenceCropIsStillApplied()
{
    Console.WriteLine("\n-- A medium-confidence detection (below CropConfidenceThreshold) is still auto-cropped, not silently skipped --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    var knownRect = new SKRectI(50, 50, 750, 550);
    var sourcePath = WriteBoundaryTestImage(Path.Combine(workDir, "medium_conf.png"), imageWidth, imageHeight, knownRect);
    var outDir = Path.Combine(workDir, "Processed");

    // Raising CropConfidenceThreshold above what this (otherwise clean) detection will ever
    // score simulates a real-world medium-confidence photo without needing to hand-craft an
    // exact numeric contour. MediumConfidenceThreshold is left at its default (0.3) — the
    // bar TryAutoCrop must now actually apply the crop, matching what Crop Review already
    // shows as its default suggestion. Before this fix, TryAutoCrop gated on
    // CropConfidenceThreshold directly, so this same setup would have kept the full,
    // uncropped frame — exactly the "cropped images aren't saved" bug reported from real
    // hardware.
    var processor = new ImageProcessor { CropConfidenceThreshold = 0.99 };
    var result = processor.Process(sourcePath, outDir, splitPages: false, manualOverride: false);

    Check("Processing succeeds", result.Success);
    Check("A medium-confidence detection is still cropped", result.WasCropped);
    Check("Medium-confidence auto-crop is flagged for review, not silently treated as fully trusted",
        result.QcVerdict == "WARNING");
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Output is genuinely smaller than the full source frame (a real crop happened)",
            output.Width < imageWidth && output.Height < imageHeight);
    }
}

void TestLowConfidenceCropIsSkippedAndFlagged()
{
    Console.WriteLine("\n-- A genuinely low-confidence detection keeps the full frame and is flagged --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    var knownRect = new SKRectI(50, 50, 750, 550);
    var sourcePath = WriteBoundaryTestImage(Path.Combine(workDir, "low_conf.png"), imageWidth, imageHeight, knownRect);
    var outDir = Path.Combine(workDir, "Processed");

    // Both thresholds pushed out of reach — nothing should qualify even as a medium-confidence
    // suggestion. The pipeline must still degrade safely: keep the full frame and flag it,
    // not crash or crop to something wrong.
    var processor = new ImageProcessor { CropConfidenceThreshold = 0.99, MediumConfidenceThreshold = 0.99 };
    var result = processor.Process(sourcePath, outDir, splitPages: false, manualOverride: false);

    Check("Processing succeeds", result.Success);
    Check("A low-confidence detection is not auto-cropped", !result.WasCropped);
    Check("Low-confidence pages are flagged for manual review, not marked clean", result.QcVerdict == "WARNING");
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Output keeps the full source frame when confidence is too low to trust",
            output.Width == imageWidth && output.Height == imageHeight);
    }
}

void TestFixedFramesRoundTrip()
{
    Console.WriteLine("\n-- Fixed-frame format/parse round-trips exactly --");
    var frames = new[]
    {
        new FixedFrameRect(10, 20, 100, 150),
        new FixedFrameRect(200, 50, 80, 60),
        new FixedFrameRect(5, 300, 400, 200)
    };
    var spec = ImageProcessor.FormatFixedFrames(frames);
    var parsed = ImageProcessor.ParseFixedFrames(spec);

    Check("Round-trip preserves frame count", parsed.Length == frames.Length);
    for (var i = 0; i < frames.Length && i < parsed.Length; i++)
    {
        Check($"Frame {i} round-trips exactly", Math.Abs(parsed[i].X - frames[i].X) < 0.01 && Math.Abs(parsed[i].Y - frames[i].Y) < 0.01 &&
            Math.Abs(parsed[i].Width - frames[i].Width) < 0.01 && Math.Abs(parsed[i].Height - frames[i].Height) < 0.01);
    }
}

void TestProcessFixedFramesProducesNOutputs()
{
    Console.WriteLine("\n-- ProcessFixedFrames crops N independent, correctly-sized outputs, no whole-frame extra --");
    var workDir = TempWorkDir();
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "source_fixed.png"), 400, 300);
    var outDir = Path.Combine(workDir, "Processed");

    var frames = new[]
    {
        new FixedFrameRect(10, 10, 150, 100),
        new FixedFrameRect(200, 150, 120, 80),
        new FixedFrameRect(50, 200, 90, 70)
    };
    var spec = ImageProcessor.FormatFixedFrames(frames);
    var result = new ImageProcessor().ProcessFixedFrames(sourcePath, outDir, spec);

    Check("Processing succeeds", result.Success);
    Check("Produces exactly N output files (one per frame, no whole-frame extra)", result.OutputFilePaths.Count == frames.Length);
    if (result.OutputFilePaths.Count == frames.Length)
    {
        var sorted = result.OutputFilePaths.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Check("Output filenames already sort in frame order (zero-padded _frameNN)", sorted.SequenceEqual(result.OutputFilePaths));
        for (var i = 0; i < frames.Length; i++)
        {
            using var output = Cv2.ImRead(sorted[i], ImreadModes.Unchanged);
            Check($"Frame {i + 1} output size matches its own calibrated rectangle",
                Math.Abs(output.Width - (int)frames[i].Width) <= 1 && Math.Abs(output.Height - (int)frames[i].Height) <= 1);
        }
    }
}

void TestFixedFramesBypassesConfidenceGating()
{
    Console.WriteLine("\n-- Fixed frames crop unconditionally even where auto-crop's confidence gate keeps the full frame --");
    var workDir = TempWorkDir();
    // A flat, featureless image: no contour for auto-crop to ever find — exactly the real-world
    // shape of the bug this feature exists to route around.
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "flat.png"), 500, 400);

    var autoCropResult = new ImageProcessor().Process(sourcePath, Path.Combine(workDir, "Processed_auto"), splitPages: false, manualOverride: false);
    Check("Auto-crop on a featureless image keeps the full frame uncropped", !autoCropResult.WasCropped);
    if (autoCropResult.Success && autoCropResult.OutputFilePaths.Count > 0)
    {
        using var autoOutput = Cv2.ImRead(autoCropResult.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Auto-crop output is the full, uncropped source size", autoOutput.Width == 500 && autoOutput.Height == 400);
    }

    var frameSpec = ImageProcessor.FormatFixedFrames(new[] { new FixedFrameRect(50, 50, 200, 150) });
    var fixedResult = new ImageProcessor().ProcessFixedFrames(sourcePath, Path.Combine(workDir, "Processed_fixed"), frameSpec);
    Check("Fixed-frame processing succeeds on the very same featureless image", fixedResult.Success);
    if (fixedResult.Success && fixedResult.OutputFilePaths.Count > 0)
    {
        using var fixedOutput = Cv2.ImRead(fixedResult.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Fixed-frame output is cropped to exactly the calibrated rectangle regardless of confidence",
            Math.Abs(fixedOutput.Width - 200) <= 1 && Math.Abs(fixedOutput.Height - 150) <= 1);
    }
}

async Task TestBackgroundWorkerBranchesToFixedFramesWhenBatchFlagSet()
{
    Console.WriteLine("\n-- Full flow: fixed-frame batch -> worker branch -> export produces one page per frame --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    var queue = new CaptureQueueService(db);

    var project = new Project { Name = "SMOKE-FIXEDFRAMES", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();

    const int calibW = 800, calibH = 600;
    var frames = new[]
    {
        new FixedFrameRect(20, 20, 300, 250),
        new FixedFrameRect(400, 20, 300, 250)
    };
    var batch = new Batch
    {
        ProjectId = project.Id,
        Name = "FIXEDFRAMES",
        BatchCode = "FIXEDFRAMES",
        UseFixedFrames = true,
        FixedFrames = ImageProcessor.FormatFixedFrames(frames),
        FixedFrameImageWidth = calibW,
        FixedFrameImageHeight = calibH
    };
    db.Batches.Add(batch);
    await db.SaveChangesAsync();

    var originalPath = WriteSolidImage(Path.Combine(workDir, "capture.png"), calibW, calibH);
    var job = await queue.EnqueueCaptureAsync(batch.Id, originalPath, 1);

    // Mirrors BackgroundProcessingWorker.ProcessLoop's branch for a batch with UseFixedFrames set.
    var outputDir = Path.Combine(Path.GetDirectoryName(job.OriginalFilePath) ?? ".", "Processed");
    var processResult = new ImageProcessor().ProcessFixedFrames(job.OriginalFilePath, outputDir, batch.FixedFrames!);
    Check("Worker-equivalent fixed-frame processing succeeds", processResult.Success);
    Check("Worker-equivalent processing writes exactly one file per frame", processResult.OutputFilePaths.Count == frames.Length);
    await queue.UpdateJobStatusAsync(job.Id, "processing", processResult.Success ? "Completed" : "Failed");

    using var exportDb = new AppDbContext(dbPath);
    var exporter = new BatchExportService(exportDb);
    var exportDir = Path.Combine(workDir, "Export");
    var resultDir = await exporter.ExportBatchAsync(batch.Id, exportDir, "PNG");
    var exportedFiles = Directory.GetFiles(resultDir, "*.png").OrderBy(f => f).ToArray();

    Check("Export produces one page per calibrated frame for the one capture — no BatchExportService changes needed",
        exportedFiles.Length == frames.Length);
}

void TestTiffDisplayDecodeRoundTrip()
{
    Console.WriteLine("\n-- ImageDecodeHelper bridges OpenCV-written TIFFs for on-screen display --");
    var workDir = TempWorkDir();

    // A plain OpenCV-written TIFF — exactly what ImageProcessor writes for every processed
    // derivative, and exactly what SkiaSharp/Avalonia's Skia-backed decoder cannot read
    // directly (confirmed earlier this session). This is the same file type the thumbnail
    // strip failed to refresh after Save & Reprocess.
    var tifPath = Path.Combine(workDir, "sample.tif");
    using (var mat = new Mat(30, 40, MatType.CV_8UC3, new Scalar(60, 120, 200)))
        Cv2.ImWrite(tifPath, mat);

    var tifBytes = ImageDecodeHelper.GetDisplayBytes(tifPath);
    Check("TIFF produces non-null display bytes", tifBytes != null);
    if (tifBytes != null)
    {
        using var decoded = SKBitmap.Decode(tifBytes);
        Check("Bridged TIFF bytes decode successfully via SkiaSharp", decoded != null);
        if (decoded != null)
        {
            Check("Decoded width matches the source TIFF", decoded.Width == 40);
            Check("Decoded height matches the source TIFF", decoded.Height == 30);
        }
    }

    // A plain PNG/JPG derivative (the emergency fallback path's output) should pass through
    // unchanged, decoding directly with no OpenCV round-trip needed.
    var pngPath = WriteSolidImage(Path.Combine(workDir, "sample.png"), 25, 15);
    var pngBytes = ImageDecodeHelper.GetDisplayBytes(pngPath);
    Check("Non-TIFF files pass through unchanged", pngBytes != null && pngBytes.SequenceEqual(File.ReadAllBytes(pngPath)));

    Check("A missing file returns null instead of throwing", ImageDecodeHelper.GetDisplayBytes(Path.Combine(workDir, "missing.tif")) == null);
}

async Task TestDeleteCaptureExcludesFromExport()
{
    Console.WriteLine("\n-- Deleting a mis-captured page excludes it from the pending queue and from export --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    var queue = new CaptureQueueService(db);

    var project = new Project { Name = "SMOKE-DELETE", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    var batch = new Batch { ProjectId = project.Id, Name = "DEL", BatchCode = "DEL" };
    db.Batches.Add(batch);
    await db.SaveChangesAsync();

    var keptPath = WriteDummyImage(Path.Combine(workDir, "page1_keep.png"));
    var keptJob = await queue.EnqueueCaptureAsync(batch.Id, keptPath, 1);
    await queue.UpdateJobStatusAsync(keptJob.Id, "processing", "Completed");
    await queue.UpdateJobStatusAsync(keptJob.Id, "qc", "PASS");

    var mistakePath = WriteDummyImage(Path.Combine(workDir, "page2_mistake.png"));
    var mistakeJob = await queue.EnqueueCaptureAsync(batch.Id, mistakePath, 2);
    await queue.UpdateJobStatusAsync(mistakeJob.Id, "processing", "Completed");
    await queue.UpdateJobStatusAsync(mistakeJob.Id, "qc", "PASS");

    // A third page captured by mistake but never even processed yet — the common real case
    // ("wrong page, delete it immediately").
    var stillPendingPath = WriteDummyImage(Path.Combine(workDir, "page3_pending_mistake.png"));
    var pendingMistakeJob = await queue.EnqueueCaptureAsync(batch.Id, stillPendingPath, 3);

    await queue.DeleteCaptureAsync(mistakeJob.Id);
    await queue.DeleteCaptureAsync(pendingMistakeJob.Id);

    using var verifyDb = new AppDbContext(dbPath);
    var reloadedMistake = await verifyDb.CaptureJobs.FindAsync(mistakeJob.Id);
    Check("Deleted completed job is marked Superseded, not removed from the audit trail",
        reloadedMistake!.ProcessingStatus == "Superseded" && reloadedMistake.ExportStatus == "Superseded");

    var pendingJobs = await queue.GetPendingJobsAsync();
    Check("Deleted pending job no longer appears in the processing queue",
        pendingJobs.All(j => j.Id != pendingMistakeJob.Id));

    using var exportDb = new AppDbContext(dbPath);
    var exporter = new BatchExportService(exportDb);
    var exportDir = Path.Combine(workDir, "Export");
    var resultDir = await exporter.ExportBatchAsync(batch.Id, exportDir, "PNG");
    var exportedFiles = Directory.GetFiles(resultDir, "*.png");
    Check("Export includes only the kept page, not the deleted mistake", exportedFiles.Length == 1);
}

/// <summary>Draws exactly what MicroCapture.Camera.MockCameraService.GenerateMockFrame draws
/// (text over a stroked, not filled, rectangle) — the real dev-time stand-in for a photographed
/// page, and meaningfully different from this file's other synthetic images (a filled block).
/// A real regression here would have caught the exact scenario reported from live testing.</summary>
string WriteMockCameraStyleImage(string path, int width, int height)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(width, height));
    var canvas = surface.Canvas;
    canvas.Clear(SkiaSharp.SKColors.DarkBlue);
    using var font = new SkiaSharp.SKFont { Size = width / 15f };
    using var textPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true };
    var lines = new[] { "MOCK CAPTURE", "h_b_000002_20260805_234246.jpg" };
    float y = height / 2f - (lines.Length * font.Size) / 2f;
    foreach (var line in lines)
    {
        canvas.DrawText(line, width / 2f, y, SkiaSharp.SKTextAlign.Center, font, textPaint);
        y += font.Size + 10;
    }
    using var rectPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 5, IsAntialias = true };
    var inset = width / 10f;
    canvas.DrawRect(inset, inset, width - inset * 2, height - inset * 2, rectPaint);
    using var image = surface.Snapshot();
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
    File.WriteAllBytes(path, data.ToArray());
    return path;
}

void TestMockCameraStyleFrameAutoCrops()
{
    Console.WriteLine("\n-- A mock-camera-style frame (text over a stroked rect, 3840x2160) auto-crops --");
    var workDir = TempWorkDir();
    var path = WriteMockCameraStyleImage(Path.Combine(workDir, "mock_realistic.jpg"), 3840, 2160);
    var outDir = Path.Combine(workDir, "Processed");

    var result = new ImageProcessor().Process(path, outDir, splitPages: false, manualOverride: false);

    Check("Processing succeeds", result.Success);
    Check("The realistic mock frame is auto-cropped, not passed through raw", result.WasCropped);
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Output is genuinely smaller than the 3840x2160 source",
            output.Width < 3840 && output.Height < 2160);
    }
}

async Task TestManualCropReviewFlowOnMockCameraStyleFrame()
{
    Console.WriteLine("\n-- Full DB flow on a mock-camera-style frame: capture, manual Crop Review save, worker reprocess, export --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    var queue = new CaptureQueueService(db);
    var project = new Project { Name = "DIAG", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();
    var batch = new Batch { ProjectId = project.Id, Name = "DIAG", BatchCode = "DIAG" };
    db.Batches.Add(batch);
    await db.SaveChangesAsync();

    var capturePath = WriteMockCameraStyleImage(Path.Combine(workDir, "capture_mock.jpg"), 3840, 2160);
    var job = await queue.EnqueueCaptureAsync(batch.Id, capturePath, 1);
    var outputDir = Path.Combine(workDir, "Processed");
    var processor = new ImageProcessor();

    // First pass — worker auto-processes exactly like BackgroundProcessingWorker does.
    var autoResult = processor.Process(job.OriginalFilePath, outputDir, splitPages: false, manualOverride: job.ManualOverrideApplied, leftCrop: job.LeftCropBox, rightCrop: job.RightCropBox);
    await queue.UpdateJobStatusAsync(job.Id, "processing", "Completed");
    await queue.UpdateJobStatusAsync(job.Id, "qc", autoResult.QcVerdict);

    // Simulate opening Crop Review and saving a much tighter manual crop than auto-crop found.
    using var reviewDb = new AppDbContext(dbPath);
    var reviewJob = await reviewDb.CaptureJobs.FindAsync(job.Id);
    reviewJob!.LeftCropBox = "600,500,2600,1100";
    reviewJob.RightCropBox = null;
    reviewJob.ManualOverrideApplied = true;
    reviewJob.ProcessingStatus = "Pending";
    reviewJob.QcStatus = "Pending";
    await reviewDb.SaveChangesAsync();

    // Worker's next pass — fresh DbContext, exactly like BackgroundProcessingWorker.
    using var workerDb2 = new AppDbContext(dbPath);
    var workerQueue2 = new CaptureQueueService(workerDb2);
    var pending = await workerQueue2.GetPendingJobsAsync();
    Check("Crop Review save re-queues the job for reprocessing", pending.Count == 1);
    foreach (var pendingJob in pending)
    {
        var reprocessResult = processor.Process(pendingJob.OriginalFilePath, outputDir, splitPages: false, manualOverride: pendingJob.ManualOverrideApplied, leftCrop: pendingJob.LeftCropBox, rightCrop: pendingJob.RightCropBox);
        Check("Reprocessing with the manual crop succeeds", reprocessResult.Success);
        if (reprocessResult.OutputFilePaths.Count > 0)
        {
            using var reOut = Cv2.ImRead(reprocessResult.OutputFilePaths[0], ImreadModes.Unchanged);
            Check("Reprocessed output matches the manually saved crop rect (2600x1100)",
                reOut.Width == 2600 && reOut.Height == 1100);
        }
        await workerQueue2.UpdateJobStatusAsync(pendingJob.Id, "processing", "Completed");
        await workerQueue2.UpdateJobStatusAsync(pendingJob.Id, "qc", reprocessResult.QcVerdict);
    }

    using var exportDb = new AppDbContext(dbPath);
    var exporter = new BatchExportService(exportDb);
    var exportDir = Path.Combine(workDir, "Export");
    var resultDir = await exporter.ExportBatchAsync(batch.Id, exportDir, "PNG");
    var exportedFiles = Directory.GetFiles(resultDir, "*.png");
    Check("Export produces exactly one file", exportedFiles.Length == 1);
    if (exportedFiles.Length == 1)
    {
        using var exported = Cv2.ImRead(exportedFiles[0], ImreadModes.Unchanged);
        Check("Exported file reflects the manual crop, not the raw 3840x2160 capture",
            exported.Width == 2600 && exported.Height == 1100);
    }
}

async Task TestRealUiFlowCaptureCropSaveThumbnailAndExport()
{
    Console.WriteLine("\n-- REAL UI FLOW: actual MainWindowViewModel + CropReviewViewModel + commands, isolated DB/dir --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    // Pre-seed the Project with our own temp OutputDirectory so StartBatchAsync finds an
    // existing project (matched by name) instead of creating one under the operator's real
    // ~/Pictures/MicroCapture folder — the one piece of MainWindowViewModel that still
    // hardcodes a real user-facing path.
    using (var seedDb = new AppDbContext(dbPath))
    {
        _ = new CaptureQueueService(seedDb); // triggers schema creation, same as every real AppDbContext use
        seedDb.Projects.Add(new Project { Name = "UITEST", OutputDirectory = workDir });
        await seedDb.SaveChangesAsync();
    }

    var camera = new MicroCapture.Camera.MockCameraService();
    var vm = new MainWindowViewModel(camera, dbPath);

    await RunPumped(() => vm.ConnectCommand.ExecuteAsync(null));
    Check("Real UI flow: camera connects", vm.IsConnected);

    vm.ProjectCode = "UITEST";
    vm.BatchCode = "UITEST";
    await RunPumped(() => vm.StartBatchCommand.ExecuteAsync(null));
    Check("Real UI flow: batch starts without touching the real Pictures folder", Directory.Exists(workDir));

    await RunPumped(() => vm.CaptureCommand.ExecuteAsync(null));
    PumpUntil(() => vm.RecentCaptures.Count == 1);
    Check("Real UI flow: capture adds exactly one thumbnail", vm.RecentCaptures.Count == 1);
    if (vm.RecentCaptures.Count == 0) return;

    var jobId = vm.RecentCaptures[0].JobId;

    // The real BackgroundProcessingWorker (owned by vm, polling once a second) should pick
    // this up on its own — wait for it instead of forcing a pass by hand.
    PumpUntil(() => vm.RecentCaptures[0].Status != "Processing", timeoutMs: 20000);
    Check("Real UI flow: worker auto-processes the capture", vm.RecentCaptures[0].Status.StartsWith("Processed"));
    Check("Real UI flow: thumbnail bitmap is populated after auto-processing", vm.RecentCaptures[0].Thumbnail != null);
    var thumbBeforeReprocess = vm.RecentCaptures[0].Thumbnail;

    // Open Crop Review exactly the way MainWindowViewModel.ReviewCrop does internally
    // (same jobId, a fresh context against the same file) — the command itself doesn't
    // expose the window/viewmodel it creates, so this constructs it the same way instead of
    // going through the command.
    using var reviewDb = new AppDbContext(dbPath);
    var reviewQueue = new CaptureQueueService(reviewDb);
    var cropVm = new CropReviewViewModel(jobId, reviewDb, reviewQueue);
    PumpUntil(() => cropVm.Image != null);
    Check("Real UI flow: Crop Review loads the captured image", cropVm.Image != null);

    // Drag the top-left handle inward by a large, deliberate amount — the same call the
    // View's OnPointerMoved makes (resolve through the ViewModel, then assign the property).
    var draggedTopLeft = new CropPoint(cropVm.TopLeft.X + 500, cropVm.TopLeft.Y + 400);
    cropVm.TopLeft = cropVm.ResolveCornerDrag(0, draggedTopLeft);
    Dispatcher.UIThread.RunJobs();

    // Mirrors the subscription MainWindowViewModel.ReviewCrop wires up in the real app.
    var sawImmediateReprocessingFeedback = false;
    cropVm.Saved += (_, _) =>
    {
        var thumbnail = vm.RecentCaptures.FirstOrDefault(t => t.JobId == jobId);
        if (thumbnail != null)
        {
            thumbnail.Status = "Reprocessing…";
            sawImmediateReprocessingFeedback = true;
        }
    };

    var cropReviewWindow = new CropReviewWindow { DataContext = cropVm };
    await RunPumped(() => cropVm.SaveCommand.ExecuteAsync(cropReviewWindow));
    Check("Real UI flow: thumbnail shows immediate feedback on save, not a stale-looking state",
        sawImmediateReprocessingFeedback && vm.RecentCaptures[0].Status == "Reprocessing…");

    // The real worker should pick the re-queued job back up on its own within ~1s.
    PumpUntil(() => vm.RecentCaptures[0].Thumbnail != thumbBeforeReprocess, timeoutMs: 20000);
    Check("Real UI flow: thumbnail actually refreshes after Save & Reprocess (this is the exact reported bug)",
        vm.RecentCaptures[0].Thumbnail != thumbBeforeReprocess);
    Check("Real UI flow: status reflects the reprocess completed", vm.RecentCaptures[0].Status.StartsWith("Processed"));

    await RunPumped(() => vm.ExportBatchCommand.ExecuteAsync(null));
    Check("Real UI flow: export completed and left output in the isolated temp directory, not ~/Pictures",
        Directory.GetFiles(workDir, "*.pdf", SearchOption.AllDirectories).Length > 0);

    var pdfFiles = Directory.GetFiles(workDir, "*.pdf", SearchOption.AllDirectories);
    if (pdfFiles.Length > 0)
    {
        var pdfInfo = new FileInfo(pdfFiles[0]);
        Check("Real UI flow: exported PDF is non-trivial in size", pdfInfo.Length > 1000);
    }

    await RunPumped(() => vm.ShutdownAsync());
}
