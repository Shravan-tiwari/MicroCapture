// Diagnostic tool (not part of the shipped app): runs the real ImageProcessor pipeline
// against real-photo fixtures and reports what happened, for visually validating the Phase 1
// accuracy rework (boundary detection, trapezoid, book-curve dewarp, deskew, binarization)
// against real captures instead of only synthetic SmokeTest images. Never contributes to any
// automated pass/fail — real photos can only be visually judged, not asserted against a
// synthetic ground truth. See tools/SmokeTest/Fixtures/real-photos/README.md.
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
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  process <input-dir> <output-dir> [--binarize]");
    Console.WriteLine("  calibrate <calibration-images-dir>");
    Console.WriteLine("  dewarp-lines <cropped-page-image>");
}

static int RunProcess(string[] args)
{
    if (args.Length < 3) { PrintUsage(); return 1; }
    var inputDir = args[1];
    var outputDir = args[2];
    var binarize = args.Contains("--binarize");

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

        var result = processor.Process(imagePath, outputDir, binarizeEnabled: binarize, dewarpEnabled: true);

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
