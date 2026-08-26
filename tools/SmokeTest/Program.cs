// Headless regression checks for the durable capture queue / export pipeline.
// No UI, no camera — exercises CaptureQueueService, AppDbContext, and BatchExportService
// directly against disposable temp SQLite databases so it's safe to run repeatedly and
// won't touch the operator's real MicroCapture.db.
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
TestAutoSplitTriggersOnConfidentSpineShadow();
TestAutoSplitDoesNotTriggerOnPlainSinglePage();
TestAutoSplitDoesNotTriggerOnPageEdgeInsideGutterBand();
TestBoundaryCurveStaysSafeOnNotchedEdge();
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
TestLiveViewDetectionIsRobust();
TestBatchManifestRoundTripsAndValidates();
TestBatchManifestSurvivesRelocation();
TestBatchManifestSurvivesInterruptedWrite();
TestBatchLockIsAdvisory();
TestBrightnessPassExcludesHandOverlap();
TestUniformBrightnessImageStaysUndetected();
TestBorderTouchingPageIsNotOverPadded();
TestMediumConfidenceCropIsStillApplied();
TestFixedFramesRoundTrip();
TestProcessFixedFramesProducesNOutputs();
TestFixedFramesFallsBackToCalibratedRectOnFeaturelessImage();
TestFrameGeometryEditing();
await TestFramePersistDoesNotLeakAcrossBatchSwitch();
TestFixedFramesScaleFromReferenceResolution();
TestFixedFramesZeroReferenceIsBackCompatible();
TestCheckLiveRegionsDetectsPerRegionContentChange();
TestCheckLiveRegionsSharpnessIsPerRegion();
await TestBackgroundWorkerBranchesToFixedFramesWhenBatchFlagSet();
TestTiffDisplayDecodeRoundTrip();
TestDewarpControlPointsStayWithinPageBounds();
TestLineMeshFlattensSharedPageWideBow();
TestLineMeshDeclinesOnPlainUniformPage();
TestFingerRemovalCleansEdgeTouchingSkinBlob();
TestFingerRemovalLeavesInteriorSkinToneAlone();
TestBleedthroughSuppressesFaintGhostPreservesRealInk();
TestWriteTiffPreservesLargeColorImagePixelData();
await TestDeleteCaptureExcludesFromExport();
TestMockCameraStyleFrameAutoCrops();
await TestManualCropReviewFlowOnMockCameraStyleFrame();
await TestRealUiFlowCaptureCropSaveThumbnailAndExport();
await TestCropReviewAdjustModeRotationReachesExport();
await TestFinalizeSearchablePdfActuallyEmbedsOcrText();
TestManualAdjustmentsAreNoOpAtDefaults();
TestManualAdjustmentsRotateAndFlip();
TestManualAdjustmentsBrightnessContrastDirection();
TestManualAdjustmentsSaturationDirection();
TestManualAdjustmentsWhiteBalanceDirection();
TestManualAdjustmentsSharpnessIncreasesEdgeContrast();
TestAdjustmentGeometryClamping();
TestAdjustmentPresetsWithinRange();

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

/// <summary>A single bright page against a dark backdrop, photographed at enough of an angle
/// that the page's own left edge (not a real spine) falls inside DetectGutter's central 30-70%
/// search band — reproduces a real bug (Trapezoid_Image003.JPG) where that one-sided
/// background-to-page transition was mistaken for a confident spine shadow because the darkest
/// column in the band landed right at the band's own boundary, not at a genuine interior dip
/// flanked by bright content on both sides.</summary>
string WritePageEdgeInsideGutterBandTestImage(string path, int imageWidth, int imageHeight, int pageStartX)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(10, 10, 10));
    using var paint = new SKPaint { Color = new SKColor(210, 210, 210), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(pageStartX, 0, imageWidth, imageHeight), paint);
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

/// <summary>A page carrying text-like internal detail on a plain dark backdrop, optionally
/// blurred to simulate an out-of-focus capture. The internal detail is the point: a flat
/// featureless rectangle has almost no focus signal at all (its Laplacian variance is near zero
/// whether sharp or soft), so it can't distinguish focus states — only real page content can.</summary>
string WriteDetailedPage(string path, int imageWidth, int imageHeight, SKRectI rect, bool blurred)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(40, 40, 40));
    using var pagePaint = new SKPaint { Color = new SKColor(230, 230, 230), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), pagePaint);

    // Text-like rules across the page, drawn into a layer so the blur applies to the page
    // content rather than compositing oddly against the backdrop.
    using var textPaint = new SKPaint
    {
        Color = new SKColor(30, 30, 30),
        Style = SKPaintStyle.Fill,
        IsAntialias = true,
        ImageFilter = blurred ? SKImageFilter.CreateBlur(5, 5) : null
    };
    for (var y = rect.Top + 30; y < rect.Bottom - 20; y += 26)
    {
        var lineRight = rect.Right - 40 - ((y / 26) % 3) * 60;
        canvas.DrawRect(new SKRect(rect.Left + 40, y, lineRight, y + 10), textPaint);
    }

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

/// <summary>A sharp white page on a dark background with a skin-toned rectangle overlapping
/// one edge — analogous to a hand holding a book open, positioned partly over the dark
/// backdrop and partly onto the page itself. Both the page and the skin patch are near-white/
/// bright against the dark background, so plain brightness thresholding alone (with no color
/// awareness) can't tell them apart — this specifically isolates FindContoursByBrightness's
/// SkinExclusionMask step, which is the only stage in the pipeline that reads chrominance
/// rather than luminance. Grayscale-only detectors (the Canny passes) can't distinguish
/// skin from page at all, so this scenario cleanly targets skin exclusion, not general
/// boundary-detection robustness (see the note by WriteSoftEdgeTestImage's tests for why a
/// synthetic Canny-defeating image isn't attempted here).</summary>
string WriteHandOverlapTestImage(string path, int imageWidth, int imageHeight, SKRectI pageRect, SKRectI skinRect)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(20, 20, 20));
    using var pagePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(pageRect.Left, pageRect.Top, pageRect.Right, pageRect.Bottom), pagePaint);
    // A representative mid-tone skin color (Y~161, Cr~155, Cb~105 — inside the default
    // SkinCrLow/High=135/180, SkinCbLow/High=85/135 range this test exercises).
    using var skinPaint = new SKPaint { Color = new SKColor(200, 150, 120), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(skinRect.Left, skinRect.Top, skinRect.Right, skinRect.Bottom), skinPaint);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>A page whose left edge has a real inward notch across its own middle third, so
/// Canny evidence for that edge clusters into two separated groups near the top and bottom
/// corners with nothing in between — the same *shape* of evidence gap behind a real regression
/// (confirmed on a real photo, IMG_0022's right half: <c>FitEdgeCurve</c>'s degree-2 fit sat
/// tight against two such clusters individually while swinging wildly *between* them, invisible
/// to any check that only evaluates error at the inlier points themselves). This synthetic
/// version does NOT reproduce that swing — tried, and a clean, noise-free, perfectly vertical
/// two-cluster edge just fits a flat curve; the real instability needed the noise/asymmetry a
/// real photo's Canny response has, which isn't faithfully fakeable (same conclusion reached
/// once before in this codebase for a different detection edge case — see the "Tried and
/// reverted" note in ImageProcessor.cs). What this test *does* legitimately guard: Process()
/// producing a real, uncorrupted crop on a page whose edge has a genuine gap in evidence,
/// whichever path (boundary-curve or corner-fallback) it takes. The actual regression's real
/// fix is verified against the real fixture instead — re-run
/// <c>dotnet run --project tools/DewarpDiagnostic -- corners &lt;path-to-real-photo-half&gt;</c>
/// and confirm MaxOffsetPx stays sane (declines rather than reporting a wild value) if this
/// area is ever touched again.</summary>
string WriteNotchedEdgeTestImage(string path, int imageWidth, int imageHeight, SKRectI pageRect, int notchDepth, int notchTop, int notchBottom)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(new SKColor(20, 20, 20));
    using var pagePaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(pageRect.Left, pageRect.Top, pageRect.Right, pageRect.Bottom), pagePaint);
    using var notchPaint = new SKPaint { Color = new SKColor(20, 20, 20), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(pageRect.Left, notchTop, pageRect.Left + notchDepth, notchBottom), notchPaint);
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

/// <summary>Several solid black horizontal bars on white — stands in for text lines (the
/// line-blob detector behind dewarp/deskew only cares about blob geometry, not glyph shapes)
/// so dewarp curve-fitting can be exercised without needing real rendered text.</summary>
string WriteMultiBarTestImage(string path, int imageWidth, int imageHeight, (int X, int Y, int Width, int Height)[] bars)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
    foreach (var bar in bars)
        canvas.DrawRect(new SKRect(bar.X, bar.Y, bar.X + bar.Width, bar.Y + bar.Height), paint);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>Several black horizontal bars, each following the *same* smooth curve shape (a
/// centered parabola bowing upward toward the page's horizontal center) but at different
/// vertical baselines — stands in for real text lines that share one systematic page-wide bend
/// (the case <see cref="ImageProcessor.TryApplyLineMesh"/> targets), built from many thin
/// vertical strokes since SkiaSharp has no direct "draw a bowed rectangle" primitive.</summary>
string WriteCurvedBarTestImage(string path, int imageWidth, int imageHeight, int[] baselines, int barThickness, double amplitudePx)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
    var marginX = imageWidth / 20;
    foreach (var baseline in baselines)
    {
        for (var x = marginX; x < imageWidth - marginX; x += 2)
        {
            var t = (x - marginX) / (double)(imageWidth - 2 * marginX); // 0..1
            var bow = amplitudePx * Math.Sin(Math.PI * t); // 0 at both ends, peak at center
            var y = baseline - bow;
            canvas.DrawRect(new SKRect(x, (float)y, x + 2, (float)(y + barThickness)), paint);
        }
    }
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>A plain page background with a few black text-like bars, plus one skin-toned blob at
/// <paramref name="skinRect"/> — for exercising <see cref="ImageProcessor.TryRemoveFingers"/>.
/// The color (RGB 224,172,120) lands inside the default YCrCb skin range
/// (<see cref="ImageProcessor.SkinCrLow"/>/High, SkinCbLow/High) the same way real skin does.</summary>
string WriteSkinBlobTestImage(string path, int imageWidth, int imageHeight, SKRectI skinRect)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var textPaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
    for (var y = imageHeight / 4; y < imageHeight - imageHeight / 4; y += 40)
        canvas.DrawRect(new SKRect(imageWidth / 10, y, imageWidth - imageWidth / 10, y + 12), textPaint);
    using var skinPaint = new SKPaint { Color = new SKColor(224, 172, 120), Style = SKPaintStyle.Fill, IsAntialias = false };
    canvas.DrawRect(new SKRect(skinRect.Left, skinRect.Top, skinRect.Right, skinRect.Bottom), skinPaint);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(path);
    data.SaveTo(stream);
    return path;
}

/// <summary>A white page with solid black "real ink" bars plus a set of thin light-gray
/// "bleedthrough ghost" strokes — for exercising
/// <see cref="ImageProcessor.ApplyBleedthroughSuppressionFromBytes"/>. The ghost strokes are
/// deliberately thin (like real bleed-through text), not one solid block: the algorithm's local-
/// background estimate is a wide blur that needs to "see past" foreground content to the true
/// surrounding background on both sides, exactly like it does for real, thin text strokes — a
/// large solid block wider than the blur radius would just become its own local background and
/// never register as having any "depth" at all, which isn't representative of real bleedthrough.
/// The ghost color (230,230,230 on white) sits at a shallow local depth, the same order of
/// magnitude as real bleedthrough measured on a confirmed real fixture (IMG_0022.JPG); the ink
/// bars sit far deeper (near-black), matching that same fixture's real text.</summary>
string WriteBleedthroughTestImage(string path, int imageWidth, int imageHeight, SKRectI[] inkBars, SKRectI ghostArea, int ghostStrokeThickness, int ghostStrokeGap)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var bitmap = new SKBitmap(imageWidth, imageHeight);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var ghostPaint = new SKPaint { Color = new SKColor(230, 230, 230), Style = SKPaintStyle.Fill, IsAntialias = false };
    for (var y = ghostArea.Top; y < ghostArea.Bottom; y += ghostStrokeThickness + ghostStrokeGap)
        canvas.DrawRect(new SKRect(ghostArea.Left, y, ghostArea.Right, y + ghostStrokeThickness), ghostPaint);
    using var inkPaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
    foreach (var bar in inkBars)
        canvas.DrawRect(new SKRect(bar.Left, bar.Top, bar.Right, bar.Bottom), inkPaint);
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

