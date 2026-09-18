// Diagnostic tool (not part of the shipped app): runs the real ImageProcessor pipeline
// against real-photo fixtures and reports what happened, for visually validating boundary
// detection, dewarp, deskew, and binarization against real captures instead of only synthetic
// SmokeTest images. Never contributes to any automated pass/fail — real photos can only be
// visually judged, not asserted against a synthetic ground truth. See
// tools/SmokeTest/Fixtures/real-photos/README.md.
//
// `process` is THE canonical command: it calls ImageProcessor.Process, the exact same entry
// point MicroCapture.Processing/BackgroundProcessingWorker.cs invokes for a real batch. Book
// curve correction (dewarpEnabled, the "Book Curve Correction" checkbox) calls page_dewarp.py
// (https://mzucker.github.io/2016/08/15/page-dewarping.html — vendored at
// MicroCapture.Processing/vendor/page_dewarp/page_dewarp.py with one deliberate change from
// upstream, see that file's header) as a subprocess — see
// MicroCapture.Processing/PythonDewarpRunner.cs — rather than a C# reimplementation of it; two
// earlier in-process approaches (a hand-ported "Method 4" side-edge detector, and before that a
// C# port of page_dewarp.py itself) were both replaced after direct comparison against the real
// script on real photos showed quality gaps. There is no separate C# auto-crop step before
// dewarp anymore either — page_dewarp.py's own text-line-based boundary detection is the only
// boundary detection an automatic capture gets when Book Curve Correction is on; a capture with
// dewarp off passes straight through untouched. There's also no pre-dewarp C# deskew step
// anymore — confirmed on real photos that page_dewarp.py's own optimizer solves camera
// rotation as part of its fit and needs no pre-leveled input, and that a C# pre-rotation could
// actively feed it a WORSE input on severely-tilted photos (its own text-line/Hough angle
// estimators are unreliable above ~10° rotation).
// `dewarp-model` below reports whether the subprocess produced output and what it warned about
// — page_dewarp.py's own fitted camera-pose/cubic-surface state lives and dies inside the
// subprocess, so there's no internal model left in C# to inspect the way the old ports had.
// `boundary`/`corners`/`rotfield`/`spread`/`points` are the contour/text-line-blob detector's
// own diagnostics for deskew — unaffected by this change. TryTrimGutterShadow,
// TryApplyLineMesh, and SharpenAfterDewarp (and this tool's own `mesh` subcommand) were removed
// entirely — the C# post-dewarp cleanup stages are gone; only page_dewarp.py's own output runs
// through FinishPageProcessing now.
//
// Usage:
//   dotnet run --project tools/DewarpDiagnostic -- process <input-dir> <output-dir> [--binarize]
//   dotnet run --project tools/DewarpDiagnostic -- calibrate <calibration-images-dir>

using MicroCapture.Processing;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

switch (args[0])
{
    case "process":
        return RunProcess(args);
    case "calibrate":
        return RunCalibrate(args);
    case "dewarp-model":
        return RunDewarpModel(args);
    case "spread":
        return RunSpread(args);
    case "boundary":
        return RunBoundary(args);
    case "corners":
        return RunCorners(args);
    case "rotfield":
        return RunRotField(args);
    case "points":
        return RunPoints(args);
    case "finger":
        return RunFinger(args);
    case "bleed":
        return RunBleed(args);
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  process <input-dir> <output-dir> [--binarize] [--no-dewarp] [--deskew]");
    Console.WriteLine("  calibrate <calibration-images-dir>");
    Console.WriteLine("  dewarp-model <cropped-page-image>");
    Console.WriteLine("  spread <image-or-dir>");
    Console.WriteLine("  boundary <image-or-dir>");
    Console.WriteLine("  corners <image-or-dir>");
    Console.WriteLine("  rotfield <image-or-dir>");
    Console.WriteLine("  points <image> [pointsPerEdge]");
    Console.WriteLine("  finger <image-or-dir> [--apply <out-dir>]");
    Console.WriteLine("  bleed <image-or-dir> [--apply <out-dir>]");
}

