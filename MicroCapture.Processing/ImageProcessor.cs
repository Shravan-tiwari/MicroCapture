using System;
using System.Globalization;
using System.IO;
using BitMiracle.LibTiff.Classic;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>Per-page metadata written into every processed TIFF's own tags — DPI (resolution),
/// Author (the batch's operator), and the actual capture timestamp — so the file's Properties
/// aren't blank or defaulted to a meaningless 96 DPI.</summary>
public readonly record struct TiffMetadata(int Dpi, string? Author, DateTime TimestampUtc)
{
    public static TiffMetadata Default => new(300, null, DateTime.UtcNow);
}

/// <summary>A single crop-shape corner, in an image's own pixel coordinates. OpenCvSharp-free
/// so it can safely cross into the UI project (mirrors the existing DTO pattern used by
/// <see cref="DocumentBoundary"/> and <see cref="LiveFrameCheck"/>).</summary>
public readonly record struct CropPoint(double X, double Y);

/// <summary>One operator-calibrated fixed-frame rectangle, in the pixel space of the image it
/// was calibrated against (see Batch.FixedFrameImageWidth/Height). Unlike <see cref="CropPoint"/>
/// quads, fixed frames are always axis-aligned — no perspective correction, since they exist
/// for a stationary, straight-down copy-stand shot.</summary>
public readonly record struct FixedFrameRect(double X, double Y, double Width, double Height);

/// <summary>Document boundary detected in a still image, in that image's own pixel coordinates.
/// <see cref="Quad"/> is populated when the detected contour approximated a clean
/// quadrilateral (ordered top-left, top-right, bottom-right, bottom-left) — the UI should
/// prefer it over the axis-aligned <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/
/// <see cref="Height"/> rect when present, since it captures perspective skew the rect can't.</summary>
public readonly record struct DocumentBoundary(int X, int Y, int Width, int Height, double Confidence, CropPoint[]? Quad = null);

/// <summary>Result of a single live-view frame check: whether a document-sized boundary is
/// present, where it is, how sharp (in-focus) that region is, and a small content signature
/// of the boundary region (a 24x24 grayscale thumbnail's raw bytes) so the caller can tell a
/// genuinely new page apart from the same page still sitting in a fixed copy-stand position —
/// position alone isn't a reliable "did the page change" signal when a physical page guide
/// places every page in nearly the same spot.</summary>
public readonly record struct LiveFrameCheck(bool Detected, int X, int Y, int Width, int Height, int ImageWidth, int ImageHeight, double Sharpness, byte[]? ContentSignature)
{
    public static readonly LiveFrameCheck None = default;
}

/// <summary>
/// Core image processing pipeline: auto-crop, deskew, perspective correction,
/// enhancement, and QC scoring. All operations preserve the original file.
/// </summary>
public class ImageProcessor
{
    // --- Configuration ---
    public double CropConfidenceThreshold { get; set; } = 0.5;
    // Below this, detection is unreliable enough that Crop Review shouldn't pre-fill a
    // suggestion at all — full-frame/manual is the safer default. Between this and
    // CropConfidenceThreshold, a suggestion is still shown, but flagged as lower-confidence
    // rather than presented with the same certainty as a high-confidence detection.
    public double MediumConfidenceThreshold { get; set; } = 0.3;
    public double MaxDeskewDegrees { get; set; } = 5.0;
    public int CropPadding { get; set; } = 10;
    public double BlurThreshold { get; set; } = 100.0;
    public double GutterConfidenceThreshold { get; set; } = 0.08;
    // Unsharp-mask strength applied as the pipeline's last step, after enhancement — counters
    // the softening every warp/rotate resample introduces. Deliberately mild: high enough to
    // read as crisper edges/text, low enough to avoid visible halos on a document scan.
    public double SharpenAmount { get; set; } = 0.6;
    public double SharpenSigma { get; set; } = 2.0;

    /// <summary>Fast, non-mutating boundary check for live-view auto-capture gating.</summary>
    public static bool IsDocumentDetected(byte[] encodedImage) => CheckLiveFrame(encodedImage).Detected;

    /// <summary>Non-mutating per-frame check used to drive assisted auto-capture guidance:
    /// reports whether a document-sized boundary is present, its bounding box, and a
    /// sharpness (focus) score so the caller can require a stable, in-focus frame before
    /// prompting the operator to capture.</summary>
    public static LiveFrameCheck CheckLiveFrame(byte[] encodedImage)
    {
        try
        {
            using var image = Cv2.ImDecode(encodedImage, ImreadModes.Grayscale);
            if (image.Empty()) return LiveFrameCheck.None;
            using var blurred = new Mat();
            using var edges = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            using var joined = new Mat();
            Cv2.GaussianBlur(image, blurred, new Size(5, 5), 0);
            Cv2.Canny(blurred, edges, 50, 200);
            Cv2.Dilate(edges, joined, kernel, iterations: 2);
            Cv2.FindContours(joined, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) return LiveFrameCheck.None;

            var imageArea = image.Width * image.Height;
            var best = contours.OrderByDescending(contour => Cv2.ContourArea(contour)).First();
            if (Cv2.ContourArea(best) / imageArea < 0.2) return LiveFrameCheck.None;

            var rect = Cv2.BoundingRect(best);

            using var laplacian = new Mat();
            Cv2.Laplacian(image, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var stddev);
            var sharpness = stddev.Val0 * stddev.Val0;

            byte[]? signature = null;
            try
            {
                var safeX = Math.Clamp(rect.X, 0, image.Width - 1);
                var safeY = Math.Clamp(rect.Y, 0, image.Height - 1);
                var safeRect = new Rect(safeX, safeY, Math.Clamp(rect.Width, 1, image.Width - safeX), Math.Clamp(rect.Height, 1, image.Height - safeY));
                using var region = new Mat(image, safeRect);
                using var thumb = new Mat();
                Cv2.Resize(region, thumb, new Size(24, 24), 0, 0, InterpolationFlags.Area);
                thumb.GetArray(out byte[] pixels);
                signature = pixels;
            }
            catch
            {
                // The signature is only used for an extra "did the page actually change" check —
                // detection and focus results above remain fully valid without it.
            }

            return new LiveFrameCheck(true, rect.X, rect.Y, rect.Width, rect.Height, image.Width, image.Height, sharpness, signature);
        }
        catch
        {
            // Live-view analysis is advisory. A decoding/native failure must never
            // interrupt the stream or turn an operator capture into a crash.
            return LiveFrameCheck.None;
        }
    }