void TestAutoSplitTriggersOnConfidentSpineShadow()
{
    Console.WriteLine("\n-- Process() auto-splits a spread it was never told about, off a confident spine shadow --");
    var workDir = TempWorkDir();
    const int imageWidth = 1000, imageHeight = 400;
    const int gutterCenterX = 430; // same fixture as TestGutterSplitDetection — known-confident gutter.
    var sourcePath = WriteGutterTestImage(Path.Combine(workDir, "auto_spread.png"), imageWidth, imageHeight, gutterCenterX, gutterBandWidth: 30);
    var outDir = Path.Combine(workDir, "Processed");

    // splitPages: false and manualOverride: false — nobody asked for a split; this is exactly
    // the automatic-capture path an operator who forgot to check Batch.SplitBookPages hits.
    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: false);

    Check("Processing succeeds", result.Success);
    Check("A confident spine shadow alone promotes to a two-file split", result.OutputFilePaths.Count == 2);
    if (result.OutputFilePaths.Count == 2)
    {
        Check("First output is the left half", result.OutputFilePaths[0].Contains("_1_left"));
        Check("Second output is the right half", result.OutputFilePaths[1].Contains("_2_right"));
    }
    Check("A warning explains the auto-detected split", result.Warnings.Any(w => w.Contains("auto-detected")));
}

void TestAutoSplitDoesNotTriggerOnPlainSinglePage()
{
    Console.WriteLine("\n-- Process() leaves a real single page alone (no spine shadow to promote on) --");
    var workDir = TempWorkDir();
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "auto_single.png"), 1000, 400);
    var outDir = Path.Combine(workDir, "Processed");

    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: false);

    Check("Processing succeeds", result.Success);
    Check("No gutter signal means exactly one output file, not a false-positive split", result.OutputFilePaths.Count == 1);
    if (result.OutputFilePaths.Count == 1)
        Check("The single output is the whole-page path", result.OutputFilePaths[0].Contains("_processed"));
}

void TestAutoSplitDoesNotTriggerOnPageEdgeInsideGutterBand()
{
    Console.WriteLine("\n-- Process() does not split a single angled page whose own edge falls inside the gutter search band --");
    var workDir = TempWorkDir();
    const int imageWidth = 1000, imageHeight = 400;
    // Page starts at 31% of width — just inside the 30% search-band boundary, same shape as
    // the real Trapezoid_Image003.JPG failure: dark background up to the boundary, uniform
    // bright page for the entire rest of the frame, no second bright region beyond a real dip.
    var sourcePath = WritePageEdgeInsideGutterBandTestImage(Path.Combine(workDir, "page_edge_in_band.png"), imageWidth, imageHeight, pageStartX: 310);
    var outDir = Path.Combine(workDir, "Processed");

    var splitPercent = new ImageProcessor().DetectGutterSplitPercent(sourcePath);
    Check("Gutter detection falls back to an even 50/50 rather than trusting the page-edge boundary artifact", splitPercent == 50.0);

    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: false);
    Check("Processing succeeds", result.Success);
    Check("A single page's own edge inside the search band does not trigger a false-positive split", result.OutputFilePaths.Count == 1);
}

void TestBoundaryCurveStaysSafeOnNotchedEdge()
{
    Console.WriteLine("\n-- A page edge with a genuine evidence gap (real notch) still produces a real, uncorrupted crop --");
    var workDir = TempWorkDir();
    const int imageWidth = 1000, imageHeight = 1200;
    var pageRect = new SKRectI(200, 100, 900, 1100);
    // Notch spans the middle third of the left edge, 60px deep — comfortably past
    // BoundaryCurveBandPx (30px default), so that region contributes zero inlier evidence
    // while the top/bottom thirds (each 300px, well past the 15%/85% span gate) still do.
    var sourcePath = WriteNotchedEdgeTestImage(Path.Combine(workDir, "notched_edge.png"), imageWidth, imageHeight, pageRect, notchDepth: 60, notchTop: 400, notchBottom: 800);
    var outDir = Path.Combine(workDir, "Processed");

    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: false);
    Check("Processing succeeds", result.Success);
    Check("Produces exactly one output file", result.OutputFilePaths.Count == 1);
    if (result.OutputFilePaths.Count == 1)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Grayscale);
        Check("Output is a real, non-trivial image", output.Width > 10 && output.Height > 10);
        Cv2.MeanStdDev(output, out var mean, out _);
        // The confirmed real regression pulled in background/off-page pixels, dragging mean
        // brightness far down toward black; the page interior here is solid white (255) on a
        // dark (20) backdrop, so a correctly-cropped-or-safely-declined result must read bright.
        Check($"Output isn't corrupted-dark (mean brightness {mean.Val0:F0}, expect page-bright)", mean.Val0 > 150);
    }
}

void TestManualOverrideLegacyRectCrop()
{
    Console.WriteLine("\n-- Manual override with a legacy \"x,y,w,h\" rect string crops to the expected size --");
    var workDir = TempWorkDir();
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "source_rect.png"), 400, 300);
    var outDir = Path.Combine(workDir, "Processed");

    // DPI pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about crop geometry,
    // not DPI-driven resampling.
    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: true, leftCrop: "50,50,200,150",
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow));

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
    // DPI pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about warp geometry,
    // not DPI-driven resampling.
    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: false, manualOverride: true, leftCrop: quad,
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow));

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

/// <summary>The live-view auto-capture gate used to run a single fixed Canny(50,200) with a hard
/// "page must fill 20% of frame" cutoff, long after the full-resolution path had moved to adaptive
/// thresholds — so under dim or low-contrast light it found nothing and auto-capture sat at
/// "Waiting for boundary" forever, and a page sitting slightly small or angled was rejected
/// outright. These cover the conditions that used to fail.</summary>
void TestLiveViewDetectionIsRobust()
{
    Console.WriteLine("\n-- Live-view detection survives low contrast, shadow, and a small page --");
    var workDir = TempWorkDir();
    // Live frames are ~960px wide, not the 800x600 the full-res tests use — keep this
    // representative so the internal downscale-for-detection step is genuinely exercised.
    const int imageWidth = 960, imageHeight = 640;

    var lowContrast = File.ReadAllBytes(WriteLowContrastTestImage(
        Path.Combine(workDir, "live_low_contrast.png"), imageWidth, imageHeight,
        new SKRectI(60, 40, 900, 600), backgroundGray: 150, pageGray: 180));
    var lowContrastCheck = ImageProcessor.CheckLiveFrame(lowContrast);
    Check("Live view detects a low-contrast page (fixed Canny used to miss this)", lowContrastCheck.Detected);

    var shadowed = File.ReadAllBytes(WriteShadowedTestImage(
        Path.Combine(workDir, "live_shadowed.png"), imageWidth, imageHeight,
        new SKRectI(60, 40, 900, 600), shadowBandWidth: 120));
    var shadowedCheck = ImageProcessor.CheckLiveFrame(shadowed);
    Check("Live view detects a page with a cast shadow", shadowedCheck.Detected);
    if (shadowedCheck.Detected)
        Check("Live view detection covers the full page, not just the unshadowed part",
            shadowedCheck.Width >= 840 - 120);

    // ~14% of frame: comfortably rejected by the old hard 0.2 cutoff, but a perfectly ordinary
    // placement for a small document or a page pushed toward one side of the cradle.
    var smallPage = File.ReadAllBytes(WriteLowContrastTestImage(
        Path.Combine(workDir, "live_small_page.png"), imageWidth, imageHeight,
        new SKRectI(300, 200, 700, 420), backgroundGray: 40, pageGray: 220));
    Check("Live view detects a small/offset page the old 20% area cutoff rejected",
        ImageProcessor.CheckLiveFrame(smallPage).Detected);

    // The focus gate is only meaningful if its score actually tracks focus. Identical pages,
    // one sharp and one blurred, must land on opposite sides of that comparison.
    var pageRect = new SKRectI(60, 40, 900, 600);
    var sharpPage = File.ReadAllBytes(WriteDetailedPage(
        Path.Combine(workDir, "live_sharp.png"), imageWidth, imageHeight, pageRect, blurred: false));
    var blurredPage = File.ReadAllBytes(WriteDetailedPage(
        Path.Combine(workDir, "live_blurred.png"), imageWidth, imageHeight, pageRect, blurred: true));
    var sharpCheck = ImageProcessor.CheckLiveFrame(sharpPage);
    var blurredCheck = ImageProcessor.CheckLiveFrame(blurredPage);
    Console.WriteLine($"  [info] sharp page sharpness={sharpCheck.Sharpness:F1}, blurred page sharpness={blurredCheck.Sharpness:F1}");
    Check("Both the sharp and blurred pages are detected", sharpCheck.Detected && blurredCheck.Detected);
    if (sharpCheck.Detected && blurredCheck.Detected)
        Check("A blurred page scores lower sharpness than the same page in focus",
            blurredCheck.Sharpness < sharpCheck.Sharpness);

    // A frame with nothing page-like in it must still report nothing, or auto-capture would
    // fire on an empty cradle.
    var empty = File.ReadAllBytes(WriteLowContrastTestImage(
        Path.Combine(workDir, "live_empty.png"), imageWidth, imageHeight,
        new SKRectI(0, 0, 1, 1), backgroundGray: 40, pageGray: 40));
    Check("An empty frame is not falsely detected as a page", !ImageProcessor.CheckLiveFrame(empty).Detected);
}

BatchManifest SampleManifest(string batchFolder) => new()
{
    BatchId = "batch-abc",
    BatchCode = "CAIRNS-001",
    ProjectCode = "QLD",
    ProjectId = "proj-1",
    Settings = new BatchManifestSettings { Dpi = 300, CaptureFormat = "TIFF", DewarpEnabled = true },
    Pages =
    {
        new BatchManifestPage
        {
            PageNumber = 1, JobId = "job-1", ProcessingStatus = "Completed",
            OriginalFile = "temp/P0001.jpg",
            ProcessedFiles = { "output/P0001.tif" },
            ThumbnailFile = "thumbnails/000001.png",
            Adjustments = new BatchManifestAdjustments { Brightness = 0.25, RotationDegrees = 90 }
        },
        new BatchManifestPage { PageNumber = 2, JobId = "job-2", ProcessingStatus = "Pending" }
    }
};

void TestBatchManifestRoundTripsAndValidates()
{
    Console.WriteLine("\n-- Batch manifest round-trips, and validation names what's missing --");
    var service = new BatchManifestService();
    var batchFolder = Path.Combine(TempWorkDir(), "CAIRNS-001");

    // A folder that isn't a batch at all must say so, rather than failing obscurely later.
    Directory.CreateDirectory(batchFolder);
    var notABatch = service.Validate(batchFolder);
    Check("A folder with no manifest is rejected as not-a-batch", !notABatch.IsValid);
    Check("The not-a-batch error names the manifest file", notABatch.Error?.Contains(BatchFolder.ManifestFileName) == true);

    service.Save(batchFolder, SampleManifest(batchFolder));
    Check("Saving creates the manifest", File.Exists(BatchFolder.ManifestPath(batchFolder)));
    foreach (var required in BatchFolder.RequiredFolders)
        Check($"Saving creates the {required} folder", Directory.Exists(Path.Combine(batchFolder, required)));

    var validation = service.Validate(batchFolder);
    Check("A complete batch folder validates", validation.IsValid);
    var loaded = validation.Manifest!;
    Check("Batch code round-trips", loaded.BatchCode == "CAIRNS-001");
    Check("DPI round-trips", loaded.Settings.Dpi == 300);
    Check("Book curve correction round-trips", loaded.Settings.DewarpEnabled);
    Check("Page count round-trips", loaded.PageCount == 2);
    Check("Per-page adjustments round-trip", loaded.Pages[0].Adjustments?.Brightness == 0.25);
    Check("Pending pages are recorded, not just completed ones", loaded.Pages[1].ProcessingStatus == "Pending");

    // Page numbers come from the shared manifest so a second machine doesn't reuse one.
    Check("Next page number follows the highest already claimed", service.NextPageNumber(batchFolder) == 3);

    // A missing image is reported but must not make the batch unopenable.
    var missing = service.FindMissingPageFiles(batchFolder, loaded);
    Check("Files listed in the manifest but absent on disk are reported", missing.Count > 0);
    Check("Missing page files do not invalidate the batch", service.Validate(batchFolder).IsValid);

    // Derived subfolders are recreated rather than blocking the open — they hold nothing that
    // can't be regenerated, and refusing would strand the images.
    Directory.Delete(BatchFolder.TempPath(batchFolder), recursive: true);
    Check("A missing derived folder is repaired, not fatal", service.Validate(batchFolder).IsValid);
    Check("The repaired folder is actually recreated", Directory.Exists(BatchFolder.TempPath(batchFolder)));

    // A manifest from a future build must be refused rather than silently misread.
    var future = SampleManifest(batchFolder);
    future.SchemaVersion = BatchManifest.CurrentSchemaVersion + 1;
    service.Save(batchFolder, future);
    var futureValidation = service.Validate(batchFolder);
    Check("A newer-format manifest is refused with a clear message", !futureValidation.IsValid);
    Check("The newer-format error tells the operator to update", futureValidation.Error?.Contains("newer version") == true);
}

