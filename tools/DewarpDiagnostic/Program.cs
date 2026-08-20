// Diagnostic tool (not part of the shipped app): runs the real ImageProcessor pipeline
// against real-photo fixtures and reports what happened, for visually validating boundary
// detection, dewarp, deskew, and binarization against real captures instead of only synthetic
// SmokeTest images. Never contributes to any automated pass/fail — real photos can only be
// visually judged, not asserted against a synthetic ground truth. See
// tools/SmokeTest/Fixtures/real-photos/README.md.
//
// `process` is THE canonical command: it calls ImageProcessor.Process/ProcessFixedFrames, the
// exact same entry points MicroCapture.Processing/BackgroundProcessingWorker.cs invokes for a
// real batch — there is only one boundary/dewarp pipeline now (Method 4 side-edge detection +
// cubic-bow single-pass remap, ported from tools/phaseA-prototype/boundary_prototype.ipynb),
// not a choice between "the real one" and "an alternative," so this subcommand's output is
// exactly what a real capture would produce. `altboundary`/`altflatten` were previously separate
// subcommands exercising a genuinely different code path than `process` (a known, confirmed gap
// — validating them didn't validate what the shipped app actually ran); they're kept below only
// as finer-grained diagnostics INTO THE SAME pipeline `process` now runs (boundary trace only,
// or flatten only, without the finger/bleedthrough/enhance/binarize tail) — not a different
// pipeline anymore. `boundary`/`corners`/`rotfield`/`spread`/`points`/`mesh` are the OLD
// contour/text-line-blob detector's own diagnostics — that detector only still runs for the
// manual-crop-override path (Process's manualOverride branch), never for an automatic capture,
// so treat their output as manual-path-only, not representative of `process`'s automatic result.
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
    case "dewarp-lines":
        return RunDewarpLines(args);
    case "dewarp-model":
        return RunDewarpModel(args);
    case "spread":
        return RunSpread(args);
    case "boundary":
        return RunBoundary(args);
    case "altboundary":
        return RunAltBoundary(args);
    case "altflatten":
        return RunAltFlatten(args);
    case "singleflatten":
        return RunSingleFlatten(args);
    case "corners":
        return RunCorners(args);
    case "rotfield":
        return RunRotField(args);
    case "points":
        return RunPoints(args);
    case "mesh":
        return RunMesh(args);
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
    Console.WriteLine("  process <input-dir> <output-dir> [--binarize] [--force-single]");
    Console.WriteLine("  calibrate <calibration-images-dir>");
    Console.WriteLine("  dewarp-lines <cropped-page-image>");
    Console.WriteLine("  spread <image-or-dir>");
    Console.WriteLine("  boundary <image-or-dir>");
    Console.WriteLine("  altboundary <image-or-dir> [--out <out-dir>]");
    Console.WriteLine("  altflatten <image-or-dir> <out-dir>");
    Console.WriteLine("  singleflatten <image-or-dir> <out-dir>");
    Console.WriteLine("  corners <image-or-dir>");
    Console.WriteLine("  rotfield <image-or-dir>");
    Console.WriteLine("  points <image> [pointsPerEdge]");
    Console.WriteLine("  mesh <image-or-dir>");
    Console.WriteLine("  finger <image-or-dir> [--apply <out-dir>]");
    Console.WriteLine("  bleed <image-or-dir> [--apply <out-dir>]");
}

static int RunProcess(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var inputDir = args[1];
    var outputDir = args[2];
    var binarize = args.Contains("--binarize");
    // Dev-only escape hatch: disables the auto-split promotion entirely (see
    // ImageProcessor.Process's GutterConfidenceThreshold check) so a real spread is forced
    // through the single-page Method 4 flatten (AltFlattenSinglePage) instead of the two-page
    // split (AltFlattenSpread), without needing to hand-edit ImageProcessor to compare the two.
    var forceSingle = args.Contains("--force-single");
    // NOTE: dewarpEnabled only affects the manual-crop-override path now (TryApplyDewarp, the
    // legacy book-curve dewarp curve) — the automatic Method 4 path this tool actually exercises
    // folds curve-straightening into its own single-pass remap unconditionally, so --no-dewarp
    // has no effect on a normal (non-manual-override) `process` run. Kept for the manual-path
    // diagnostics below (dewarp-lines/dewarp-model) and for the rare manual-crop fixture.
    var noDewarp = args.Contains("--no-dewarp");

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
    if (forceSingle) processor.GutterConfidenceThreshold = double.MaxValue;
    Console.WriteLine($"Processing {images.Count} image(s) from {inputDir} (binarize={binarize})\n");

    foreach (var imagePath in images)
    {
        var name = Path.GetFileNameWithoutExtension(imagePath);
        Console.WriteLine($"=== {name} ===");

        var result = processor.Process(imagePath, outputDir, binarizeEnabled: binarize, dewarpEnabled: !noDewarp);

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

static int RunDewarpLines(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var path = args[1];
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }
    var bytes = File.ReadAllBytes(path);
    Console.WriteLine(ImageProcessor.DebugDewarpLines(bytes));
    return 0;
}

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
    Console.WriteLine(ImageProcessor.DebugDewarpModel(bytes));
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

static int RunAltBoundary(string[] args)
{
    if (args.Length < 2) { PrintUsage(); return 1; }
    var target = args[1];
    var outIdx = Array.IndexOf(args, "--out");
    var outDir = outIdx >= 0 && args.Length > outIdx + 1 ? args[outIdx + 1] : null;
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
        // Cv2.ImDecode (which the Alt* diagnostics use) doesn't handle TIFF — bridge through
        // the same helper the UI uses to display a TIFF ImageProcessor itself wrote.
        var bytes = ImageDecodeHelper.GetDisplayBytes(path) ?? throw new InvalidOperationException($"Could not decode {path}");
        Console.WriteLine(processor.DebugAltBoundaryDetection(bytes));

        if (outDir != null)
        {
            var overlay = processor.AltBoundaryOverlay(bytes);
            var outPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + "_altboundary.png");
            File.WriteAllBytes(outPath, overlay);
            Console.WriteLine($"  -> {outPath}");
        }
    }
    return 0;
}

static int RunAltFlatten(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var target = args[1];
    var outDir = args[2];
    Directory.CreateDirectory(outDir);

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
        try
        {
            var (leftPng, rightPng, report) = processor.DebugAltFlatten(bytes);
            Console.WriteLine(report);

            var baseName = Path.GetFileNameWithoutExtension(path);
            var leftPath = Path.Combine(outDir, baseName + "_altflat_left.png");
            var rightPath = Path.Combine(outDir, baseName + "_altflat_right.png");
            File.WriteAllBytes(leftPath, leftPng);
            File.WriteAllBytes(rightPath, rightPng);
            Console.WriteLine($"  -> {leftPath}");
            Console.WriteLine($"  -> {rightPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAILED: {ex.Message}");
        }
    }
    return 0;
}

static int RunSingleFlatten(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var target = args[1];
    var outDir = args[2];
    Directory.CreateDirectory(outDir);

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
        try
        {
            var png = processor.DebugAltFlattenSinglePage(bytes);
            var baseName = Path.GetFileNameWithoutExtension(path);
            var outPath = Path.Combine(outDir, baseName + "_singleflat.png");
            File.WriteAllBytes(outPath, png);
            Console.WriteLine($"  -> {outPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAILED: {ex}");
        }
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

static int RunMesh(string[] args)
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
        Console.WriteLine(processor.DebugLineMesh(bytes));
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