    /// <summary>
    /// Run the full processing pipeline on a captured image.
    /// Original file is never modified. A processed derivative is created.
    /// </summary>
    public ProcessingResult Process(string inputPath, string outputDirectory, bool splitPages = false, bool manualOverride = false, string? leftCrop = null, string? rightCrop = null, TiffMetadata? metadata = null)
    {
        var result = new ProcessingResult { OriginalFilePath = inputPath };
        var meta = metadata ?? TiffMetadata.Default;

        if (!File.Exists(inputPath))
        {
            result.Success = false;
            result.Errors.Add($"Input file not found: {inputPath}");
            return result;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            using var src = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (src.Empty())
            {
                result.Success = false;
                result.Errors.Add("Failed to decode image.");
                return result;
            }

            if (splitPages)
            {
                // Split logic. Each side is parsed to its 4 corners; a plain saved strip
                // (today's UI) is an axis-aligned quad and takes WarpQuad's cheap-crop path,
                // while a future per-half quad edit would automatically get a real warp —
                // one shared implementation, no special-casing here.
                Point2f[] leftCorners, rightCorners;
                if (manualOverride && !string.IsNullOrEmpty(leftCrop) && !string.IsNullOrEmpty(rightCrop))
                {
                    leftCorners = ParseCropCorners(leftCrop, src.Width, src.Height);
                    rightCorners = ParseCropCorners(rightCrop, src.Width, src.Height);
                }
                else
                {
                    // Best-effort automatic gutter detection; falls back to an even
                    // 50/50 split when no confident spine shadow is found.
                    var gutter = DetectGutter(src);
                    var splitX = gutter.Confidence >= GutterConfidenceThreshold
                        ? (int)Math.Round(src.Width * gutter.Fraction)
                        : src.Width / 2;
                    splitX = Math.Clamp(splitX, 1, src.Width - 1);
                    leftCorners = RectCorners(0, 0, splitX, src.Height);
                    rightCorners = RectCorners(splitX, 0, src.Width - splitX, src.Height);
                }

                // Process left
                using var leftMat = WarpQuad(src, leftCorners);
                var leftResult = ProcessSinglePage(leftMat, result, manualOverride);
                var outLeft = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_1_left.tif");
                WriteTiff(outLeft, leftResult, meta);
                result.OutputFilePaths.Add(outLeft);
                leftResult.Dispose();

                // Process right
                using var rightMat = WarpQuad(src, rightCorners);
                var rightResult = ProcessSinglePage(rightMat, result, manualOverride);
                var outRight = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_2_right.tif");
                WriteTiff(outRight, rightResult, meta);
                result.OutputFilePaths.Add(outRight);
                rightResult.Dispose();

                result.Success = true;
            }
            else
            {
                // Single page logic. Only a manual override needs to apply a crop shape here —
                // the automatic (non-override) case passes the untouched source straight
                // through, since ProcessSinglePage's own TryAutoCrop will detect and warp it
                // as needed. Pre-warping the full frame first would be pure wasted work.
                Mat? manualCrop = null;
                if (manualOverride && !string.IsNullOrEmpty(leftCrop))
                    manualCrop = WarpQuad(src, ParseCropCorners(leftCrop, src.Width, src.Height));

                var processed = ProcessSinglePage(manualCrop ?? src, result, manualOverride);
                manualCrop?.Dispose();

                var outName = Path.GetFileNameWithoutExtension(inputPath) + "_processed.tif";
                var outPath = Path.Combine(outputDirectory, outName);
                WriteTiff(outPath, processed, meta);
                result.OutputFilePaths.Add(outPath);
                result.Success = true;
                processed.Dispose();
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"OpenCV processing failed: {ex.Message}. Using fallback processor.");
            try
            {
                using var inputStream = File.OpenRead(inputPath);
                using var originalBitmap = SkiaSharp.SKBitmap.Decode(inputStream);
                if (originalBitmap != null)
                {
                    SkiaSharp.SKBitmap outputBitmap = originalBitmap;
                    if (manualOverride && !string.IsNullOrEmpty(leftCrop))
                    {
                        // OpenCV itself is unavailable on this path, so a true perspective
                        // warp isn't possible here — degrade to the crop shape's bounding
                        // rect rather than losing the crop entirely.
                        var corners = ParseCropCorners(leftCrop, originalBitmap.Width, originalBitmap.Height);
                        var rect = BoundingRectOfCorners(corners, originalBitmap.Width, originalBitmap.Height);
                        var skRect = new SkiaSharp.SKRectI(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
                        var cropped = new SkiaSharp.SKBitmap(rect.Width, rect.Height);
                        using (var canvas = new SkiaSharp.SKCanvas(cropped))
                        {
                            canvas.DrawBitmap(originalBitmap, skRect, new SkiaSharp.SKRect(0, 0, rect.Width, rect.Height));
                        }
                        outputBitmap = cropped;
                    }
                    var outName = Path.GetFileNameWithoutExtension(inputPath) + "_processed.jpg";
                    var outPath = Path.Combine(outputDirectory, outName);
                    using var image = SkiaSharp.SKImage.FromBitmap(outputBitmap);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 95);
                    using var outStream = File.Create(outPath);
                    data.SaveTo(outStream);
                    result.OutputFilePaths.Add(outPath);
                    result.Success = true;
                    result.QcVerdict = "PASS";
                }
            }
            catch (Exception fallbackEx)
            {
                result.Success = false;
                result.Errors.Add($"Fallback crop failed: {fallbackEx.Message}");
            }
        }

        return result;
    }

