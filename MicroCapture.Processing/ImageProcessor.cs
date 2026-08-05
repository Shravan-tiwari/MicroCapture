using System;
using System.Globalization;
using System.IO;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>A single crop-shape corner, in an image's own pixel coordinates. OpenCvSharp-free
/// so it can safely cross into the UI project (mirrors the existing DTO pattern used by
/// <see cref="DocumentBoundary"/> and <see cref="LiveFrameCheck"/>).</summary>
public readonly record struct CropPoint(double X, double Y);

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
    public double MaxDeskewDegrees { get; set; } = 5.0;
    public int CropPadding { get; set; } = 10;
    public double BlurThreshold { get; set; } = 100.0;
    public double GutterConfidenceThreshold { get; set; } = 0.08;

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
    public ProcessingResult Process(string inputPath, string outputDirectory, bool splitPages = false, bool manualOverride = false, string? leftCrop = null, string? rightCrop = null)
    {
        var result = new ProcessingResult { OriginalFilePath = inputPath };

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
                Cv2.ImWrite(outLeft, leftResult);
                result.OutputFilePaths.Add(outLeft);
                leftResult.Dispose();

                // Process right
                using var rightMat = WarpQuad(src, rightCorners);
                var rightResult = ProcessSinglePage(rightMat, result, manualOverride);
                var outRight = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_2_right.tif");
                Cv2.ImWrite(outRight, rightResult);
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
                Cv2.ImWrite(outPath, processed);
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

    /// <summary>UI-facing, OpenCvSharp-free equivalent of <see cref="ParseCropCorners"/> — same
    /// parsing rules (legacy rect or new quad format, full-frame fallback), used by Crop Review
    /// to restore a previously saved crop shape so there's exactly one implementation of what a
    /// saved crop string means.</summary>
    public static CropPoint[] ParseCropShape(string cropStr, int maxW, int maxH) =>
        ParseCropCorners(cropStr, maxW, maxH).Select(p => new CropPoint(p.X, p.Y)).ToArray();

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
        RunQualityChecks(working, result);

        return working;
    }

    // ───────────── AUTO-CROP ─────────────

    private readonly record struct BoundaryDetection(bool Found, double Confidence, Rect PaddedRect, Point2f[]? Quad);

    /// <summary>Shared contour-based document detection used by both the mutating
    /// auto-crop pass (<see cref="TryAutoCrop"/>) and the read-only boundary lookup
    /// exposed to the UI (<see cref="DetectDocumentBoundary"/>). Returns the largest
    /// sufficiently-large contour's padded bounding rect, plus its 4-point approximation
    /// when the contour is quadrilateral (usable for perspective correction).</summary>
    private BoundaryDetection DetectBoundary(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);
        using var edged = new Mat();
        Cv2.Canny(blurred, edged, 50, 200);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var dilated = new Mat();
        Cv2.Dilate(edged, dilated, kernel, iterations: 2);

        Cv2.FindContours(dilated, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0) return default;

        double maxArea = 0;
        int maxIdx = 0;
        for (int i = 0; i < contours.Length; i++)
        {
            var area = Cv2.ContourArea(contours[i]);
            if (area > maxArea) { maxArea = area; maxIdx = i; }
        }

        var imageArea = src.Rows * src.Cols;
        var ratio = maxArea / imageArea;
        if (ratio < 0.1) return default;

        var rect = Cv2.BoundingRect(contours[maxIdx]);
        int x = Math.Max(0, rect.X - CropPadding);
        int y = Math.Max(0, rect.Y - CropPadding);
        int w = Math.Min(src.Cols - x, rect.Width + 2 * CropPadding);
        int h = Math.Min(src.Rows - y, rect.Height + 2 * CropPadding);
        var paddedRect = new Rect(x, y, w, h);

        var perimeter = Cv2.ArcLength(contours[maxIdx], true);
        var polygon = Cv2.ApproxPolyDP(contours[maxIdx], perimeter * 0.02, true);
        Point2f[]? quad = polygon.Length == 4
            ? OrderCorners(polygon.Select(point => new Point2f(point.X, point.Y)).ToArray())
            : null;

        return new BoundaryDetection(true, ratio, paddedRect, quad);
    }

    private Mat TryAutoCrop(Mat src, ProcessingResult result)
    {
        try
        {
            var detection = DetectBoundary(src);
            if (!detection.Found)
            {
                result.Warnings.Add("No document contour detected — skipping auto-crop.");
                return src;
            }

            result.CropConfidence = detection.Confidence;

            if (detection.Confidence < CropConfidenceThreshold)
            {
                result.Warnings.Add($"Crop confidence low ({detection.Confidence:P1}). Keeping full image.");
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
            result.WasCropped = true;
            return cropped;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Auto-crop failed: {ex.Message}");
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
            if (!detection.Found || detection.Confidence < CropConfidenceThreshold) return null;
            var rect = detection.PaddedRect;
            var quad = detection.Quad?.Select(p => new CropPoint(p.X, p.Y)).ToArray();
            return new DocumentBoundary(rect.X, rect.Y, rect.Width, rect.Height, detection.Confidence, quad);
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
        Cv2.WarpPerspective(src, warped, transform, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Replicate);
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
            Cv2.WarpAffine(src, rotated, rotMat, src.Size(), InterpolationFlags.Linear,
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

        if (!blurOk)
        {
            result.Warnings.Add($"Blur score ({variance:F1}) below threshold ({BlurThreshold}).");
            result.QcVerdict = "FAIL";
        }
        else if (!exposureOk)
        {
            result.Warnings.Add($"Exposure mean ({hist.Val0:F1}) outside acceptable range.");
            result.QcVerdict = "WARNING";
        }
        else
        {
            result.QcVerdict = "PASS";
        }
    }
}