static int RunProcess(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var inputDir = args[1];
    var outputDir = args[2];
    var binarize = args.Contains("--binarize");
    // dewarpEnabled gates the entire book-curve dewarp step (TryApplyDewarp ->
    // PythonDewarpRunner.cs's page_dewarp.py subprocess call) — --no-dewarp turns it off,
    // matching the "Book Curve Correction" checkbox off in the real app.
    var noDewarp = args.Contains("--no-dewarp");
    // deskewEnabled matches the separate "Deskew" checkbox — only actually runs when dewarp is
    // off (see ProcessSinglePage/Batch.DeskewEnabled for why they don't stack), so pair this
    // with --no-dewarp to exercise it here.
    var deskew = args.Contains("--deskew");

    if (!Directory.Exists(inputDir))
    {
        Console.Error.WriteLine($"Input directory not found: {inputDir}");
        return 1;
    }
    Directory.CreateDirectory(outputDir);

    var images = Directory.GetFiles(inputDir, "*.*", SearchOption.TopDirectoryOnly)
        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f)
        .ToList();

    if (images.Count == 0)
    {
        Console.Error.WriteLine($"No JPG/TIFF images found in {inputDir}");
        return 1;
    }

    var processor = new ImageProcessor();
    Console.WriteLine($"Processing {images.Count} image(s) from {inputDir} (binarize={binarize})\n");

    foreach (var imagePath in images)
    {
        var name = Path.GetFileNameWithoutExtension(imagePath);
        Console.WriteLine($"=== {name} ===");

        var result = processor.Process(imagePath, outputDir, binarizeEnabled: binarize, dewarpEnabled: !noDewarp, deskewEnabled: deskew);

        Console.WriteLine($"  Success: {result.Success}");
        Console.WriteLine($"  CropConfidence: {result.CropConfidence:P1}");
        Console.WriteLine($"  WasCropped: {result.WasCropped}  WasDeskewed: {result.WasDeskewed}  WasBinarized: {result.WasBinarized}");
        if (result.WasDeskewed)
            Console.WriteLine($"  OriginalSkewDegrees: {result.OriginalSkewDegrees:F2}  AppliedCorrectionDegrees: {result.AppliedCorrectionDegrees:F2}");
        Console.WriteLine($"  QcVerdict: {result.QcVerdict}  BlurScore: {result.BlurScore:F1}  ExposureScore: {result.ExposureScore:F1}");
        foreach (var w in result.Warnings) Console.WriteLine($"  - {w}");
        foreach (var e in result.Errors) Console.WriteLine($"  ! {e}");

        // Write a PNG alongside the TIFF for easy viewing (Preview/any image viewer can open
        // it directly, unlike a plain OpenCV-written TIFF — see ImageDecodeHelper's own remarks).
        foreach (var outPath in result.OutputFilePaths)
        {
            var pngBytes = ImageDecodeHelper.GetDisplayBytes(outPath);
            if (pngBytes == null) continue;
            var pngPath = Path.ChangeExtension(outPath, ".png");
            File.WriteAllBytes(pngPath, pngBytes);
            Console.WriteLine($"  -> {pngPath}");
        }

        Console.WriteLine();
    }

    return 0;
}

static int RunCalibrate(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var dir = args[1];
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"Calibration image directory not found: {dir}");
        return 1;
    }

    var images = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f)
        .ToList();

    Console.WriteLine($"Found {images.Count} calibration image(s)");
    var outcome = LensCalibrationService.Calibrate(images);

    Console.WriteLine($"Success: {outcome.Success}");
    Console.WriteLine($"ImagesUsed: {outcome.ImagesUsed} / {outcome.ImagesTotal}");
    Console.WriteLine($"ReprojectionErrorPx: {outcome.ReprojectionErrorPx:F4}");
    if (outcome.Calibration is { } c)
    {
        Console.WriteLine($"Fx={c.Fx:F2} Fy={c.Fy:F2} Cx={c.Cx:F2} Cy={c.Cy:F2}");
        Console.WriteLine($"DistCoeffs=[{string.Join(", ", c.DistCoeffs.Select(d => d.ToString("F6")))}]");
        Console.WriteLine($"CalibratedSize={c.CalibratedWidth}x{c.CalibratedHeight}");
    }
    Console.WriteLine("--- Warnings ---");
    foreach (var w in outcome.Warnings) Console.WriteLine(w);

    return outcome.Success ? 0 : 1;
}

/// <summary>Reports whether the real page_dewarp.py subprocess (PythonDewarpRunner.cs)
/// produced a flattened result for a cropped page image, and every warning the run generated,
/// without running the rest of the pipeline. Supersedes two earlier generations of this
/// subcommand — a Method-4-era `dewarp-lines`, and a later C#-ported-model `dewarp-model` — both
/// of which pointed at detectors that no longer exist.</summary>
static int RunDewarpModel(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }
    var bytes = File.ReadAllBytes(path);
    Console.WriteLine(ImageProcessor.DebugPageDewarpModel(bytes));
    return 0;
}

static int RunSpread(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var target = args[1];
    IEnumerable<string> files = Directory.Exists(target)
        ? Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
        : File.Exists(target) ? new[] { target } : Array.Empty<string>();
    var fileList = files.ToList();

    if (fileList.Count == 0)
    {
        Console.Error.WriteLine($"No image(s) found at {target}");
        return 1;
    }

    foreach (var path in fileList)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        Console.WriteLine(ImageProcessor.DebugSpreadDetection(File.ReadAllBytes(path)));
    }
    return 0;
}