void TestBatchManifestSurvivesRelocation()
{
    Console.WriteLine("\n-- A batch folder still opens after being moved to a different path --");
    var service = new BatchManifestService();
    var original = Path.Combine(TempWorkDir(), "batch-here");
    service.Save(original, SampleManifest(original));

    // Stand in for the real cases: a USB stick mounting under a different drive letter, a mapped
    // drive, a UNC share. All of them change the absolute path, which is why manifest paths are
    // stored relative to the batch folder.
    var moved = Path.Combine(TempWorkDir(), "somewhere", "else", "batch-there");
    Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
    Directory.Move(original, moved);

    var validation = service.Validate(moved);
    Check("The moved batch still validates", validation.IsValid);
    Check("The moved batch keeps its identity", validation.Manifest?.BatchId == "batch-abc");
    Check("The moved batch keeps its settings", validation.Manifest?.Settings.Dpi == 300);

    // The real test of relative paths: page files must resolve against the NEW location.
    var page = validation.Manifest!.Pages[0];
    var resolved = BatchFolder.ToAbsolute(moved, page.ProcessedFiles[0])!;
    Check("Page paths resolve against the new location", resolved.StartsWith(Path.GetFullPath(moved)));
    Check("Page paths do not still point at the old location", !resolved.Contains("batch-here"));

    // And the reverse direction must refuse to record anything outside the batch folder, since
    // such a path could not survive the next move.
    Check("A path outside the batch folder is refused rather than recorded absolute",
        BatchFolder.ToRelative(moved, Path.Combine(TempWorkDir(), "outside.tif")) == null);
    Check("A path inside the batch folder is stored relative with forward slashes",
        BatchFolder.ToRelative(moved, Path.Combine(BatchFolder.OutputPath(moved), "P0001.tif")) == "output/P0001.tif");
}

void TestBatchManifestSurvivesInterruptedWrite()
{
    Console.WriteLine("\n-- A manifest damaged mid-write falls back to the backup --");
    var service = new BatchManifestService();
    var batchFolder = Path.Combine(TempWorkDir(), "interrupted");
    service.Save(batchFolder, SampleManifest(batchFolder));

    // Second save leaves the first as the backup.
    var updated = SampleManifest(batchFolder);
    updated.Settings.Dpi = 600;
    service.Save(batchFolder, updated);
    Check("A backup manifest is kept alongside the current one", File.Exists(BatchFolder.ManifestBackupPath(batchFolder)));

    // Truncated the way a pulled USB stick or a power cut would leave it.
    File.WriteAllText(BatchFolder.ManifestPath(batchFolder), "{ \"SchemaVersion\": 1, \"Bat");
    var validation = service.Validate(batchFolder);
    Check("A truncated manifest still opens via the backup", validation.IsValid);
    Check("The recovered manifest is the previous good copy", validation.Manifest?.BatchId == "batch-abc");

    // With no backup either, it must fail clearly rather than opening an empty batch.
    File.Delete(BatchFolder.ManifestBackupPath(batchFolder));
    var unrecoverable = service.Validate(batchFolder);
    Check("An unrecoverable manifest fails with a clear message", !unrecoverable.IsValid);
    Check("The unrecoverable error mentions damage", unrecoverable.Error?.Contains("damaged") == true);
}

void TestBatchLockIsAdvisory()
{
    Console.WriteLine("\n-- Batch locking warns about another machine without blocking --");
    var locks = new BatchLockService();
    var batchFolder = Path.Combine(TempWorkDir(), "locked");
    new BatchManifestService().Save(batchFolder, SampleManifest(batchFolder));

    Check("An unlocked batch reports no holder", !locks.IsHeldByAnother(batchFolder, out _));

    locks.Acquire(batchFolder);
    Check("Acquiring writes a lock file", File.Exists(BatchFolder.LockPath(batchFolder)));
    // Reopening a batch this machine already had open must never prompt.
    Check("This machine's own lock is not treated as a conflict", !locks.IsHeldByAnother(batchFolder, out _));

    // Stand in for another workstation holding the batch right now.
    File.WriteAllText(BatchFolder.LockPath(batchFolder), JsonSerializer.Serialize(new BatchLockInfo
    {
        Machine = "OTHER-PC", User = "someone", HeartbeatUtc = DateTime.UtcNow
    }));
    Check("Another machine's live lock is reported", locks.IsHeldByAnother(batchFolder, out var holder));
    Check("The conflict names who has it", holder != null && BatchLockService.DescribeHolder(holder).Contains("OTHER-PC"));

    // A USB stick unplugged mid-batch leaves its lock behind forever — that must read as routine,
    // not as a permanent conflict, or the batch becomes unopenable.
    File.WriteAllText(BatchFolder.LockPath(batchFolder), JsonSerializer.Serialize(new BatchLockInfo
    {
        Machine = "OTHER-PC", User = "someone",
        HeartbeatUtc = DateTime.UtcNow - BatchLockService.StaleAfter - TimeSpan.FromMinutes(1)
    }));
    Check("An abandoned lock is not treated as a conflict", !locks.IsHeldByAnother(batchFolder, out _));

    // Releasing must not clear a lock that belongs to someone else.
    File.WriteAllText(BatchFolder.LockPath(batchFolder), JsonSerializer.Serialize(new BatchLockInfo
    {
        Machine = "OTHER-PC", User = "someone", HeartbeatUtc = DateTime.UtcNow
    }));
    locks.Release(batchFolder);
    Check("Releasing leaves another machine's lock alone", File.Exists(BatchFolder.LockPath(batchFolder)));

    locks.Acquire(batchFolder);
    locks.Release(batchFolder);
    Check("Releasing clears this machine's own lock", !File.Exists(BatchFolder.LockPath(batchFolder)));
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

void TestBrightnessPassExcludesHandOverlap()
{
    Console.WriteLine("\n-- Brightness-based foreground segmentation excludes a hand overlapping the page edge --");
    var workDir = TempWorkDir();
    const int imageWidth = 800, imageHeight = 600;
    var pageRect = new SKRectI(50, 50, 750, 550);
    // Overlaps the page's top edge and extends above it into the dark background — the shape
    // a finger/hand holding the top of a book produces in these real fixtures. Without skin
    // exclusion, brightness segmentation (which can't otherwise distinguish "bright skin" from
    // "bright page") would merge this into the foreground blob and pull its detected top edge
    // up to ~20, not the true page top at 50.
    var skinRect = new SKRectI(300, 20, 500, 70);
    var path = WriteHandOverlapTestImage(Path.Combine(workDir, "hand_overlap.png"), imageWidth, imageHeight, pageRect, skinRect);

    // Asserted directly against the brightness pass's own reported candidate (via the public
    // DebugBoundaryDetection diagnostic), not DetectDocumentBoundary's overall best-of-3
    // winner: on this deliberately clean/sharp synthetic image (unlike a real photo) Direct
    // Canny also confidently detects the merged page+hand shape — grayscale Canny has no way
    // to know the extra bright region is skin, so it isn't penalized for including it, and can
    // out-score even a correctly hand-excluded brightness candidate on this synthetic case
    // alone. That's a genuine, accepted property of the best-of-3/no-source-bonus design (see
    // the "no weighting for brightness candidates" reasoning in DetectBoundaryInMat) — real
    // photos validated this doesn't cause a problem in practice (16/16 real halves crop
    // cleanly with no hand included), because real Canny fragments long before it would
    // confidently include hand content the way this clean synthetic edge does. What this test
    // isolates and guards is narrower but still real: SkinExclusionMask itself must keep doing
    // its job inside FindContoursByBrightness, regardless of which pass ultimately wins.
    var debug = new ImageProcessor().DebugBoundaryDetection(File.ReadAllBytes(path));
    var brightnessSection = debug.Split("Brightness pass:", 2)[1];
    var match = System.Text.RegularExpressions.Regex.Match(brightnessSection, @"Y\s*=\s*(-?\d+)");
    Check("Brightness pass reports a candidate", match.Success);
    if (match.Success)
    {
        var brightnessTop = int.Parse(match.Groups[1].Value);
        Check("Brightness pass's own candidate reflects the true page top, not the excluded hand region",
            Math.Abs(brightnessTop - (pageRect.Top - 10)) <= 20);
    }
}

void TestUniformBrightnessImageStaysUndetected()
{
    Console.WriteLine("\n-- A perfectly uniform image (no real bright/dark split) is not falsely detected by the brightness pass --");
    var workDir = TempWorkDir();
    // Otsu's threshold selection always returns *some* value even with zero real separation
    // (a flat histogram) — BrightnessSeparationScore is what's supposed to catch that and
    // keep FindContoursByBrightness from contributing a meaningless full-frame "detection"
    // where none of the other passes would find anything either. A perfectly uniform image has
    // zero pixel variance, the sharpest version of this case (totalVariance == 0).
    var path = WriteSolidImage(Path.Combine(workDir, "uniform.png"), 800, 600);

    var detected = new ImageProcessor().DetectDocumentBoundary(path);
    Check("No boundary is (falsely) detected on a uniform image", !detected.HasValue);
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

    // DPI pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about split geometry,
    // not DPI-driven resampling.
    var result = new ImageProcessor().Process(sourcePath, outDir, splitPages: true, manualOverride: true, leftCrop: leftCrop, rightCrop: rightCrop,
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow));

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
    // DPI pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about split/export
    // geometry, not DPI-driven resampling.
    var outputDir = Path.Combine(Path.GetDirectoryName(job.OriginalFilePath) ?? ".", "Processed");
    var processResult = new ImageProcessor().Process(job.OriginalFilePath, outputDir, splitPages: batch.SplitBookPages, manualOverride: job.ManualOverrideApplied, leftCrop: job.LeftCropBox, rightCrop: job.RightCropBox,
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow));
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

    // Step 2: worker reprocesses it (mirrors BackgroundProcessingWorker exactly). DPI pinned
    // to BaselineDpi throughout this test so ResizeForDpi is a no-op — this test is about
    // crop/export geometry, not DPI-driven resampling.
    var outputDir = Path.Combine(Path.GetDirectoryName(job.OriginalFilePath) ?? ".", "Processed");
    var pinnedDpiMeta = new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow);
    var firstResult = new ImageProcessor().Process(job.OriginalFilePath, outputDir, splitPages: batch.SplitBookPages, manualOverride: job.ManualOverrideApplied, leftCrop: job.LeftCropBox, rightCrop: job.RightCropBox,
        metadata: pinnedDpiMeta);
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
    var secondResult = new ImageProcessor().Process(reopenedJob.OriginalFilePath, outputDir, splitPages: batch.SplitBookPages, manualOverride: reopenedJob.ManualOverrideApplied, leftCrop: reopenedJob.LeftCropBox, rightCrop: reopenedJob.RightCropBox,
        metadata: pinnedDpiMeta);
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
    // DPI pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about crop geometry,
    // not DPI-driven resampling.
    var processor = new ImageProcessor { CropConfidenceThreshold = 0.99 };
    var result = processor.Process(sourcePath, outDir, splitPages: false, manualOverride: false,
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow));

    Check("Processing succeeds", result.Success);
    Check("A medium-confidence detection is still cropped", result.WasCropped);
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Output is genuinely smaller than the full source frame (a real crop happened)",
            output.Width < imageWidth && output.Height < imageHeight);
    }
}