    /// <summary>Crops one captured frame into N independent output files using a batch's
    /// pre-calibrated fixed rectangles, instead of per-shot contour detection. No confidence
    /// gating and no perspective warp — every defined rectangle is always cropped and saved
    /// as-is (aside from deskew/enhancement), since these exist only for a stationary,
    /// straight-down copy-stand shot where the frame position never needs to be guessed.</summary>
    public ProcessingResult ProcessFixedFrames(string inputPath, string outputDirectory, string fixedFramesSpec, TiffMetadata? metadata = null)
    {
        var result = new ProcessingResult { OriginalFilePath = inputPath };
        var meta = metadata ?? TiffMetadata.Default;

        if (!File.Exists(inputPath))
        {
            result.Success = false;
            result.Errors.Add($"Input file not found: {inputPath}");
            return result;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            using var src = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (src.Empty())
            {
                result.Success = false;
                result.Errors.Add("Failed to decode image.");
                return result;
            }

            var frames = ParseFixedFrames(fixedFramesSpec);
            if (frames.Length == 0)
            {
                result.Success = false;
                result.Errors.Add("Batch has no calibrated fixed frames.");
                return result;
            }

            var padWidth = Math.Max(2, frames.Length.ToString(CultureInfo.InvariantCulture).Length);
            for (var i = 0; i < frames.Length; i++)
            {
                var rect = ClampRectToBounds(new Rect(
                    (int)Math.Round(frames[i].X), (int)Math.Round(frames[i].Y),
                    (int)Math.Round(frames[i].Width), (int)Math.Round(frames[i].Height)), src.Cols, src.Rows);

                using var cropped = src[rect].Clone();
                var frameResult = new ProcessingResult { OriginalFilePath = inputPath };
                using var processed = ProcessSinglePage(cropped, frameResult, skipAutoCrop: true);

                var outName = $"{Path.GetFileNameWithoutExtension(inputPath)}_frame{(i + 1).ToString("D" + padWidth, CultureInfo.InvariantCulture)}.tif";
                var outPath = Path.Combine(outputDirectory, outName);
                WriteTiff(outPath, processed, meta);
                result.OutputFilePaths.Add(outPath);

                result.Warnings.AddRange(frameResult.Warnings.Select(w => $"Frame {i + 1}: {w}"));
                result.QcVerdict = CombineVerdict(result.QcVerdict, frameResult.QcVerdict);
                result.BlurScore = frameResult.BlurScore;
                result.ExposureScore = frameResult.ExposureScore;
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Warnings.Add($"Fixed-frame processing failed: {ex.Message}");
            result.Errors.Add(ex.Message);
        }

        return result;
    }

    /// <summary>Writes a processed page as TIFF, tagging it with the batch's chosen DPI so the
    /// file's own resolution metadata is correct instead of absent (Explorer/Photoshop default
    /// an untagged TIFF to 96 DPI, which has nothing to do with what was actually captured).
    /// This only changes the resolution tag — it does not resample or add pixel detail.</summary>
    private static void WriteTiff(string path, Mat mat, TiffMetadata metadata)
    {
        Cv2.ImWrite(path, mat,
            new ImageEncodingParam(ImwriteFlags.TiffResUnit, 2), // 2 = RESUNIT_INCH (libtiff)
            new ImageEncodingParam(ImwriteFlags.TiffXDpi, metadata.Dpi),
            new ImageEncodingParam(ImwriteFlags.TiffYDpi, metadata.Dpi));

        // OpenCV's own TIFF writer has no support for Artist/Software/DateTime tags — reopen
        // the file it just wrote (pixel data untouched) purely to add them, the same operation
        // libtiff's own "tiffset" CLI tool performs. Best-effort: a tag-write failure must
        // never invalidate an image that was already written successfully.
        try
        {
            using var tiff = Tiff.Open(path, "r+");
            if (tiff == null) return;
            tiff.SetField(TiffTag.SOFTWARE, "MicroCapture");
            tiff.SetField(TiffTag.DATETIME, metadata.TimestampUtc.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(metadata.Author))
                tiff.SetField(TiffTag.ARTIST, metadata.Author);
            tiff.RewriteDirectory();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WriteTiff] Could not write metadata tags for '{path}': {ex}");
        }
    }

    /// <summary>UI-facing, OpenCvSharp-free equivalent of <see cref="ParseCropCorners"/> — same
    /// parsing rules (legacy rect or new quad format, full-frame fallback), used by Crop Review
    /// to restore a previously saved crop shape so there's exactly one implementation of what a
    /// saved crop string means.</summary>
    public static CropPoint[] ParseCropShape(string cropStr, int maxW, int maxH) =>
        ParseCropCorners(cropStr, maxW, maxH).Select(p => new CropPoint(p.X, p.Y)).ToArray();