static int RunBoundary(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var target = args[1];
    bool IsImage(string f) => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
    IEnumerable<string> files = Directory.Exists(target)
        ? Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(f => f)
        : File.Exists(target) ? new[] { target } : Array.Empty<string>();
    var fileList = files.ToList();

    if (fileList.Count == 0)
    {
        Console.Error.WriteLine($"No image(s) found at {target}");
        return 1;
    }

    var processor = new ImageProcessor();
    foreach (var path in fileList)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        // Cv2.ImDecode (which DebugBoundaryDetection uses) doesn't handle TIFF — bridge
        // through the same helper the UI uses to display a TIFF ImageProcessor itself wrote.
        var bytes = ImageDecodeHelper.GetDisplayBytes(path) ?? throw new InvalidOperationException($"Could not decode {path}");
        Console.WriteLine(processor.DebugBoundaryDetection(bytes));
    }
    return 0;
}

static int RunCorners(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var target = args[1];
    bool IsImage(string f) => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
    IEnumerable<string> files = Directory.Exists(target)
        ? Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(f => f)
        : File.Exists(target) ? new[] { target } : Array.Empty<string>();
    var fileList = files.ToList();

    if (fileList.Count == 0)
    {
        Console.Error.WriteLine($"No image(s) found at {target}");
        return 1;
    }

    var processor = new ImageProcessor();
    foreach (var path in fileList)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        var bytes = ImageDecodeHelper.GetDisplayBytes(path) ?? throw new InvalidOperationException($"Could not decode {path}");
        Console.WriteLine(processor.DebugCornerRefinement(bytes));
    }
    return 0;
}

static int RunRotField(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var target = args[1];
    bool IsImage(string f) => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
    IEnumerable<string> files = Directory.Exists(target)
        ? Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(f => f)
        : File.Exists(target) ? new[] { target } : Array.Empty<string>();
    var fileList = files.ToList();

    if (fileList.Count == 0)
    {
        Console.Error.WriteLine($"No image(s) found at {target}");
        return 1;
    }

    var processor = new ImageProcessor();
    foreach (var path in fileList)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        var bytes = ImageDecodeHelper.GetDisplayBytes(path) ?? throw new InvalidOperationException($"Could not decode {path}");
        Console.WriteLine(processor.DebugDeskewRotationField(bytes));
    }
    return 0;
}

static int RunFinger(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var target = args[1];
    var applyIdx = Array.IndexOf(args, "--apply");
    var outDir = applyIdx >= 0 && args.Length > applyIdx + 1 ? args[applyIdx + 1] : null;
    if (outDir != null) Directory.CreateDirectory(outDir);

    bool IsImage(string f) => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
    IEnumerable<string> files = Directory.Exists(target)
        ? Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(f => f)
        : File.Exists(target) ? new[] { target } : Array.Empty<string>();
    var fileList = files.ToList();

    if (fileList.Count == 0)
    {
        Console.Error.WriteLine($"No image(s) found at {target}");
        return 1;
    }

    var processor = new ImageProcessor();
    foreach (var path in fileList)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        var bytes = ImageDecodeHelper.GetDisplayBytes(path) ?? throw new InvalidOperationException($"Could not decode {path}");
        Console.WriteLine(processor.DebugFingerRemoval(bytes));

        if (outDir != null)
        {
            var cleaned = processor.RemoveFingersFromBytes(bytes);
            if (cleaned != null)
            {
                var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + "_defingered.png");
                File.WriteAllBytes(outPath, cleaned);
                Console.WriteLine($"  -> {outPath}");
            }
            else
            {
                Console.WriteLine("  -> no change (nothing qualified)");
            }
        }
    }
    return 0;
}

static int RunBleed(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var target = args[1];
    var applyIdx = Array.IndexOf(args, "--apply");
    var outDir = applyIdx >= 0 && args.Length > applyIdx + 1 ? args[applyIdx + 1] : null;
    if (outDir != null) Directory.CreateDirectory(outDir);

    bool IsImage(string f) => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase);
    IEnumerable<string> files = Directory.Exists(target)
        ? Directory.GetFiles(target, "*.*", SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(f => f)
        : File.Exists(target) ? new[] { target } : Array.Empty<string>();
    var fileList = files.ToList();

    if (fileList.Count == 0)
    {
        Console.Error.WriteLine($"No image(s) found at {target}");
        return 1;
    }

    var processor = new ImageProcessor();
    foreach (var path in fileList)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        var bytes = ImageDecodeHelper.GetDisplayBytes(path) ?? throw new InvalidOperationException($"Could not decode {path}");
        Console.WriteLine(processor.DebugBleedthrough(bytes));

        if (outDir != null)
        {
            var cleaned = processor.ApplyBleedthroughSuppressionFromBytes(bytes);
            var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + "_debled.png");
            File.WriteAllBytes(outPath, cleaned);
            Console.WriteLine($"  -> {outPath}");
        }
    }
    return 0;
}

static int RunPoints(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }
    var pointsPerEdge = args.Length >= 3 && int.TryParse(args[2], out var n) ? n : 16;

    var processor = new ImageProcessor();
    var bytes = ImageDecodeHelper.GetDisplayBytes(path) ?? throw new InvalidOperationException($"Could not decode {path}");
    Console.WriteLine(processor.DebugBoundaryPoints(bytes, pointsPerEdge));
    return 0;
}