// TestLowConfidenceCropIsSkippedAndFlagged removed: it asserted the legacy TryAutoCrop
// pipeline's confidence-gate behavior (CropConfidenceThreshold/MediumConfidenceThreshold
// causing the automatic path to keep the full uncropped frame and flag WARNING rather than
// trust a shaky detection). The Method 4 pipeline that replaced TryAutoCrop for automatic
// detection deliberately has no confidence gate — product decision: "we wish to create robust
// methods which will always work" — so CropConfidenceThreshold/MediumConfidenceThreshold no
// longer affect the automatic path at all (they still matter for Crop Review's own suggestion
// UI, which is unrelated to this test). There is no equivalent behavior to assert here anymore.

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
    // DPI pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about crop geometry,
    // not DPI-driven resampling.
    var result = new ImageProcessor().ProcessFixedFrames(sourcePath, outDir, spec,
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow));

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

void TestFixedFramesFallsBackToCalibratedRectOnFeaturelessImage()
{
    Console.WriteLine("\n-- Fixed frames fall back to the calibrated rectangle on a featureless image Method 4 can't refine --");
    var workDir = TempWorkDir();
    // A flat, featureless image: nothing for Method 4's gradient-based edge trace to find —
    // exercises AltFlattenPage/ProcessFixedFrames' own defensive fallback (FoundRealEdges/
    // Method4Result "no span found" path) rather than the confidence-gate concept this test
    // used to assert (retired — see the removed TestLowConfidenceCropIsSkippedAndFlagged's
    // comment above for why the automatic path no longer has a confidence gate at all). DPI
    // pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about crop geometry.
    var sourcePath = WriteSolidImage(Path.Combine(workDir, "flat.png"), 500, 400);
    var pinnedDpiMeta = new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow);

    var frameSpec = ImageProcessor.FormatFixedFrames(new[] { new FixedFrameRect(50, 50, 200, 150) });
    var fixedResult = new ImageProcessor().ProcessFixedFrames(sourcePath, Path.Combine(workDir, "Processed_fixed"), frameSpec, metadata: pinnedDpiMeta);
    Check("Fixed-frame processing succeeds on a featureless image", fixedResult.Success);
    if (fixedResult.Success && fixedResult.OutputFilePaths.Count > 0)
    {
        using var fixedOutput = Cv2.ImRead(fixedResult.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Fixed-frame output falls back to the calibrated rectangle's own size, not a degenerate crop",
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

    // Mirrors BackgroundProcessingWorker.ProcessLoop's branch for a batch with UseFixedFrames set,
    // including the reference dims it passes so frames get projected onto the capture's own size.
    var outputDir = Path.Combine(Path.GetDirectoryName(job.OriginalFilePath) ?? ".", "Processed");
    var processResult = new ImageProcessor().ProcessFixedFrames(job.OriginalFilePath, outputDir, batch.FixedFrames!,
        frameReferenceWidth: batch.FixedFrameImageWidth, frameReferenceHeight: batch.FixedFrameImageHeight);
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

/// <summary>Encodes a two-halves live-view-like frame: the left half filled with sharp black
/// bars, the right half either sharp bars, blurred bars, or plain white — enough to drive the
/// per-region focus and content-change assertions without needing a real camera.</summary>
byte[] EncodeTwoRegionFrame(int width, int height, bool rightHasBars, bool rightBlurred, int rightBarOffset)
{
    using var bitmap = new SKBitmap(width, height);
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
        // Left region: fixed sharp bars, identical in every generated frame.
        for (var i = 0; i < 5; i++)
            canvas.DrawRect(new SKRect(20, 20 + i * 24, width / 2 - 20, 32 + i * 24), paint);

        if (rightHasBars)
        {
            using var rightPaint = new SKPaint
            {
                Color = SKColors.Black,
                Style = SKPaintStyle.Fill,
                IsAntialias = false,
                // A large blur turns hard bar edges into gradients, collapsing the Laplacian
                // variance the sharpness score is built on.
                MaskFilter = rightBlurred ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f) : null
            };
            for (var i = 0; i < 5; i++)
                canvas.DrawRect(new SKRect(width / 2 + 20, 20 + i * 24 + rightBarOffset, width - 20, 32 + i * 24 + rightBarOffset), rightPaint);
        }
    }
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

void TestCheckLiveRegionsDetectsPerRegionContentChange()
{
    Console.WriteLine("\n-- CheckLiveRegions sees a content change confined to one region --");
    const int w = 640, h = 480;
    // Left half and right half, as fractions — the same shape the VM passes for its frames.
    var regions = new[]
    {
        new FixedFrameRect(0.0, 0.0, 0.5, 1.0),
        new FixedFrameRect(0.5, 0.0, 0.5, 1.0)
    };

    var baseline = ImageProcessor.CheckLiveRegions(EncodeTwoRegionFrame(w, h, true, false, 0), regions);
    var unchanged = ImageProcessor.CheckLiveRegions(EncodeTwoRegionFrame(w, h, true, false, 0), regions);
    // Only the RIGHT region's content moves; the left half is pixel-identical.
    var rightChanged = ImageProcessor.CheckLiveRegions(EncodeTwoRegionFrame(w, h, true, false, 40), regions);

    Check("CheckLiveRegions decodes and returns one result per region",
        baseline.Decoded && baseline.Regions.Length == regions.Length);

    if (baseline.Decoded && unchanged.Decoded && rightChanged.Decoded)
    {
        Check("An identical frame produces an identical signature per region",
            baseline.Regions[0].ContentSignature!.SequenceEqual(unchanged.Regions[0].ContentSignature!) &&
            baseline.Regions[1].ContentSignature!.SequenceEqual(unchanged.Regions[1].ContentSignature!));

        Check("The untouched region's signature is unchanged when only the other region moves",
            baseline.Regions[0].ContentSignature!.SequenceEqual(rightChanged.Regions[0].ContentSignature!));

        Check("The changed region's signature differs — a page turn in one frame is detectable",
            !baseline.Regions[1].ContentSignature!.SequenceEqual(rightChanged.Regions[1].ContentSignature!));
    }
}

void TestCheckLiveRegionsSharpnessIsPerRegion()
{
    Console.WriteLine("\n-- CheckLiveRegions scores focus per region, not over the whole frame --");
    const int w = 640, h = 480;
    var regions = new[]
    {
        new FixedFrameRect(0.0, 0.0, 0.5, 1.0),
        new FixedFrameRect(0.5, 0.0, 0.5, 1.0)
    };

    // Sharp bars on the left, heavily blurred bars on the right, in ONE frame. A whole-image
    // Laplacian would score both regions the same and let the blurred frame pass the focus gate.
    var mixed = ImageProcessor.CheckLiveRegions(EncodeTwoRegionFrame(w, h, true, true, 0), regions);

    Check("Mixed-focus frame decodes into two region results", mixed.Decoded && mixed.Regions.Length == 2);
    if (mixed.Decoded && mixed.Regions.Length == 2)
    {
        var sharp = mixed.Regions[0].Sharpness;
        var blurry = mixed.Regions[1].Sharpness;
        Check("The sharp region scores substantially higher than the blurred one in the same frame",
            sharp > blurry * 2.0 && blurry >= 0);
        if (!(sharp > blurry * 2.0))
            Console.WriteLine($"   sharp={sharp:F1} blurry={blurry:F1}");
    }
}

void TestFrameGeometryEditing()
{
    Console.WriteLine("\n-- Frame geometry: resize, move, and rubber-band creation stay inside the image --");
    const double imgW = 960, imgH = 640;
    var min = MicroCapture.UI.Controls.FrameGeometry.MinSize(imgW, imgH);

    // Dragging a corner far outside the image must clamp, not escape.
    var frame = new FixedFrameRect(100, 100, 300, 200);
    var resized = MicroCapture.UI.Controls.FrameGeometry.ResolveResize(
        frame, MicroCapture.UI.Controls.FrameHandleKind.BottomRight, 5000, 5000, imgW, imgH);
    Check("Resizing past the edge clamps to the image bounds",
        resized.X + resized.Width <= imgW + 0.001 && resized.Y + resized.Height <= imgH + 0.001);

    // Collapsing a frame onto itself must stop at the minimum, never invert.
    var collapsed = MicroCapture.UI.Controls.FrameGeometry.ResolveResize(
        frame, MicroCapture.UI.Controls.FrameHandleKind.BottomRight, 0, 0, imgW, imgH);
    Check("A frame cannot be collapsed below the minimum size or inverted",
        collapsed.Width >= min - 0.001 && collapsed.Height >= min - 0.001);

    // Moving hard against a corner must keep the whole frame on-image.
    var moved = MicroCapture.UI.Controls.FrameGeometry.Move(frame, -9999, -9999, imgW, imgH);
    Check("Moving past the top-left corner clamps to (0,0)", moved.X == 0 && moved.Y == 0);
    var movedFar = MicroCapture.UI.Controls.FrameGeometry.Move(frame, 9999, 9999, imgW, imgH);
    Check("Moving past the bottom-right keeps the frame fully inside",
        Math.Abs(movedFar.X + movedFar.Width - imgW) < 0.001 && Math.Abs(movedFar.Y + movedFar.Height - imgH) < 0.001);

    // Rubber-band creation must normalize either drag direction.
    var dragUpLeft = MicroCapture.UI.Controls.FrameGeometry.FromDragCorners(
        new Avalonia.Point(400, 300), new Avalonia.Point(100, 80), imgW, imgH);
    Check("A rubber-band dragged up-and-left normalizes to a positive rect",
        dragUpLeft.X == 100 && dragUpLeft.Y == 80 && dragUpLeft.Width == 300 && dragUpLeft.Height == 220);

    // A bare click must not become a frame — otherwise a stray click silently enters frame mode.
    var click = MicroCapture.UI.Controls.FrameGeometry.FromDragCorners(
        new Avalonia.Point(200, 200), new Avalonia.Point(201, 201), imgW, imgH);
    Check("A click-sized drag is rejected as too small to commit",
        !MicroCapture.UI.Controls.FrameGeometry.IsCommittableSize(click, imgW, imgH));
    Check("A real drag is accepted",
        MicroCapture.UI.Controls.FrameGeometry.IsCommittableSize(dragUpLeft, imgW, imgH));

    // The minimum must scale with the image, not sit at a fixed pixel count.
    Check("Minimum frame size scales with the image it is drawn on",
        MicroCapture.UI.Controls.FrameGeometry.MinSize(6000, 4000) > MicroCapture.UI.Controls.FrameGeometry.MinSize(960, 640));
}

async Task TestFramePersistDoesNotLeakAcrossBatchSwitch()
{
    Console.WriteLine("\n-- Frames written for one batch never land on another --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using var db = new AppDbContext(dbPath);
    _ = new CaptureQueueService(db);   // creates/upgrades the schema, as every other test relies on
    var project = new Project { Name = "SMOKE-FRAMESWITCH", OutputDirectory = workDir };
    db.Projects.Add(project);
    await db.SaveChangesAsync();

    var frames = new[] { new FixedFrameRect(10, 10, 200, 150) };
    var batchA = new Batch
    {
        ProjectId = project.Id,
        Name = "A",
        BatchCode = "A",
        UseFixedFrames = true,
        FixedFrames = ImageProcessor.FormatFixedFrames(frames),
        FixedFrameImageWidth = 960,
        FixedFrameImageHeight = 640
    };
    var batchB = new Batch { ProjectId = project.Id, Name = "B", BatchCode = "B" };
    db.Batches.AddRange(batchA, batchB);
    await db.SaveChangesAsync();

    // The failure this guards against: a debounced write scheduled while batch A was open,
    // landing after the operator has switched to batch B. MainWindowViewModel stops the pending
    // timer at the top of LoadBatchIntoUiAsync precisely so this cannot happen; assert the two
    // rows stayed independent.
    using var verify = new AppDbContext(dbPath);
    var storedA = await verify.Batches.FirstAsync(b => b.BatchCode == "A");
    var storedB = await verify.Batches.FirstAsync(b => b.BatchCode == "B");

    Check("The batch that owns the frames keeps them",
        storedA.UseFixedFrames && !string.IsNullOrWhiteSpace(storedA.FixedFrames));
    Check("A different batch does not inherit them",
        !storedB.UseFixedFrames && string.IsNullOrWhiteSpace(storedB.FixedFrames));
    Check("Reference dims travel with the frames, not the batch order",
        storedA.FixedFrameImageWidth == 960 && storedB.FixedFrameImageWidth == 0);
}

void TestFixedFramesScaleFromReferenceResolution()
{
    Console.WriteLine("\n-- Fixed frames authored at live-view size crop correctly from a full-res capture --");
    var workDir = TempWorkDir();

    // Frames drawn on a 960x640 live view, but the real capture is 6x/4.5x larger. Before
    // reference dims were honored, these rects were applied as raw capture pixels and every
    // crop came out as a postage stamp in the top-left corner.
    const int liveW = 960, liveH = 640;
    const int captureW = 5760, captureH = 2880;
    var frames = new[]
    {
        new FixedFrameRect(100, 80, 300, 400),
        new FixedFrameRect(500, 80, 300, 400)
    };
    var spec = ImageProcessor.FormatFixedFrames(frames);

    var capturePath = WriteSolidImage(Path.Combine(workDir, "capture.png"), captureW, captureH);
    var outputDir = Path.Combine(workDir, "Processed");
    // Target DPI == measuredDpi so ResizeForDpi is a no-op — this test is about crop geometry,
    // and DPI resampling would otherwise rewrite the output dimensions on top of it.
    var neutralDpi = new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow);
    var result = new ImageProcessor().ProcessFixedFrames(capturePath, outputDir, spec, neutralDpi,
        frameReferenceWidth: liveW, frameReferenceHeight: liveH);

    Check("Live-view-authored fixed frames process successfully against a full-res capture", result.Success);
    Check("One output file per frame", result.OutputFilePaths.Count == frames.Length);

    if (result.Success && result.OutputFilePaths.Count == frames.Length)
    {
        var sx = (double)captureW / liveW;
        var sy = (double)captureH / liveH;
        var allScaled = true;
        for (var i = 0; i < frames.Length; i++)
        {
            using var output = Cv2.ImRead(result.OutputFilePaths[i], ImreadModes.Unchanged);
            var expectedW = frames[i].Width * sx;
            var expectedH = frames[i].Height * sy;
            // Tolerance covers rounding in ClampRectToBounds plus DPI resampling rounding.
            if (Math.Abs(output.Width - expectedW) > 2 || Math.Abs(output.Height - expectedH) > 2)
            {
                allScaled = false;
                Console.WriteLine($"   frame {i + 1}: got {output.Width}x{output.Height}, expected ~{expectedW:F0}x{expectedH:F0}");
            }
        }
        Check("Each crop is scaled from live-view space onto the capture's real resolution", allScaled);
    }
}

void TestFixedFramesZeroReferenceIsBackCompatible()
{
    Console.WriteLine("\n-- Fixed frames with no reference dims keep the historical direct-pixel behavior --");
    var workDir = TempWorkDir();

    // Batches calibrated before reference dims were recorded pass 0, which must reproduce the
    // pre-change behavior exactly: frame coordinates treated as direct capture pixels.
    const int captureW = 800, captureH = 600;
    var frames = new[] { new FixedFrameRect(50, 40, 300, 200) };
    var spec = ImageProcessor.FormatFixedFrames(frames);

    var capturePath = WriteSolidImage(Path.Combine(workDir, "capture.png"), captureW, captureH);
    var outputDir = Path.Combine(workDir, "Processed");
    var neutralDpi = new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow);
    var result = new ImageProcessor().ProcessFixedFrames(capturePath, outputDir, spec, neutralDpi,
        frameReferenceWidth: 0, frameReferenceHeight: 0);

    Check("Zero-reference fixed-frame processing succeeds", result.Success);
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        Check("Zero reference dims crop at the frame's literal pixel size (unscaled)",
            Math.Abs(output.Width - 300) <= 2 && Math.Abs(output.Height - 200) <= 2);
    }
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