    /// <summary>Parses a batch's saved fixed-frame spec — "X,Y,W,H" rectangles joined by ';'
    /// (see <see cref="FormatFixedFrames"/>) — back into individual rects. Malformed entries are
    /// skipped rather than aborting the whole batch's frames.</summary>
    public static FixedFrameRect[] ParseFixedFrames(string spec)
    {
        var frames = new List<FixedFrameRect>();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var n = part.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (n.Length == 4)
                    frames.Add(new FixedFrameRect(n[0], n[1], n[2], n[3]));
            }
            catch { /* skip malformed entry */ }
        }
        return frames.ToArray();
    }

    /// <summary>Inverse of <see cref="ParseFixedFrames"/> — the format <see cref="FrameCalibrationViewModel"/>
    /// (MicroCapture.UI) saves onto Batch.FixedFrames.</summary>
    public static string FormatFixedFrames(IEnumerable<FixedFrameRect> frames) =>
        string.Join(";", frames.Select(f => string.Join(",",
            f.X.ToString("F1", CultureInfo.InvariantCulture), f.Y.ToString("F1", CultureInfo.InvariantCulture),
            f.Width.ToString("F1", CultureInfo.InvariantCulture), f.Height.ToString("F1", CultureInfo.InvariantCulture))));

    /// <summary>Parses a saved crop shape into its 4 corners (top-left, top-right,
    /// bottom-right, bottom-left). Accepts either the legacy 4-number "x,y,w,h" rect format
    /// (corners synthesized from it) or the newer 8-number "x1,y1,x2,y2,x3,y3,x4,y4" quad
    /// format written directly. Falls back to the full frame on any parse failure — the same
    /// safe default the old rect-only parser used.</summary>
    private static Point2f[] ParseCropCorners(string cropStr, int maxW, int maxH)
    {
        try
        {
            var parts = cropStr.Split(',').Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length == 8)
            {
                var corners = new Point2f[4];
                for (var i = 0; i < 4; i++)
                    corners[i] = new Point2f((float)parts[i * 2], (float)parts[i * 2 + 1]);
                return corners;
            }
            if (parts.Length == 4)
            {
                int x = Math.Max(0, (int)parts[0]);
                int y = Math.Max(0, (int)parts[1]);
                int w = Math.Min(maxW - x, (int)parts[2]);
                int h = Math.Min(maxH - y, (int)parts[3]);
                return RectCorners(x, y, w, h);
            }
        }
        catch { /* fall through to the full-frame default below */ }

        return RectCorners(0, 0, maxW, maxH);
    }

    private static Point2f[] RectCorners(int x, int y, int w, int h) =>
        new[] { new Point2f(x, y), new Point2f(x + w, y), new Point2f(x + w, y + h), new Point2f(x, y + h) };

    /// <summary>Axis-aligned bounding rect of arbitrary corners, clamped to the image bounds.
    /// Used only by the emergency SkiaSharp fallback path (when OpenCV itself is unavailable),
    /// which can't perform a perspective warp and must degrade to a plain rectangular crop.</summary>
    private static Rect BoundingRectOfCorners(Point2f[] corners, int maxW, int maxH)
    {
        var minX = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X)));
        var minY = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y)));
        var maxX = Math.Min(maxW, (int)Math.Ceiling(corners.Max(c => c.X)));
        var maxY = Math.Min(maxH, (int)Math.Ceiling(corners.Max(c => c.Y)));
        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private Mat ProcessSinglePage(Mat input, ProcessingResult result, bool skipAutoCrop)
    {
        var working = input.Clone();

        if (!skipAutoCrop)
            working = TryAutoCrop(working, result);

        working = TryDeskew(working, result);
        working = ApplyEnhancement(working);
        working = Sharpen(working);
        RunQualityChecks(working, result);

        return working;
    }

    // ───────────── AUTO-CROP ─────────────

    private readonly record struct BoundaryDetection(bool Found, double Confidence, Rect PaddedRect, Point2f[]? Quad);

    /// <summary>Shared edge-detection step behind every contour-based detector in this class
    /// (single-document auto-crop, the UI's boundary lookup, and two-page split detection) —
    /// defined once so all of them see the same document edges.
    ///
    /// Two passes, not one: illumination normalization (dividing by a heavily-blurred copy of
    /// the image, to flatten gradual shadows before edge detection) and a genuinely
    /// low-contrast page are in real tension — a page that fills most of the frame with only
    /// a subtle tone difference from its background looks, to a local blur, just like "gradual
    /// lighting variation to remove," so normalizing unconditionally can wash out exactly the
    /// case adaptive thresholding was meant to help. Try the direct (adaptively-thresholded,
    /// no normalization) pass first — it's what correctly handles low-contrast pages — and
    /// only fall back to illumination normalization if that finds nothing, which is when a
    /// strong cast shadow is the more likely cause.</summary>
    private static Point[][] FindDocumentContours(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        var direct = FindContoursWithAdaptiveCanny(blurred);
        // "Found something" isn't enough to skip the fallback — a stray noise fragment
        // shouldn't count. Only accept the direct pass if it found something plausibly
        // page-sized; otherwise a strong cast shadow (or other structured noise) may have
        // fragmented the real boundary, which is exactly what the illumination-normalized
        // fallback pass is for.
        var imageArea = src.Rows * (double)src.Cols;
        var bestDirectArea = direct.Length > 0 ? direct.Max(c => Cv2.ContourArea(c)) : 0;
        if (bestDirectArea / imageArea >= 0.05) return direct;

        using var illumination = new Mat();
        Cv2.GaussianBlur(gray, illumination, new Size(0, 0), sigmaX: 25);
        using var normalized = new Mat();
        Cv2.Divide(gray, illumination, normalized, scale: 255);
        using var normalizedBlurred = new Mat();
        Cv2.GaussianBlur(normalized, normalizedBlurred, new Size(5, 5), 0);
        var fallback = FindContoursWithAdaptiveCanny(normalizedBlurred);

        // Prefer whichever pass actually found something bigger/more useful — the fallback
        // isn't strictly better, it's just a different hypothesis (shadow vs. genuine edge).
        var bestFallbackArea = fallback.Length > 0 ? fallback.Max(c => Cv2.ContourArea(c)) : 0;
        return bestFallbackArea > bestDirectArea ? fallback : direct;
    }

    private static Point[][] FindContoursWithAdaptiveCanny(Mat blurredGray)
    {
        var (low, high) = AutoCannyThresholds(blurredGray);
        using var edged = new Mat();
        Cv2.Canny(blurredGray, edged, low, high);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var dilated = new Mat();
        Cv2.Dilate(edged, dilated, kernel, iterations: 2);

        Cv2.FindContours(dilated, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return contours;
    }

    /// <summary>Adaptive Canny thresholds anchored to the strongest edges actually present in
    /// the image, not overall pixel brightness — the commonly-cited "median pixel intensity"
    /// auto-Canny heuristic correlates with edge strength in a busy natural photo, but fails
    /// on a mostly-flat document image (a low-contrast page filling most of the frame has a
    /// pixel-intensity median dominated by the page itself, unrelated to how strong its edge
    /// against the background actually is).
    ///
    /// Anchored near the top of the gradient-magnitude distribution (99.5th percentile)
    /// rather than a more central one (e.g. the 90th): for a mostly flat/textureless document
    /// photo, the vast majority of pixels have near-zero gradient, so a percentile that isn't
    /// close to the very top sits in that near-zero noise floor — numerically unstable and,
    /// confirmed while building this, literally non-deterministic run-to-run from
    /// floating-point summation order in OpenCV's internal parallelism. Anchoring near the
    /// max instead lands on the real dominant edge (the page boundary) even in a flat scene,
    /// and still scales down appropriately for a busy/noisy one.</summary>
    private static (double Low, double High) AutoCannyThresholds(Mat gray)
    {
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, ksize: 3);
        using var magnitude = new Mat();
        Cv2.Magnitude(gx, gy, magnitude);

        magnitude.GetArray(out float[] values);
        if (values.Length == 0) return (50, 200);

        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        var anchorIndex = Math.Clamp((int)(sorted.Length * 0.995), 0, sorted.Length - 1);
        var strongEdge = sorted[anchorIndex];
        // Absolute floors/ceilings keep an extremely flat or extremely busy image from
        // pushing thresholds to unstable extremes at either end.
        var high = Math.Clamp(strongEdge * 0.5, 30, 220);
        // Canny's own recommended hysteresis ratio is roughly 1:2 to 1:3.
        var low = Math.Clamp(high * 0.4, 15, high);
        return (low, high);
    }

    /// <summary>Builds a <see cref="BoundaryDetection"/> from one contour: its padded bounding
    /// rect, a multi-factor confidence score, and 4-point approximation when the contour is
    /// quadrilateral. Shared by single-document and two-page detection so "what makes a good
    /// crop shape from a contour" is defined exactly once.</summary>
    private BoundaryDetection BuildDetection(Point[] contour, double imageArea, int srcCols, int srcRows)
    {
        var contourArea = Cv2.ContourArea(contour);
        var ratio = contourArea / imageArea;
        if (ratio < 0.1) return default;

        var rect = Cv2.BoundingRect(contour);

        // Rectangularity: how much of the contour's own minimum-area rectangle it actually
        // fills. A true rectangle scores near 1.0; a blobby or irregular shape scores lower
        // even at the same overall area — so two detections of equal size no longer score
        // identically just because "area ratio" was the only signal.
        var minAreaRect = Cv2.MinAreaRect(contour);
        var minAreaRectArea = (double)minAreaRect.Size.Width * minAreaRect.Size.Height;
        var rectangularity = minAreaRectArea > 0 ? Math.Min(1.0, contourArea / minAreaRectArea) : 0.0;

        var perimeter = Cv2.ArcLength(contour, true);
        var polygon = Cv2.ApproxPolyDP(contour, perimeter * 0.02, true);
        Point2f[]? quad = polygon.Length == 4
            ? OrderCorners(polygon.Select(point => new Point2f(point.X, point.Y)).ToArray())
            : null;

        // Corner-angle regularity: how close each corner is to a true 90 degrees, when a
        // clean 4-point approximation exists. A page photographed square scores near 1.0; a
        // sharply skewed or non-rectangular quad scores lower.
        var angleScore = quad != null ? CornerAngleScore(quad) : 1.0;

        // Size still matters — a tiny confident-looking rectangle is still a bad crop — but
        // shape quality now genuinely pulls the score down for irregular detections.
        var confidence = Math.Clamp(ratio, 0, 1) * 0.5 + rectangularity * 0.3 + angleScore * 0.2;

        // Don't add padding on a side that's already at the image border: the contour
        // reaching the frame edge means the real page edge likely extends past what the
        // camera captured, not that there's genuine background margin to include — padding
        // there would clip toward the opposite side for nothing.
        var touchesLeft = rect.X <= 1;
        var touchesTop = rect.Y <= 1;
        var touchesRight = rect.X + rect.Width >= srcCols - 1;
        var touchesBottom = rect.Y + rect.Height >= srcRows - 1;

        var padLeft = touchesLeft ? 0 : CropPadding;
        var padTop = touchesTop ? 0 : CropPadding;
        var padRight = touchesRight ? 0 : CropPadding;
        var padBottom = touchesBottom ? 0 : CropPadding;

        var x = Math.Max(0, rect.X - padLeft);
        var y = Math.Max(0, rect.Y - padTop);
        var w = Math.Min(srcCols - x, rect.Width + padLeft + padRight);
        var h = Math.Min(srcRows - y, rect.Height + padTop + padBottom);
        var paddedRect = new Rect(x, y, w, h);

        return new BoundaryDetection(true, confidence, paddedRect, quad);
    }

    /// <summary>Scores how close a quad's 4 corners are to true right angles (1.0 = perfect
    /// rectangle, tapering to 0 by 45 degrees of average deviation).</summary>
    private static double CornerAngleScore(Point2f[] quad)
    {
        double totalDeviation = 0;
        for (var i = 0; i < 4; i++)
        {
            var curr = quad[i];
            var prev = quad[(i + 3) % 4];
            var next = quad[(i + 1) % 4];
            var v1 = new Point2f(prev.X - curr.X, prev.Y - curr.Y);
            var v2 = new Point2f(next.X - curr.X, next.Y - curr.Y);
            var mag1 = Math.Sqrt(v1.X * v1.X + v1.Y * v1.Y);
            var mag2 = Math.Sqrt(v2.X * v2.X + v2.Y * v2.Y);
            if (mag1 < 1e-3 || mag2 < 1e-3) continue;
            var cosAngle = Math.Clamp((v1.X * v2.X + v1.Y * v2.Y) / (mag1 * mag2), -1.0, 1.0);
            var angleDegrees = Math.Acos(cosAngle) * 180.0 / Math.PI;
            totalDeviation += Math.Abs(angleDegrees - 90.0);
        }
        return Math.Clamp(1.0 - totalDeviation / 4.0 / 45.0, 0.0, 1.0);
    }

    /// <summary>Single-document detection used by both the mutating auto-crop pass
    /// (<see cref="TryAutoCrop"/>) and the read-only boundary lookup exposed to the UI
    /// (<see cref="DetectDocumentBoundary"/>): the largest sufficiently-large contour.</summary>
    private BoundaryDetection DetectBoundary(Mat src)
    {
        var contours = FindDocumentContours(src);
        if (contours.Length == 0) return default;

        var best = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        return BuildDetection(best, src.Rows * (double)src.Cols, src.Cols, src.Rows);
    }

    private readonly record struct TwoPageDetection(bool Found, BoundaryDetection Left, BoundaryDetection Right);

    /// <summary>Best-effort detection of two separate pages in one spread image (an open
    /// book), instead of a single shared boundary. Requires two contours that are each
    /// plausibly "about half the spread" (15%-60% of the frame) and don't substantially
    /// overlap horizontally — a spread with no visible gap between pages (flat lighting, no
    /// gutter shadow, pages pressed together) will usually trace as one large contour instead,
    /// which correctly fails this and should fall back to the simpler split-line flow rather
    /// than guess.</summary>
    private TwoPageDetection DetectTwoPageBoundaries(Mat src)
    {
        var contours = FindDocumentContours(src);
        if (contours.Length < 2) return default;

        var imageArea = src.Rows * (double)src.Cols;
        var candidates = contours
            .Select(c => (Contour: c, Ratio: Cv2.ContourArea(c) / imageArea))
            .Where(c => c.Ratio is >= 0.15 and <= 0.6)
            .OrderByDescending(c => c.Ratio)
            .Take(2)
            .ToList();
        if (candidates.Count < 2) return default;

        var ordered = candidates
            .Select(c => (c.Contour, Rect: Cv2.BoundingRect(c.Contour)))
            .OrderBy(c => c.Rect.X + c.Rect.Width / 2.0)
            .ToList();
        var (leftContour, leftRect) = ordered[0];
        var (rightContour, rightRect) = ordered[1];

        var overlapX = Math.Max(0, Math.Min(leftRect.Right, rightRect.Right) - Math.Max(leftRect.X, rightRect.X));
        var narrowerWidth = Math.Min(leftRect.Width, rightRect.Width);
        if (narrowerWidth > 0 && overlapX / (double)narrowerWidth > 0.25) return default;

        var left = BuildDetection(leftContour, imageArea, src.Cols, src.Rows);
        var right = BuildDetection(rightContour, imageArea, src.Cols, src.Rows);
        return left.Found && right.Found ? new TwoPageDetection(true, left, right) : default;
    }

    /// <summary>Read-only two-page boundary lookup for the UI's split-mode Crop Review, in the
    /// image's own pixel coordinates. Returns null when confident detection of two separate
    /// pages fails — callers should fall back to the single-line split flow rather than guess.</summary>
    public (DocumentBoundary Left, DocumentBoundary Right)? DetectSplitPageBoundaries(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return null;
            var detection = DetectTwoPageBoundaries(src);
            return detection.Found ? (ToDocumentBoundary(detection.Left), ToDocumentBoundary(detection.Right)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static DocumentBoundary ToDocumentBoundary(BoundaryDetection detection)
    {
        var rect = detection.PaddedRect;
        var quad = detection.Quad?.Select(p => new CropPoint(p.X, p.Y)).ToArray();
        return new DocumentBoundary(rect.X, rect.Y, rect.Width, rect.Height, detection.Confidence, quad);
    }

    private Mat TryAutoCrop(Mat src, ProcessingResult result)
    {
        try
        {
            var detection = DetectBoundary(src);
            if (!detection.Found)
            {
                result.Warnings.Add("No document contour detected — skipping auto-crop.");
                result.QcVerdict = CombineVerdict(result.QcVerdict, "WARNING");
                return src;
            }

            result.CropConfidence = detection.Confidence;

            // Gate on the same medium-confidence bar Crop Review already shows as its default
            // suggestion (MediumConfidenceThreshold), not the stricter CropConfidenceThreshold.
            // These two used to differ: Crop Review would happily pre-fill and display a
            // medium-confidence box, while this pipeline — the one that actually determines
            // what gets exported for every page nobody manually reviews — discarded that same
            // detection and shipped the full, uncropped frame instead. On real photos (as
            // opposed to the clean synthetic test images the confidence formula was tuned
            // against), medium confidence is the common case, not the exception.
            if (detection.Confidence < MediumConfidenceThreshold)
            {
                result.Warnings.Add($"Crop confidence low ({detection.Confidence:P1}). Keeping full image — needs manual crop review.");
                result.QcVerdict = CombineVerdict(result.QcVerdict, "WARNING");
                return src;
            }

            // A four-corner contour represents a photographed page. Rectifying it
            // here avoids the trapezoidal crop produced by a bounding rectangle.
            Mat cropped;
            if (detection.Quad != null)
            {
                cropped = WarpQuad(src, detection.Quad);
                result.Warnings.Add("Document boundary detected and perspective-corrected.");
            }
            else
            {
                cropped = src[detection.PaddedRect].Clone();
                result.Warnings.Add("Document boundary was not quadrilateral; applied rectangular auto-crop.");
            }

            if (detection.Confidence < CropConfidenceThreshold)
            {
                result.Warnings.Add($"Crop confidence medium ({detection.Confidence:P1}) — applied automatically; recommend reviewing in Crop Review.");
                result.QcVerdict = CombineVerdict(result.QcVerdict, "WARNING");
            }

            result.WasCropped = true;
            return cropped;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Auto-crop failed: {ex.Message}");
            result.QcVerdict = CombineVerdict(result.QcVerdict, "WARNING");
            return src;
        }
    }

    /// <summary>Read-only boundary lookup for the UI's Crop Review screen: detects the
    /// document in a still image and returns its padded bounding rect — plus a 4-point quad
    /// when the contour approximated one — in that image's own pixel coordinates, without
    /// modifying anything. Returns null when no confident boundary was found.</summary>
    public DocumentBoundary? DetectDocumentBoundary(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return null;
            var detection = DetectBoundary(src);
            // Medium-confidence detections are still returned (and pre-filled in Crop
            // Review) — just flagged by the caller as a lower-confidence suggestion via the
            // returned Confidence value, rather than rejected outright the way anything
            // below CropConfidenceThreshold used to be.
            if (!detection.Found || detection.Confidence < MediumConfidenceThreshold) return null;
            return ToDocumentBoundary(detection);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts a thinned set of strong edge-pixel coordinates from a still image, for
    /// the UI to snap a dragged crop corner to a nearby real edge instead of leaving it exactly
    /// where the pointer happens to be. Meant to be computed once when Crop Review opens (not
    /// re-run per drag frame) — all native resources are disposed before returning, so nothing
    /// keeps an OpenCV Mat alive for the window's lifetime.</summary>
    public static IReadOnlyList<CropPoint> DetectEdgePoints(string imagePath, int maxPoints = 5000)
    {
        if (!File.Exists(imagePath)) return Array.Empty<CropPoint>();
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
            if (src.Empty()) return Array.Empty<CropPoint>();
            using var blurred = new Mat();
            Cv2.GaussianBlur(src, blurred, new Size(5, 5), 0);
            using var edges = new Mat();
            Cv2.Canny(blurred, edges, 50, 200);

            using var idx = new Mat();
            Cv2.FindNonZero(edges, idx);
            idx.GetArray(out Point[] points);
            if (points.Length <= maxPoints)
                return points.Select(p => new CropPoint(p.X, p.Y)).ToArray();

            var stride = (int)Math.Ceiling(points.Length / (double)maxPoints);
            var sampled = new List<CropPoint>(maxPoints + 1);
            for (var i = 0; i < points.Length; i += stride)
                sampled.Add(new CropPoint(points[i].X, points[i].Y));
            return sampled;
        }
        catch
        {
            // Snapping is advisory. A failure here should degrade to "no snapping", never crash the editor.
            return Array.Empty<CropPoint>();
        }
    }

    /// <summary>Crops the quadrilateral region <paramref name="corners"/> (ordered top-left,
    /// top-right, bottom-right, bottom-left) of <paramref name="src"/> into an upright
    /// rectangle sized to the quad's own longest edges. Shared by the automatic auto-crop
    /// pass, the manual quad-edit path, and the plain rect/strip paths (default split, legacy
    /// saved crops) so there's exactly one implementation of "apply this crop shape" — a
    /// genuinely skewed quad gets a full perspective warp, while a shape that's already an
    /// axis-aligned rectangle (the common case: no perspective to correct) takes a cheap
    /// direct crop instead of paying full-image `WarpPerspective` cost for nothing.</summary>
    internal static Mat WarpQuad(Mat src, Point2f[] corners)
    {
        if (IsAxisAlignedRect(corners, out var rect))
            return src[ClampRectToBounds(rect, src.Cols, src.Rows)].Clone();

        var width = Math.Max(1, (int)Math.Round(Math.Max(Distance(corners[0], corners[1]), Distance(corners[2], corners[3]))));
        var height = Math.Max(1, (int)Math.Round(Math.Max(Distance(corners[0], corners[3]), Distance(corners[1], corners[2]))));
        var destination = new[] { new Point2f(0, 0), new Point2f(width - 1, 0), new Point2f(width - 1, height - 1), new Point2f(0, height - 1) };
        using var transform = Cv2.GetPerspectiveTransform(corners, destination);
        var warped = new Mat();
        // Cubic over the previous Linear: this is the one resample every auto-cropped or
        // split page goes through, so its softening shows up in every output — cubic keeps
        // edges/text noticeably crisper at a cost that's negligible next to the rest of the
        // pipeline (CLAHE, contour detection) on a single document-sized image.
        Cv2.WarpPerspective(src, warped, transform, new Size(width, height), InterpolationFlags.Cubic, BorderTypes.Replicate);
        return warped;
    }

    private static bool IsAxisAlignedRect(Point2f[] corners, out Rect rect)
    {
        rect = default;
        if (corners.Length != 4) return false;
        var (tl, tr, br, bl) = (corners[0], corners[1], corners[2], corners[3]);
        const float epsilon = 0.01f;
        if (Math.Abs(tl.Y - tr.Y) > epsilon || Math.Abs(bl.Y - br.Y) > epsilon ||
            Math.Abs(tl.X - bl.X) > epsilon || Math.Abs(tr.X - br.X) > epsilon)
            return false;

        var x = (int)Math.Round(Math.Min(tl.X, bl.X));
        var y = (int)Math.Round(Math.Min(tl.Y, tr.Y));
        var w = (int)Math.Round(Math.Max(tr.X, br.X) - x);
        var h = (int)Math.Round(Math.Max(bl.Y, br.Y) - y);
        rect = new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
        return true;
    }

    private static Rect ClampRectToBounds(Rect rect, int maxW, int maxH)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, maxW - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, maxH - 1));
        var w = Math.Clamp(rect.Width, 1, maxW - x);
        var h = Math.Clamp(rect.Height, 1, maxH - y);
        return new Rect(x, y, w, h);
    }

    private static Point2f[] OrderCorners(Point2f[] corners)
    {
        var ordered = new Point2f[4];
        ordered[0] = corners.OrderBy(point => point.X + point.Y).First(); // top-left
        ordered[2] = corners.OrderByDescending(point => point.X + point.Y).First(); // bottom-right
        ordered[1] = corners.OrderByDescending(point => point.X - point.Y).First(); // top-right
        ordered[3] = corners.OrderBy(point => point.X - point.Y).First(); // bottom-left
        return ordered;
    }

    private static double Distance(Point2f first, Point2f second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ───────────── BOOK SPLIT (GUTTER DETECTION) ─────────────

    private readonly record struct GutterDetection(double Fraction, double Confidence);

    /// <summary>Detects a book's gutter (spine shadow) as a fraction of image width by
    /// finding the darkest vertical band within the central portion of a two-page spread.
    /// Confidence is the relative brightness drop versus the search window's average —
    /// callers should fall back to an even 50/50 split when confidence is low rather than
    /// trust a false positive from uneven lighting.</summary>
    private static GutterDetection DetectGutter(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(9, 9), 0);

        using var colMeans = new Mat();
        Cv2.Reduce(blurred, colMeans, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);

        int width = blurred.Cols;
        // Search only the central band so the outer page edges/background can't be mistaken
        // for the gutter — a real spine sits between the two pages, not at the frame edges.
        int searchStart = (int)(width * 0.3);
        int searchEnd = (int)(width * 0.7);
        if (searchEnd <= searchStart) return new GutterDetection(0.5, 0);

        int minIdx = searchStart;
        float minVal = float.MaxValue;
        float sum = 0;
        int count = 0;
        for (int x = searchStart; x < searchEnd; x++)
        {
            float v = colMeans.At<float>(0, x);
            sum += v;
            count++;
            if (v < minVal) { minVal = v; minIdx = x; }
        }

        float windowAvg = count > 0 ? sum / count : 0;
        double confidence = windowAvg > 0 ? Math.Max(0, (windowAvg - minVal) / windowAvg) : 0;
        return new GutterDetection((double)minIdx / width, confidence);
    }

    /// <summary>Best-effort automatic gutter detection for the UI's split-percent slider
    /// and the default (non-manual-override) split pass. Returns a value in [1, 99]; falls
    /// back to an even 50 whenever the detected band isn't dark enough relative to its
    /// surroundings to be trusted.</summary>
    public double DetectGutterSplitPercent(string imagePath)
    {
        if (!File.Exists(imagePath)) return 50.0;
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return 50.0;
            var gutter = DetectGutter(src);
            if (gutter.Confidence < GutterConfidenceThreshold) return 50.0;
            return Math.Clamp(gutter.Fraction * 100.0, 1.0, 99.0);
        }
        catch
        {
            return 50.0;
        }
    }

    // ───────────── DESKEW ─────────────

    private Mat TryDeskew(Mat src, ProcessingResult result)
    {
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var binary = new Mat();
            Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            Cv2.BitwiseNot(binary, binary);

            // Find lines via HoughLinesP
            var lines = Cv2.HoughLinesP(binary, 1, Math.PI / 180, 100, minLineLength: 100, maxLineGap: 10);

            if (lines.Length == 0)
            {
                result.Warnings.Add("No lines detected for deskew.");
                return src;
            }

            // Calculate median angle
            var angles = new double[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var seg = lines[i];
                var angle = Math.Atan2(seg.P2.Y - seg.P1.Y, seg.P2.X - seg.P1.X) * 180.0 / Math.PI;
                angles[i] = angle;
            }
            Array.Sort(angles);
            var medianAngle = angles[angles.Length / 2];

            result.OriginalSkewDegrees = medianAngle;

            if (Math.Abs(medianAngle) > MaxDeskewDegrees)
            {
                result.Warnings.Add($"Skew too large ({medianAngle:F2}°). Not auto-correcting.");
                return src;
            }

            if (Math.Abs(medianAngle) < 0.1)
            {
                // No meaningful skew
                return src;
            }

            var center = new Point2f(src.Cols / 2f, src.Rows / 2f);
            using var rotMat = Cv2.GetRotationMatrix2D(center, medianAngle, 1.0);
            var rotated = new Mat();
            Cv2.WarpAffine(src, rotated, rotMat, src.Size(), InterpolationFlags.Cubic,
                BorderTypes.Constant, Scalar.White);

            result.WasDeskewed = true;
            result.AppliedCorrectionDegrees = medianAngle;
            return rotated;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Deskew failed: {ex.Message}");
            return src;
        }
    }

    // ───────────── ENHANCEMENT ─────────────

    private Mat ApplyEnhancement(Mat src)
    {
        // Mild contrast/brightness via CLAHE on L channel (for color images)
        using var lab = new Mat();
        Cv2.CvtColor(src, lab, ColorConversionCodes.BGR2Lab);
        Cv2.Split(lab, out var channels);

        using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
        clahe.Apply(channels[0], channels[0]);

        using var merged = new Mat();
        Cv2.Merge(channels, merged);
        var enhanced = new Mat();
        Cv2.CvtColor(merged, enhanced, ColorConversionCodes.Lab2BGR);

        foreach (var ch in channels) ch.Dispose();
        return enhanced;
    }

    /// <summary>Unsharp mask: blur a copy, then push the source away from that blur. Run last
    /// in the pipeline (after crop/deskew/enhancement) since every prior resample step softens
    /// edges a little, and CLAHE's contrast change is what sharpening should read against.</summary>
    private Mat Sharpen(Mat src)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(src, blurred, new Size(0, 0), SharpenSigma);
        var sharpened = new Mat();
        Cv2.AddWeighted(src, 1 + SharpenAmount, blurred, -SharpenAmount, 0, sharpened);
        return sharpened;
    }

    // ───────────── QUALITY CONTROL ─────────────

    public void RunQualityChecks(Mat src, ProcessingResult result)
    {
        // Blur detection via Laplacian variance
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var laplacian = new Mat();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out var mean, out var stddev);
        var variance = stddev.Val0 * stddev.Val0;
        result.BlurScore = variance;

        // Exposure check via histogram mean
        var hist = Cv2.Mean(gray);
        result.ExposureScore = hist.Val0;

        // Verdict
        bool blurOk = variance >= BlurThreshold;
        bool exposureOk = hist.Val0 > 30 && hist.Val0 < 225;

        string verdict;
        if (!blurOk)
        {
            result.Warnings.Add($"Blur score ({variance:F1}) below threshold ({BlurThreshold}).");
            verdict = "FAIL";
        }
        else if (!exposureOk)
        {
            result.Warnings.Add($"Exposure mean ({hist.Val0:F1}) outside acceptable range.");
            verdict = "WARNING";
        }
        else
        {
            verdict = "PASS";
        }

        // Blur/exposure aren't the only thing that can flag a page for review — TryAutoCrop
        // may already have set WARNING (low/medium crop confidence, no boundary found). Take
        // the worse of the two rather than letting whichever check runs last silently erase
        // the other's concern.
        result.QcVerdict = CombineVerdict(result.QcVerdict, verdict);
    }

    /// <summary>Combines two QC verdicts, keeping the more severe one. Multiple independent
    /// checks (crop confidence, blur, exposure) each contribute a verdict for the same page;
    /// the operator needs to see the worst one, not just whichever check happened to run
    /// last.</summary>
    private static string CombineVerdict(string current, string candidate)
    {
        static int Rank(string v) => v switch { "FAIL" => 2, "WARNING" => 1, "PASS" => 0, _ => -1 };
        return Rank(candidate) > Rank(current) ? candidate : current;
    }
}