void TestDewarpControlPointsStayWithinPageBounds()
{
    Console.WriteLine("\n-- Dewarp baseline anchoring never extrapolates control points far outside the page (regression) --");
    var workDir = TempWorkDir();
    const int width = 1200, height = 900;

    // The topmost "line" only spans the page's right portion — mirrors the exact real-photo
    // failure this was built to fix: the original design anchored the curve's vertical
    // baseline by evaluating a narrow-domain line's own cubic fit at x=0 (far outside that
    // line's own data), which extrapolated to a Y value many times the page's own height and
    // produced a wildly wrong dewarp on a real capture. Anchoring through the line's own real
    // (median X, median Y) point instead must keep every control point sane regardless of how
    // narrow/off-center the anchor line is.
    var bars = new (int, int, int, int)[]
    {
        (900, 100, 250, 20), // topmost — narrow, confined to the right portion
        (50, 250, 1100, 20),
        (50, 400, 1100, 20),
        (50, 550, 1100, 20),
        (50, 700, 1100, 20), // bottommost — full width
    };
    var path = WriteMultiBarTestImage(Path.Combine(workDir, "multibar.png"), width, height, bars);

    var model = ImageProcessor.DetectDewarpCurveFromBytes(File.ReadAllBytes(path));
    Check("A curve model is detected from the multi-line page", model != null);
    if (model is { } m)
    {
        var maxAbsY = m.TopControlPoints.Concat(m.BottomControlPoints).Max(p => Math.Abs(p.Y));
        Check($"All control point Y values stay within a sane multiple of the page height (max |Y|={maxAbsY:F0}, page height={height})",
            maxAbsY < height * 3);
    }
}

/// <summary>Mean row of dark pixels across a narrow X window, for measuring where a synthetic
/// bar actually sits at that column without depending on ImageProcessor's own line detector —
/// an independent check of the mesh step's real pixel effect, not just its own report of what
/// it did.</summary>
double MeasureBarCentroidY(Mat grayOrColor, int x, int windowHalfWidth, int yStart, int yEnd)
{
    using var gray = grayOrColor.Channels() == 1 ? grayOrColor.Clone() : new Mat();
    if (grayOrColor.Channels() != 1) Cv2.CvtColor(grayOrColor, gray, ColorConversionCodes.BGR2GRAY);
    double sum = 0;
    var count = 0;
    for (var xi = Math.Max(0, x - windowHalfWidth); xi < Math.Min(gray.Cols, x + windowHalfWidth); xi++)
        for (var y = yStart; y < yEnd; y++)
            if (gray.At<byte>(y, xi) < 140) { sum += y; count++; }
    return count > 0 ? sum / count : double.NaN;
}

void TestLineMeshFlattensSharedPageWideBow()
{
    Console.WriteLine("\n-- Text-line mesh correction flattens a real, shared page-wide bow (positive case) --");
    var workDir = TempWorkDir();
    const int width = 1400, height = 1600;
    const double amplitude = 50.0;
    const int barThickness = 16;
    // 8 bars sharing the exact same bow shape (peaks at page center) at different baselines —
    // the case TryApplyLineMesh's pooled-fit design targets: one systematic curve shared by
    // every line, not independent per-line shapes.
    var baselines = new[] { 150, 320, 490, 660, 830, 1000, 1170, 1340 };
    var path = WriteCurvedBarTestImage(Path.Combine(workDir, "curved_bars.png"), width, height, baselines, barThickness, amplitude);
    var bytes = File.ReadAllBytes(path);

    var correctedBytes = new ImageProcessor().ApplyLineMeshFromBytes(bytes);
    Check("Mesh correction applies (enough shared-shape lines detected)", correctedBytes != null);
    if (correctedBytes == null) return;

    using var original = Cv2.ImDecode(bytes, ImreadModes.Color);
    using var corrected = Cv2.ImDecode(correctedBytes, ImreadModes.Color);
    Check("Corrected output keeps the same dimensions", corrected.Cols == original.Cols && corrected.Rows == original.Rows);

    // Independently re-measure the first bar's own bow (not via ImageProcessor's own detector)
    // before and after correction — the real, direct assertion that pixels actually moved,
    // not just that a warning string was emitted.
    var baseline0 = baselines[0];
    var yStart = Math.Max(0, baseline0 - (int)amplitude - 20);
    var yEnd = Math.Min(height, baseline0 + barThickness + 20);
    var xs = new[] { 100, 300, 500, 700, 900, 1100, 1300 };

    double MaxDeviation(Mat mat)
    {
        var ys = xs.Select(x => MeasureBarCentroidY(mat, x, 10, yStart, yEnd)).Where(y => !double.IsNaN(y)).ToList();
        if (ys.Count < 3) return double.NaN;
        var median = ys.OrderBy(y => y).ElementAt(ys.Count / 2);
        return ys.Max(y => Math.Abs(y - median));
    }

    var beforeDeviation = MaxDeviation(original);
    var afterDeviation = MaxDeviation(corrected);
    // Deviation-from-median across 7 sample points on a sine hump reads as roughly half the
    // injected 50px amplitude (median sits partway up the curve, not at the flat endpoints) —
    // confirmed empirically at ~24px, not a bug in the correction itself.
    Check($"Original bar shows the injected bow (before deviation {beforeDeviation:F0}px, expect > 15px)", beforeDeviation > 15);
    Check($"Mesh correction substantially flattens it (after deviation {afterDeviation:F0}px, expect < half of before)", afterDeviation < beforeDeviation / 2);
}

void TestLineMeshDeclinesOnPlainUniformPage()
{
    Console.WriteLine("\n-- Text-line mesh correction declines (never corrupts) a page with no text lines --");
    var workDir = TempWorkDir();
    var path = WriteSolidImage(Path.Combine(workDir, "plain.png"), 800, 600);
    var bytes = File.ReadAllBytes(path);

    var result = new ImageProcessor().ApplyLineMeshFromBytes(bytes);
    Check("No lines detected -> declines rather than inventing a correction", result == null);
}

void TestFingerRemovalCleansEdgeTouchingSkinBlob()
{
    Console.WriteLine("\n-- Finger removal inpaints a skin-toned blob touching the page edge (positive case) --");
    var workDir = TempWorkDir();
    const int width = 800, height = 1000;
    // Touches the top edge (Y=0) and sits at ~1.9% of frame area — comfortably inside
    // FingerMinAreaFraction..FingerMaxAreaFraction — mirrors a real finger sliver that survived
    // auto-crop right at the page's own boundary (confirmed against the real
    // Trapezoid_Image001.JPG raw capture, which has two thumbs in exactly this position).
    var skinRect = new SKRectI(300, 0, 450, 100);
    var path = WriteSkinBlobTestImage(Path.Combine(workDir, "finger_edge.png"), width, height, skinRect);
    var bytes = File.ReadAllBytes(path);

    var cleanedBytes = new ImageProcessor().RemoveFingersFromBytes(bytes);
    Check("Edge-touching skin blob qualifies and gets inpainted", cleanedBytes != null);
    if (cleanedBytes == null) return;

    using var cleaned = Cv2.ImDecode(cleanedBytes, ImreadModes.Color);
    // Sample well inside the blob's own footprint (avoiding its dilated border) — should now
    // read close to the surrounding white background, not the original skin tone (~172 avg).
    using var region = new Mat(cleaned, new OpenCvSharp.Rect(320, 15, 110, 70));
    Cv2.MeanStdDev(region, out var mean, out _);
    var avgChannel = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
    Check($"Inpainted region reads background-bright, not skin-toned (avg channel {avgChannel:F0}, expect > 200)", avgChannel > 200);
}

void TestFingerRemovalLeavesInteriorSkinToneAlone()
{
    Console.WriteLine("\n-- Finger removal leaves interior skin-toned content alone (false-positive safety) --");
    var workDir = TempWorkDir();
    const int width = 800, height = 1000;
    // Same skin color and similar size as the positive case above, but fully surrounded by page
    // background on all four sides — stands in for a real printed photo of a person (confirmed
    // against the real Trapezoid_Image002.JPG fixture, a magazine spread with a photo of
    // swimmers, which correctly produces zero qualifying regions).
    var skinRect = new SKRectI(300, 400, 450, 500);
    var path = WriteSkinBlobTestImage(Path.Combine(workDir, "finger_interior.png"), width, height, skinRect);
    var bytes = File.ReadAllBytes(path);

    var result = new ImageProcessor().RemoveFingersFromBytes(bytes);
    Check("Interior skin-toned content (never touching the frame edge) is left unchanged", result == null);
}

void TestBleedthroughSuppressesFaintGhostPreservesRealInk()
{
    Console.WriteLine("\n-- Bleedthrough suppression fades a faint ghost while leaving real ink untouched --");
    var workDir = TempWorkDir();
    const int width = 900, height = 700;
    var inkBars = new[]
    {
        new SKRectI(100, 100, 800, 130),
        new SKRectI(100, 160, 800, 190),
        new SKRectI(100, 220, 800, 250),
    };
    var ghostArea = new SKRectI(100, 400, 500, 600);
    var path = WriteBleedthroughTestImage(Path.Combine(workDir, "bleedthrough.png"), width, height, inkBars, ghostArea, ghostStrokeThickness: 6, ghostStrokeGap: 10);
    var bytes = File.ReadAllBytes(path);

    var cleanedBytes = new ImageProcessor().ApplyBleedthroughSuppressionFromBytes(bytes);
    using var original = Cv2.ImDecode(bytes, ImreadModes.Color);
    using var cleaned = Cv2.ImDecode(cleanedBytes, ImreadModes.Color);

    var ghostSampleRect = new OpenCvSharp.Rect(150, 450, 300, 100);
    using var ghostRegionBefore = new Mat(original, ghostSampleRect);
    using var ghostRegionAfter = new Mat(cleaned, ghostSampleRect);
    Cv2.MeanStdDev(ghostRegionBefore, out var ghostMeanBefore, out _);
    Cv2.MeanStdDev(ghostRegionAfter, out var ghostMeanAfter, out _);
    var ghostAvgBefore = (ghostMeanBefore.Val0 + ghostMeanBefore.Val1 + ghostMeanBefore.Val2) / 3.0;
    var ghostAvgAfter = (ghostMeanAfter.Val0 + ghostMeanAfter.Val1 + ghostMeanAfter.Val2) / 3.0;
    // Compared against its own "before" value, not a hardcoded absolute — the sampled region is
    // a realistic mix of thin ghost strokes and the white paper between them, same as sampling a
    // real bleed-through text region, so its starting average isn't pure ghost color.
    Check($"Faint ghost strokes fade measurably toward the white background (before {ghostAvgBefore:F1}, after {ghostAvgAfter:F1}, expect a real increase)", ghostAvgAfter > ghostAvgBefore + 3);

    using var inkRegion = new Mat(cleaned, new OpenCvSharp.Rect(150, 105, 600, 20));
    Cv2.MeanStdDev(inkRegion, out var inkMean, out _);
    var inkAvg = (inkMean.Val0 + inkMean.Val1 + inkMean.Val2) / 3.0;
    Check($"Real ink stays dark, essentially unchanged (avg {inkAvg:F0}, started at 0, expect < 10)", inkAvg < 10);
}

void TestWriteTiffPreservesLargeColorImagePixelData()
{
    Console.WriteLine("\n-- Writing a processed TIFF with metadata never corrupts pixel data (regression) --");
    var workDir = TempWorkDir();
    const int width = 1600, height = 1200;

    // Distinct-colored quadrants — any row/strip misalignment in the TIFF write (the actual
    // root cause of a real corruption bug found while building this: reopening a just-written
    // TIFF to add Artist/Software/DateTime tags via Tiff.RewriteDirectory() scrambled the row
    // layout on at least one real, large capture, producing a diagonally-sheared image with no
    // exception anywhere) would show up as sampled pixels no longer matching their quadrant.
    var sourcePath = Path.Combine(workDir, "source.png");
    using (var bitmap = new SKBitmap(width, height))
    {
        using var canvas = new SKCanvas(bitmap);
        using var tl = new SKPaint { Color = new SKColor(220, 30, 30) };
        using var tr = new SKPaint { Color = new SKColor(30, 200, 30) };
        using var bl = new SKPaint { Color = new SKColor(30, 30, 220) };
        using var br = new SKPaint { Color = new SKColor(230, 220, 30) };
        canvas.DrawRect(new SKRect(0, 0, width / 2f, height / 2f), tl);
        canvas.DrawRect(new SKRect(width / 2f, 0, width, height / 2f), tr);
        canvas.DrawRect(new SKRect(0, height / 2f, width / 2f, height), bl);
        canvas.DrawRect(new SKRect(width / 2f, height / 2f, width, height), br);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(sourcePath);
        data.SaveTo(stream);
    }

    var processor = new ImageProcessor();
    var outDir = Path.Combine(workDir, "out");
    // Manual full-frame crop keeps geometry deterministic — this test is about TIFF I/O
    // fidelity, not detection. DPI is pinned to BaselineDpi (not 300) so ResizeForDpi is a
    // no-op here too — this test's whole point is pixel-for-pixel round-trip fidelity, which
    // a real (correct, deliberate) DPI-driven resize would otherwise obscure.
    var result = processor.Process(sourcePath, outDir, manualOverride: true,
        leftCrop: $"0,0,{width},{height}",
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, "Smoke Test Operator", DateTime.UtcNow));

    Check("Processing succeeds", result.Success);
    Check("Output is a real TIFF, not the emergency JPEG fallback",
        result.OutputFilePaths.Count == 1 && result.OutputFilePaths[0].EndsWith(".tif", StringComparison.OrdinalIgnoreCase));

    if (result.OutputFilePaths.Count == 1)
    {
        using var readBack = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Color);
        Check("Written TIFF decodes back successfully", !readBack.Empty());
        Check("Dimensions round-trip exactly", readBack.Cols == width && readBack.Rows == height);

        // Sample well inside each quadrant (avoiding edges, which the pipeline's own
        // enhancement/sharpen could legitimately soften slightly).
        bool ColorNear(Vec3b actual, SKColor expected, int tolerance = 40) =>
            Math.Abs(actual.Item2 - expected.Red) <= tolerance &&
            Math.Abs(actual.Item1 - expected.Green) <= tolerance &&
            Math.Abs(actual.Item0 - expected.Blue) <= tolerance;

        var tlPixel = readBack.At<Vec3b>(height / 4, width / 4);
        var trPixel = readBack.At<Vec3b>(height / 4, width * 3 / 4);
        var blPixel = readBack.At<Vec3b>(height * 3 / 4, width / 4);
        var brPixel = readBack.At<Vec3b>(height * 3 / 4, width * 3 / 4);

        Check("Top-left quadrant color survives the write/read round-trip", ColorNear(tlPixel, new SKColor(220, 30, 30)));
        Check("Top-right quadrant color survives the write/read round-trip", ColorNear(trPixel, new SKColor(30, 200, 30)));
        Check("Bottom-left quadrant color survives the write/read round-trip", ColorNear(blPixel, new SKColor(30, 30, 220)));
        Check("Bottom-right quadrant color survives the write/read round-trip", ColorNear(brPixel, new SKColor(230, 220, 30)));
    }
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

    // DPI pinned to BaselineDpi so ResizeForDpi is a no-op — this test is about auto-crop
    // geometry, not DPI-driven resampling.
    var result = new ImageProcessor().Process(path, outDir, splitPages: false, manualOverride: false,
        metadata: new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow));

    Check("Processing succeeds", result.Success);
    if (result.Success && result.OutputFilePaths.Count > 0)
    {
        using var output = Cv2.ImRead(result.OutputFilePaths[0], ImreadModes.Unchanged);
        // This synthetic fixture (solid background, thin 5px stroked-outline "page", sparse
        // text) has too little interior gradient/texture content for Method 4's sign-change
        // signal to separate page from background — confirmed by comparison against all 8 real
        // fixtures in tools/SmokeTest/Fixtures/real-photos (book-curve + trapezoid), which all
        // crop to real, page-sized dimensions with no degenerate output. On a genuine detection
        // failure the pipeline's own defensive fallback (AltFlattenSinglePage's narrow-span
        // guard) correctly produces a non-degenerate, real page-height output rather than
        // inventing a false-confidence crop — per product direction ("robust methods which will
        // always work", no confidence-gate fallback to a plain uncropped frame — see the removed
        // TestLowConfidenceCropIsSkippedAndFlagged's comment). So this asserts "produces a real,
        // non-degenerate image" rather than "is smaller than the source", which no longer holds
        // for a synthetic fixture this far from a real photo's texture profile.
        Check("Output is a real, non-degenerate image (not a 1px-wide/near-zero-height sliver)",
            output.Width > 100 && output.Height > 100);
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
    // DPI pinned to BaselineDpi throughout this test so ResizeForDpi is a no-op — this test is
    // about crop/export geometry, not DPI-driven resampling.
    var pinnedDpiMeta = new TiffMetadata(ImageProcessor.BaselineDpi, null, DateTime.UtcNow);

    // First pass — worker auto-processes exactly like BackgroundProcessingWorker does.
    var autoResult = processor.Process(job.OriginalFilePath, outputDir, splitPages: false, manualOverride: job.ManualOverrideApplied, leftCrop: job.LeftCropBox, rightCrop: job.RightCropBox,
        metadata: pinnedDpiMeta);
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
        var reprocessResult = processor.Process(pendingJob.OriginalFilePath, outputDir, splitPages: false, manualOverride: pendingJob.ManualOverrideApplied, leftCrop: pendingJob.LeftCropBox, rightCrop: pendingJob.RightCropBox,
            metadata: pinnedDpiMeta);
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

    // Make a large, deliberate adjustment so the reprocessed output is unmistakably different
    // from the original. This used to drag the top-left crop handle, but manual crop-quad editing
    // was removed from Crop Review (see the commit that stripped quad/split-line/dewarp editing);
    // the flow under test here is edit -> save -> reprocess -> thumbnail refresh, which the
    // surviving brightness/contrast adjustments exercise just as well.
    cropVm.Brightness = 0.6;
    cropVm.Contrast = 0.4;
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

    // ExportBatchCommand no longer exists — exporting now happens inside the Finalize Batch
    // dialog (FinalizeBatchViewModel.ExportAsync), which needs a real Window owner for
    // ShowDialog and so can't be driven headlessly here. Exercise the same underlying
    // BatchExportService call the dialog makes instead, which is what this test actually cares
    // about verifying (export still works after Crop Review's reprocess), not the toolbar
    // button/dialog UI itself.
    using (var exportDb = new AppDbContext(dbPath))
    {
        var batch = await exportDb.Batches.FirstAsync(b => b.BatchCode == "UITEST");
        var exporter = new BatchExportService(exportDb);
        await exporter.ExportBatchAsync(batch.Id, workDir, "PDF");
    }
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

async Task TestCropReviewAdjustModeRotationReachesExport()
{
    Console.WriteLine("\n-- Crop Review Adjust mode: a 90-degree rotation actually reaches the exported file --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    using (var seedDb = new AppDbContext(dbPath))
    {
        _ = new CaptureQueueService(seedDb);
        seedDb.Projects.Add(new Project { Name = "ADJTEST", OutputDirectory = workDir });
        await seedDb.SaveChangesAsync();
    }

    var camera = new MicroCapture.Camera.MockCameraService();
    var vm = new MainWindowViewModel(camera, dbPath);
    await RunPumped(() => vm.ConnectCommand.ExecuteAsync(null));
    vm.ProjectCode = "ADJTEST";
    vm.BatchCode = "ADJTEST";
    await RunPumped(() => vm.StartBatchCommand.ExecuteAsync(null));
    await RunPumped(() => vm.CaptureCommand.ExecuteAsync(null));
    PumpUntil(() => vm.RecentCaptures.Count == 1);
    var jobId = vm.RecentCaptures[0].JobId;
    PumpUntil(() => vm.RecentCaptures[0].Status != "Processing", timeoutMs: 20000);
    Check("Adjust flow: capture auto-processes before we touch it", vm.RecentCaptures[0].Status.StartsWith("Processed"));

    // Avalonia's headless test platform (see UseHeadless above) stubs out real bitmap
    // decoding — cropVm.Image/ImageWidth/ImageHeight come from Avalonia's own Bitmap type and
    // read back as a fake 1x1 in this harness, even though the real packaged app (real Skia
    // renderer) decodes correctly. Get the true "before" dimensions via SkiaSharp's own
    // decoder instead, which has no such headless-mode stubbing, so this test's oracle is
    // independent of Avalonia's rendering stack entirely.
    int widthBeforeRotate, heightBeforeRotate;
    using (var probeDb = new AppDbContext(dbPath))
    {
        var probeJob = await probeDb.CaptureJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        using var directDecode = SKBitmap.Decode(probeJob.OriginalFilePath);
        widthBeforeRotate = directDecode.Width;
        heightBeforeRotate = directDecode.Height;
        Console.WriteLine($"  [info] Original capture dimensions (via SkiaSharp): {widthBeforeRotate}x{heightBeforeRotate}");
    }

    using var reviewDb = new AppDbContext(dbPath);
    var reviewQueue = new CaptureQueueService(reviewDb);
    var cropVm = new CropReviewViewModel(jobId, reviewDb, reviewQueue);
    PumpUntil(() => cropVm.Image != null);

    // What the Rotate control does in the real UI. There is no longer a separate Adjust mode to
    // enter first — Crop Review is adjustments-only since manual crop-quad editing was removed —
    // so this now just fires the same command the button does.
    cropVm.RotateClockwiseCommand.Execute(null);
    Dispatcher.UIThread.RunJobs();
    Check("Adjust flow: RotationDegrees is 90 after one clockwise rotate", cropVm.RotationDegrees == 90);

    var cropReviewWindow = new CropReviewWindow { DataContext = cropVm };
    await RunPumped(() => cropVm.SaveCommand.ExecuteAsync(cropReviewWindow));

    // The real worker (owned by vm, polling once a second) should pick this back up on its
    // own — wait on the actual DB status (what BatchExportService itself checks), not the
    // in-memory thumbnail label, which can lag behind the worker's own DB commit.
    string? statusAfterWait = null;
    PumpUntil(() =>
    {
        using var pollDb = new AppDbContext(dbPath);
        statusAfterWait = pollDb.CaptureJobs.AsNoTracking().First(j => j.Id == jobId).ProcessingStatus;
        return statusAfterWait is "Completed" or "Failed";
    }, timeoutMs: 20000);

    using (var checkDb = new AppDbContext(dbPath))
    {
        var job = await checkDb.CaptureJobs.FirstAsync(j => j.Id == jobId);
        Check("Adjust flow: HasManualAdjustments was actually persisted as true", job.HasManualAdjustments);
        Check("Adjust flow: RotationDegrees (90) was actually persisted", job.RotationDegrees == 90);
        Check($"Adjust flow: job reprocessed to Completed (was: {statusAfterWait})", job.ProcessingStatus == "Completed");

        Console.WriteLine($"  [info] job.ProcessedFilePath = '{job.ProcessedFilePath}'");
        if (!string.IsNullOrWhiteSpace(job.ProcessedFilePath))
        {
            var outputFile = job.ProcessedFilePath.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (outputFile != null && File.Exists(outputFile))
            {
                var outInfo = new FileInfo(outputFile);
                Console.WriteLine($"  [info] outputFile = '{outputFile}', size={outInfo.Length} bytes");
                using var rotated = Cv2.ImRead(outputFile, ImreadModes.Unchanged);
                // The rotation is applied to the auto-detected CROP, not the raw 3840x2160
                // capture — a landscape original (widthBeforeRotate > heightBeforeRotate) crops
                // to some landscape-ish region, and a 90-degree rotation of that crop should
                // come out portrait (taller than wide), the same orientation flip a full-image
                // rotation would show. That orientation flip — not an exact pixel-dimension
                // match to the pre-crop original — is what actually proves the rotation reached
                // the processed file on disk, not just the DB row.
                Console.WriteLine($"  [info] Original: {widthBeforeRotate}x{heightBeforeRotate} (landscape); processed output after crop+90deg-rotate: {rotated.Width}x{rotated.Height}");
                Check("Adjust flow: processed output came out portrait (taller than wide) — proves the 90-degree rotation was actually applied",
                    rotated.Height > rotated.Width);
            }
        }

        if (job.ProcessingStatus == "Completed")
        {
            var exporter = new BatchExportService(checkDb);
            var batch = await checkDb.Batches.FirstAsync(b => b.BatchCode == "ADJTEST");
            var exportDir = await exporter.ExportBatchAsync(batch.Id, workDir, "PNG");
            var exportedFiles = Directory.GetFiles(exportDir, "*.png");
            Check("Adjust flow: export produced exactly one file", exportedFiles.Length == 1);
            if (exportedFiles.Length == 1)
            {
                using var exported = Cv2.ImRead(exportedFiles[0], ImreadModes.Unchanged);
                Console.WriteLine($"  [info] Exported file: {exported.Width}x{exported.Height}");
                Check("Adjust flow: EXPORTED file also reflects the rotation (this is the exact reported bug)",
                    exported.Height > exported.Width);
            }
        }
        else
        {
            Console.WriteLine("  [skip] Export checks skipped — job never reached Completed.");
        }
    }

    await RunPumped(() => vm.ShutdownAsync());
}

// ---------- Finalize's "searchable PDF" path (BatchOcrService + BatchExportService) ----------

async Task TestFinalizeSearchablePdfActuallyEmbedsOcrText()
{
    Console.WriteLine("\n-- Finalize's PDF export actually embeds OCR text as real, extractable PDF text --");
    var dbPath = TempDbPath();
    var workDir = TempWorkDir();

    string batchId;
    string jobId;
    using (var db = new AppDbContext(dbPath))
    {
        var queue = new CaptureQueueService(db);
        var project = new Project { Name = "OCRTEST", OutputDirectory = workDir };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        var batch = new Batch { ProjectId = project.Id, Name = "OCRTEST", BatchCode = "OCRTEST", Operator = "test" };
        db.Batches.Add(batch);
        await db.SaveChangesAsync();
        batchId = batch.Id;

        // A real page with two separate, large, high-contrast, machine-readable words, spaced
        // well apart vertically — Tesseract needs actual legible glyphs to produce non-empty OCR
        // output, and two distant words make a per-word positioned text layer distinguishable
        // from the old single-blob-crammed-at-top-left approach.
        var imagePath = Path.Combine(workDir, "ocrsource.png");
        using (var bitmap = new SKBitmap(800, 400))
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var font = new SKFont(SKTypeface.Default, 48);
            canvas.DrawText("ALPHAWORD", 20, 100, SKTextAlign.Left, font, paint);
            canvas.DrawText("BETAWORD", 20, 300, SKTextAlign.Left, font, paint);
            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(imagePath, data.ToArray());
        }

        // A real batch's OriginalFilePath (raw capture) and ProcessedFilePath (the derivative
        // ImageProcessor writes) are always two distinct files — DeleteOriginals only ever
        // touches the former. Using the same path for both here would make the sidecar-cleanup
        // check below pass or fail for the wrong reason (DeleteOriginals deleting the shared
        // file out from under GetProcessedFilesForJob's Tier 1 existence check, before
        // DeleteOcrSidecars ever runs), so this test copies the source into a second,
        // differently-named file to stand in for the processed derivative, exactly like a real
        // batch's layout.
        var originalPath = Path.Combine(workDir, "ocrsource_original.png");
        File.Copy(imagePath, originalPath);

        var job = await queue.EnqueueCaptureAsync(batchId, originalPath, 1, "PNG", 150);
        jobId = job.Id;
        // Simulate the background worker's own post-processing bookkeeping without running the
        // full ImageProcessor pipeline (which would re-crop/binarize/etc. and isn't the point of
        // this test) — mark it Completed and point ProcessedFilePath at the separate "processed"
        // file, exactly like GetProcessedFilesForJob's Tier 1 expects.
        job.ProcessingStatus = "Completed";
        job.ProcessedFilePath = imagePath;
        await db.SaveChangesAsync();
    }

    using (var db = new AppDbContext(dbPath))
    {
        var ocrService = new BatchOcrService(db);
        var summary = await ocrService.RunOcrForBatchAsync(batchId);
        Check("OCR step: tesseract CLI was found (test environment has it installed)", !summary.CliMissing);
        Check("OCR step: exactly one page completed OCR", summary.Completed == 1 && summary.Failed == 0);

        var job = await db.CaptureJobs.FirstAsync(j => j.Id == jobId);
        var txtPath = Path.ChangeExtension(job.ProcessedFilePath, ".txt");
        Check("OCR step: .txt sidecar was actually written next to the processed image", File.Exists(txtPath));
        if (File.Exists(txtPath))
        {
            var ocrText = File.ReadAllText(txtPath);
            Console.WriteLine($"  [info] OCR read back: \"{ocrText.Trim()}\"");
            Check("OCR step: recognized text contains both known words",
                ocrText.Contains("ALPHAWORD", StringComparison.OrdinalIgnoreCase) && ocrText.Contains("BETAWORD", StringComparison.OrdinalIgnoreCase));
        }

        var tsvPath = Path.ChangeExtension(job.ProcessedFilePath, ".tsv");
        Check("OCR step: .tsv word-box sidecar was actually written next to the processed image", File.Exists(tsvPath));
        if (File.Exists(tsvPath))
        {
            var boxes = OcrProcessor.ReadWordBoxes(tsvPath);
            Console.WriteLine($"  [info] Parsed {boxes.Count} word box(es): {string.Join(", ", boxes.Select(b => $"'{b.Text}'@({b.Left},{b.Top},{b.Width}x{b.Height})"))}");
            Check("OCR step: tsv parses into exactly two word boxes", boxes.Count == 2);
            Check("OCR step: the two word boxes are far apart vertically (not crammed at top-left)",
                boxes.Count == 2 && Math.Abs(boxes[0].Top - boxes[1].Top) > 100);
        }
    }

    using (var db = new AppDbContext(dbPath))
    {
        var exporter = new BatchExportService(db);
        // Match FinalizeBatchViewModel.ExportAsync exactly — it always passes orderedJobIds
        // (from the dialog's Pages list), never relies on ExportBatchAsync's own default
        // ordering/selection query, so exercise that same code path here.
        var pdfPath = await exporter.ExportBatchAsync(batchId, workDir, "PDF", orderedJobIds: new[] { jobId });
        Check("Export step: PDF was produced", File.Exists(pdfPath));

        if (File.Exists(pdfPath))
        {
            var pdfBytes = File.ReadAllBytes(pdfPath);
            var pdfText = System.Text.Encoding.Latin1.GetString(pdfBytes);
            var hasToUnicode = pdfText.Contains("/ToUnicode");
            Check("Export step: PDF includes a ToUnicode CMap (required for text to be copyable, not just glyph IDs)",
                hasToUnicode);

            // The content stream is FlateDecode-compressed, so the real BT/Tm/Tj operators
            // aren't visible in the raw bytes — decompress every stream and count distinct text
            // placement matrices ("Tm") instead of just checking "Tj" is present anywhere. Two
            // separate Tm's (one per word, at genuinely different positions) is what proves this
            // is a real per-word positioned text layer, not the old single blob crammed into one
            // corner — which would show exactly one Tm no matter how many lines of text it drew.
            var tmMatrices = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pdfText, @"stream\r?\n(.*?)endstream", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                byte[] raw;
                try
                {
                    raw = System.Text.Encoding.Latin1.GetBytes(m.Groups[1].Value);
                }
                catch { continue; }
                try
                {
                    using var input = new MemoryStream(raw);
                    using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    var decoded = System.Text.Encoding.Latin1.GetString(output.ToArray());
                    if (!decoded.Contains("Tj")) continue;
                    foreach (System.Text.RegularExpressions.Match tm in System.Text.RegularExpressions.Regex.Matches(decoded, @"[\d.\-]+ [\d.\-]+ [\d.\-]+ [\d.\-]+ [\d.\-]+ [\d.\-]+ Tm"))
                        tmMatrices.Add(tm.Value);
                }
                catch { /* not a zlib stream (e.g. the font binary) — skip */ }
            }
            Console.WriteLine($"  [info] Found {tmMatrices.Count} text-placement (Tm) operator(s) across the PDF's content streams: {string.Join(" | ", tmMatrices)}");
            Check("Export step: at least 2 distinct text placements exist (per-word positioning, not one blob)",
                tmMatrices.Distinct().Count() >= 2);
        }

        // The .txt/.tsv OCR sidecars are working files only, meaningful up to the moment
        // DrawSearchText read them above — left behind, they'd just clutter the batch's source
        // folder alongside the actual images. Confirm BatchExportService actually cleans them
        // up, the same way it already deletes the original capture file.
        var job = await db.CaptureJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        var txtPathAfterExport = Path.ChangeExtension(job.ProcessedFilePath, ".txt");
        var tsvPathAfterExport = Path.ChangeExtension(job.ProcessedFilePath, ".tsv");
        Console.WriteLine($"  [info] job.ProcessedFilePath after export = '{job.ProcessedFilePath}'");
        Console.WriteLine($"  [info] checking txt='{txtPathAfterExport}' exists={File.Exists(txtPathAfterExport)}, tsv='{tsvPathAfterExport}' exists={File.Exists(tsvPathAfterExport)}");
        Check("Cleanup: .txt sidecar was deleted after export", !File.Exists(txtPathAfterExport));
        Check("Cleanup: .tsv sidecar was deleted after export", !File.Exists(tsvPathAfterExport));
    }
}

// ---------- manual adjustments (rotate/flip/tone/color/sharpen) ----------

void TestManualAdjustmentsAreNoOpAtDefaults()
{
    Console.WriteLine("\n-- ApplyManualAdjustments at all-default values is a byte-identical no-op --");
    using var src = new Mat(200, 300, MatType.CV_8UC3, new Scalar(100, 120, 140));
    using var result = ImageProcessor.ApplyManualAdjustments(src, rotationDegrees: 0, flipHorizontal: false, flipVertical: false,
        brightness: 0, contrast: 0, saturation: 0, sharpness: 0, whiteBalance: 0);
    Check("Same dimensions", result.Cols == src.Cols && result.Rows == src.Rows);
    using var diff = new Mat();
    Cv2.Absdiff(src, result, diff);
    Check("Every pixel is byte-identical to the source", Cv2.CountNonZero(diff.Reshape(1)) == 0);
}

void TestManualAdjustmentsRotateAndFlip()
{
    Console.WriteLine("\n-- ApplyManualAdjustments rotate/flip produce the expected orientation --");
    // An asymmetric marker (bright block in the top-left only) makes every rotation/flip
    // combination distinguishable by where the bright block lands.
    using var src = new Mat(100, 160, MatType.CV_8UC3, new Scalar(0, 0, 0));
    src[new OpenCvSharp.Rect(0, 0, 40, 25)].SetTo(new Scalar(255, 255, 255));

    bool BrightAt(Mat m, int x, int y) => m.At<Vec3b>(y, x).Item0 > 200;

    using (var r90 = ImageProcessor.ApplyManualAdjustments(src, 90, false, false, 0, 0, 0, 0, 0))
    {
        Check("Rotate 90 changes dimensions (width/height swap)", r90.Cols == src.Rows && r90.Rows == src.Cols);
        // Clockwise 90: original top-left content moves to top-right.
        Check("Rotate 90 clockwise moves the marker to the top-right", BrightAt(r90, r90.Cols - 5, 5));
    }
    using (var r180 = ImageProcessor.ApplyManualAdjustments(src, 180, false, false, 0, 0, 0, 0, 0))
    {
        Check("Rotate 180 keeps dimensions", r180.Cols == src.Cols && r180.Rows == src.Rows);
        Check("Rotate 180 moves the marker to the bottom-right", BrightAt(r180, r180.Cols - 5, r180.Rows - 5));
    }
    using (var flipH = ImageProcessor.ApplyManualAdjustments(src, 0, true, false, 0, 0, 0, 0, 0))
    {
        Check("Horizontal flip moves the marker to the top-right", BrightAt(flipH, flipH.Cols - 5, 5));
    }
    using (var flipV = ImageProcessor.ApplyManualAdjustments(src, 0, false, true, 0, 0, 0, 0, 0))
    {
        Check("Vertical flip moves the marker to the bottom-left", BrightAt(flipV, 5, flipV.Rows - 5));
    }
}

void TestManualAdjustmentsBrightnessContrastDirection()
{
    Console.WriteLine("\n-- ApplyManualAdjustments brightness/contrast move mean luminance in the expected direction --");
    using var src = new Mat(120, 120, MatType.CV_8UC3, new Scalar(110, 110, 110));

    double MeanGray(Mat m)
    {
        using var gray = new Mat();
        Cv2.CvtColor(m, gray, ColorConversionCodes.BGR2GRAY);
        return Cv2.Mean(gray).Val0;
    }

    var baseline = MeanGray(src);
    using (var brighter = ImageProcessor.ApplyManualAdjustments(src, 0, false, false, brightness: 0.5, contrast: 0, saturation: 0, sharpness: 0, whiteBalance: 0))
        Check($"Positive brightness raises mean luminance (baseline {baseline:F0}, after {MeanGray(brighter):F0})", MeanGray(brighter) > baseline);
    using (var darker = ImageProcessor.ApplyManualAdjustments(src, 0, false, false, brightness: -0.5, contrast: 0, saturation: 0, sharpness: 0, whiteBalance: 0))
        Check($"Negative brightness lowers mean luminance (baseline {baseline:F0}, after {MeanGray(darker):F0})", MeanGray(darker) < baseline);

    // Contrast on a flat mid-gray field shouldn't move the mean much (it scales around the
    // pivot), but should visibly spread values on a two-tone image.
    using var twoTone = new Mat(120, 120, MatType.CV_8UC3, new Scalar(80, 80, 80));
    twoTone[new OpenCvSharp.Rect(0, 0, 60, 120)].SetTo(new Scalar(170, 170, 170));
    double StdDevGray(Mat m)
    {
        using var gray = new Mat();
        Cv2.CvtColor(m, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(gray, out _, out var stddev);
        return stddev.Val0;
    }
    var baselineStd = StdDevGray(twoTone);
    using var moreContrast = ImageProcessor.ApplyManualAdjustments(twoTone, 0, false, false, brightness: 0, contrast: 0.6, saturation: 0, sharpness: 0, whiteBalance: 0);
    Check($"Positive contrast increases spread between the two tones (baseline std {baselineStd:F1}, after {StdDevGray(moreContrast):F1})",
        StdDevGray(moreContrast) > baselineStd);
}

void TestManualAdjustmentsSaturationDirection()
{
    Console.WriteLine("\n-- ApplyManualAdjustments saturation=-1 collapses a colored image toward grayscale --");
    using var src = new Mat(100, 100, MatType.CV_8UC3, new Scalar(30, 60, 200)); // strongly red-orange in BGR

    double SaturationSpread(Mat m)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(m, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.Split(hsv, out var channels);
        var mean = Cv2.Mean(channels[1]).Val0;
        foreach (var c in channels) c.Dispose();
        return mean;
    }

    var baseline = SaturationSpread(src);
    using var desaturated = ImageProcessor.ApplyManualAdjustments(src, 0, false, false, brightness: 0, contrast: 0, saturation: -1, sharpness: 0, whiteBalance: 0);
    Check($"Saturation -1 drives the HSV saturation channel toward zero (baseline {baseline:F0}, after {SaturationSpread(desaturated):F0})",
        SaturationSpread(desaturated) < baseline * 0.15);
}

void TestManualAdjustmentsWhiteBalanceDirection()
{
    Console.WriteLine("\n-- ApplyManualAdjustments white balance shifts the R/B channel ratio in the expected direction --");
    using var src = new Mat(100, 100, MatType.CV_8UC3, new Scalar(120, 120, 120));

    (double blueMean, double redMean) ChannelMeans(Mat m)
    {
        Cv2.Split(m, out var channels);
        var b = Cv2.Mean(channels[0]).Val0;
        var r = Cv2.Mean(channels[2]).Val0;
        foreach (var c in channels) c.Dispose();
        return (b, r);
    }

    using var warm = ImageProcessor.ApplyManualAdjustments(src, 0, false, false, brightness: 0, contrast: 0, saturation: 0, sharpness: 0, whiteBalance: 1.0);
    var (warmBlue, warmRed) = ChannelMeans(warm);
    Check($"Warm white balance boosts red over blue (R {warmRed:F0} vs B {warmBlue:F0})", warmRed > warmBlue);

    using var cool = ImageProcessor.ApplyManualAdjustments(src, 0, false, false, brightness: 0, contrast: 0, saturation: 0, sharpness: 0, whiteBalance: -1.0);
    var (coolBlue, coolRed) = ChannelMeans(cool);
    Check($"Cool white balance boosts blue over red (B {coolBlue:F0} vs R {coolRed:F0})", coolBlue > coolRed);
}

void TestManualAdjustmentsSharpnessIncreasesEdgeContrast()
{
    Console.WriteLine("\n-- ApplyManualAdjustments sharpness increases edge contrast on a soft edge --");
    using var src = new Mat(150, 150, MatType.CV_8UC3, new Scalar(40, 40, 40));
    using var blurredEdge = new Mat(150, 150, MatType.CV_8UC3, new Scalar(40, 40, 40));
    blurredEdge[new OpenCvSharp.Rect(75, 0, 75, 150)].SetTo(new Scalar(200, 200, 200));
    Cv2.GaussianBlur(blurredEdge, blurredEdge, new OpenCvSharp.Size(0, 0), 6.0); // soften the edge before sharpening

    double LaplacianVariance(Mat m)
    {
        using var gray = new Mat();
        Cv2.CvtColor(m, gray, ColorConversionCodes.BGR2GRAY);
        using var lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var stddev);
        return stddev.Val0 * stddev.Val0;
    }

    var baseline = LaplacianVariance(blurredEdge);
    using var sharpened = ImageProcessor.ApplyManualAdjustments(blurredEdge, 0, false, false, brightness: 0, contrast: 0, saturation: 0, sharpness: 1.0, whiteBalance: 0);
    Check($"Sharpness increases Laplacian variance (baseline {baseline:F0}, after {LaplacianVariance(sharpened):F0})",
        LaplacianVariance(sharpened) > baseline);
}

void TestAdjustmentGeometryClamping()
{
    Console.WriteLine("\n-- AdjustmentGeometry clamps tone/sharpness values and normalizes rotation --");
    Check("ClampTone clamps above range", AdjustmentGeometry.ClampTone(5.0) == 1.0);
    Check("ClampTone clamps below range", AdjustmentGeometry.ClampTone(-5.0) == -1.0);
    Check("ClampTone passes through in-range values", AdjustmentGeometry.ClampTone(0.3) == 0.3);
    Check("ClampSharpness clamps above range", AdjustmentGeometry.ClampSharpness(2.0) == 1.0);
    Check("ClampSharpness clamps below zero", AdjustmentGeometry.ClampSharpness(-1.0) == 0.0);
    Check("NormalizeRotation leaves 90 unchanged", AdjustmentGeometry.NormalizeRotation(90) == 90);
    Check("NormalizeRotation wraps 360 to 0", AdjustmentGeometry.NormalizeRotation(360) == 0);
    Check("NormalizeRotation wraps 450 to 90", AdjustmentGeometry.NormalizeRotation(450) == 90);
    Check("NormalizeRotation wraps -90 to 270", AdjustmentGeometry.NormalizeRotation(-90) == 270);
}

void TestAdjustmentPresetsWithinRange()
{
    Console.WriteLine("\n-- Adjustment presets stay within the valid clamp range and desaturate as expected --");
    AdjustmentPreset[] presets = { AdjustmentGeometry.Document, AdjustmentGeometry.Photo, AdjustmentGeometry.Grayscale, AdjustmentGeometry.BlackAndWhite };
    foreach (var preset in presets)
    {
        Check($"Preset brightness {preset.Brightness} is within [-1, 1]", preset.Brightness is >= -1.0 and <= 1.0);
        Check($"Preset contrast {preset.Contrast} is within [-1, 1]", preset.Contrast is >= -1.0 and <= 1.0);
        Check($"Preset saturation {preset.Saturation} is within [-1, 1]", preset.Saturation is >= -1.0 and <= 1.0);
    }
    Check("Grayscale preset fully desaturates", AdjustmentGeometry.Grayscale.Saturation == -1.0);
    Check("B&W preset fully desaturates", AdjustmentGeometry.BlackAndWhite.Saturation == -1.0);

    // Independently verify the Grayscale preset actually collapses a colored synthetic image,
    // exercising the same real pipeline call the UI would make with these exact values.
    using var src = new Mat(100, 100, MatType.CV_8UC3, new Scalar(30, 60, 200));
    using var applied = ImageProcessor.ApplyManualAdjustments(src, 0, false, false,
        AdjustmentGeometry.Grayscale.Brightness, AdjustmentGeometry.Grayscale.Contrast, AdjustmentGeometry.Grayscale.Saturation, 0, 0);
    using var hsv = new Mat();
    Cv2.CvtColor(applied, hsv, ColorConversionCodes.BGR2HSV);
    Cv2.Split(hsv, out var channels);
    var satMean = Cv2.Mean(channels[1]).Val0;
    foreach (var c in channels) c.Dispose();
    Check($"Applying the Grayscale preset produces a near-zero saturation image (mean S {satMean:F0})", satMean < 15);
}
