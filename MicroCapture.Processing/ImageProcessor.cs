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

/// <summary>One-time camera lens intrinsics/distortion calibration, computed by
/// <see cref="LensCalibrationService"/> from photos of a ChArUco calibration board and applied
/// via <see cref="ImageProcessor.Undistort"/> at the very start of the pipeline (before
/// cropping), since undistorting a full raw frame avoids principal-point bookkeeping a
/// post-crop undistort would need. <see cref="CalibratedWidth"/>/<see cref="CalibratedHeight"/>
/// record the pixel resolution the calibration itself was computed at, so <see cref="Undistort"/>
/// can scale the focal length/principal point if a capture's resolution ever differs —
/// distortion coefficients themselves are resolution-independent. OpenCvSharp-free (mirrors
/// <see cref="CropPoint"/>/<see cref="TiffMetadata"/>) so it can safely cross into the UI
/// project.</summary>
public readonly record struct LensCalibration(double Fx, double Fy, double Cx, double Cy, double[] DistCoeffs, int CalibratedWidth, int CalibratedHeight);

/// <summary>One operator-calibrated fixed-frame rectangle, in the pixel space of the image it
/// was calibrated against (see Batch.FixedFrameImageWidth/Height). Unlike <see cref="CropPoint"/>
/// quads, fixed frames are always axis-aligned — no perspective correction, since they exist
/// for a stationary, straight-down copy-stand shot.</summary>
public readonly record struct FixedFrameRect(double X, double Y, double Width, double Height);

/// <summary>Book-curvature correction model: 5 control points along a page's top content edge
/// and 5 along its bottom edge, in that page's own (post-crop) pixel coordinates, evenly
/// spaced by X. Auto-detection seeds these from the detected text-line envelope; the operator
/// can drag each point's Y in Crop Review (X stays pinned to its slot). Both auto-detected and
/// manually-adjusted curves round-trip through this same shape — see
/// ImageProcessor.DetectDewarpCurve/ApplyDewarp/FormatDewarpCurve/ParseDewarpCurve.
/// OpenCvSharp-free (mirrors <see cref="CropPoint"/>/<see cref="DocumentBoundary"/>) so it can
/// safely cross into the UI project.</summary>
public readonly record struct DewarpModel(CropPoint[] TopControlPoints, CropPoint[] BottomControlPoints)
{
    public const int ControlPointCount = 5;
}

/// <summary>Document boundary detected in a still image, in that image's own pixel coordinates.
/// <see cref="Quad"/> (ordered top-left, top-right, bottom-right, bottom-left) is populated
/// whenever a boundary was found at all — <c>ImageProcessor.DeriveQuad</c> always derives 4
/// corners from the detected contour (falling back to its convex hull's extreme corners when
/// the contour doesn't cleanly approximate to 4 points), so it's null only in the vanishingly
/// rare case of a fully degenerate contour. The UI should prefer it over the axis-aligned
/// <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/> rect when present,
/// since it captures perspective skew the rect can't.</summary>
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
    // Raised from the old 5.0 now that the primary estimate comes from actual text-line
    // baselines (TryDeskew) rather than raw whole-image Hough lines — far less prone to the
    // false-positive risk (shadows/page borders/background objects) that motivated the
    // original tight clamp. WarpAffine keeps the source canvas size unchanged, so very large
    // angles start clipping corners; 15° is a reasoned starting point pending validation
    // against real captures, not a hard physical limit.
    public double MaxDeskewDegrees { get; set; } = 15.0;
    // Text-line angle estimates whose interquartile range exceeds this are inconsistent enough
    // that the median isn't trustworthy as a single global rotation — falls back to whole-image
    // Hough detection instead of risking a wrong rotation from disagreeing evidence.
    public double MaxDeskewAngleIqrDegrees { get; set; } = 3.0;
    public int CropPadding { get; set; } = 10;
    // How far outward a fixed-frame's calibrated rectangle is padded before it's searched for
    // the real page boundary (as a fraction of the rectangle's own width/height) — the real
    // page edge may sit slightly outside what the operator drew. Starting point pending
    // validation against real fixed-frame captures.
    public double FixedFrameSearchPadFraction { get; set; } = 0.15;
    public double BlurThreshold { get; set; } = 100.0;
    public double GutterConfidenceThreshold { get; set; } = 0.08;
    // Validated against the operator's real photo fixtures (tools/SmokeTest/Fixtures/real-photos)
    // plus the dev-mode mock camera frame: all 7 genuine spreads across book-curve and
    // trapezoid sets show an absolute drop of 30-124 grayscale levels (their lit-paper
    // backgrounds are bright enough for a real spine shadow to read as a sizeable drop), while
    // the dev mock frame's near-black synthetic background hits GutterConfidenceThreshold's
    // *relative* bar from pure compression/blur noise at a drop of only ~13 levels — see
    // GutterDetection.AbsoluteDropLevels. 20 sits with margin on both sides of that gap. Only
    // used to gate the automatic (non-manual) spread-vs-single-page promotion in Process(), not
    // the already-in-split-mode split-point picker, which only needs "where," not "whether."
    public double GutterAbsoluteDropThreshold { get; set; } = 20.0;
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
    public ProcessingResult Process(string inputPath, string outputDirectory, bool splitPages = false, bool manualOverride = false, string? leftCrop = null, string? rightCrop = null, TiffMetadata? metadata = null, bool dewarpEnabled = false, string? dewarpCurve = null, bool dewarpManualOverride = false, bool binarizeEnabled = false, LensCalibration? lensCalibration = null)
    {
        var result = new ProcessingResult { OriginalFilePath = inputPath };
        var meta = metadata ?? TiffMetadata.Default;
        // A saved manual curve currently applies uniformly to both halves of a split spread —
        // each half still gets its own independently *auto-detected* curve when not manually
        // overridden (DetectDewarpCurve runs fresh per Mat below), only a manual edit is shared.
        var savedDewarp = !string.IsNullOrEmpty(dewarpCurve) ? ParseDewarpCurve(dewarpCurve) : null;

        if (!File.Exists(inputPath))
        {
            result.Success = false;
            result.Errors.Add($"Input file not found: {inputPath}");
            return result;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            using var rawSrc = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (rawSrc.Empty())
            {
                result.Success = false;
                result.Errors.Add("Failed to decode image.");
                return result;
            }

            // Lens undistortion runs before anything else — it corrects the camera's own
            // optics, not how the page sits in frame, so every geometric correction after this
            // point (boundary detection, trapezoid warp, curve dewarp, deskew) sees an already-
            // rectilinear image instead of having to work around residual barrel/pincushion
            // distortion baked into the raw capture.
            using var undistorted = lensCalibration is { } calib ? SafeUndistort(rawSrc, calib, result) : null;
            var src = undistorted ?? rawSrc;

            // Automatic (non-manual) captures never got a chance to route through the split
            // path unless an operator remembered to check Batch.SplitBookPages up front — in
            // practice most real spreads went through the single-whole-image quad path
            // instead, which is a poor fit for book curvature (one cubic trying to model two
            // independent per-page bowl shapes meeting at a spine trough).
            //
            // The spine-shadow gutter signal (DetectGutter), not the two-page contour detector
            // below, is what actually distinguishes a real spread from a real single page on
            // this app's own captures: validated against the operator's real photo fixtures
            // (tools/SmokeTest/Fixtures/real-photos), all 4 genuine book-curve spreads scored
            // gutter confidence 0.23-0.79 and the one genuine single trapezoid page scored
            // 0.05 — cleanly on either side of GutterConfidenceThreshold (0.08). The contour
            // detector (DetectTwoPageBoundaries), by contrast, found zero of those 5 photos:
            // real spreads with pages pressed together at the spine (no visible physical gap,
            // just a shadow) trace as one contour, exactly per its own doc comment. It's kept
            // below purely as an opportunistic quality upgrade for the (rarer) spread that does
            // have a visible gap, never as the trigger for whether to split at all.
            // Skipped entirely under manualOverride: an operator who already reviewed the crop
            // in Crop Review made an explicit choice that shouldn't be second-guessed.
            var gutter = DetectGutter(src);
            var autoSplitBySpine = !manualOverride
                && gutter.Confidence >= GutterConfidenceThreshold
                && gutter.AbsoluteDropLevels >= GutterAbsoluteDropThreshold;

            if (splitPages || autoSplitBySpine)
            {
                var autoTwoPage = !manualOverride ? DetectTwoPageBoundaries(src) : default;
                var autoTwoPageConfident = autoTwoPage.Found
                    && autoTwoPage.Left.Confidence >= MediumConfidenceThreshold
                    && autoTwoPage.Right.Confidence >= MediumConfidenceThreshold;

                // Split logic. Each side is parsed to its 4 corners; a plain saved strip
                // (today's UI) is an axis-aligned quad and takes WarpQuad's cheap-crop path,
                // while a per-half quad (manual edit, or an auto-detected page contour) gets a
                // real warp — one shared implementation, no special-casing here.
                Point2f[] leftCorners, rightCorners;
                if (manualOverride && !string.IsNullOrEmpty(leftCrop) && !string.IsNullOrEmpty(rightCrop))
                {
                    leftCorners = ParseCropCorners(leftCrop, src.Width, src.Height);
                    rightCorners = ParseCropCorners(rightCrop, src.Width, src.Height);
                }
                else if (autoTwoPageConfident && autoTwoPage.Left.Quad != null && autoTwoPage.Right.Quad != null)
                {
                    // Each half already comes from its own independently-detected page
                    // contour (real edges, not an axis-aligned guess at a shared split line) —
                    // this is what makes a V-cradle spread's asymmetric, draping page shapes
                    // work, where a single vertical split line would not.
                    leftCorners = autoTwoPage.Left.Quad;
                    rightCorners = autoTwoPage.Right.Quad;
                    if (!splitPages) result.Warnings.Add("Two-page spread auto-detected (page contours) — split into left/right pages automatically.");
                }
                else
                {
                    // Best-effort automatic gutter detection; falls back to an even
                    // 50/50 split when no confident spine shadow is found.
                    var splitX = gutter.Confidence >= GutterConfidenceThreshold
                        ? (int)Math.Round(src.Width * gutter.Fraction)
                        : src.Width / 2;
                    splitX = Math.Clamp(splitX, 1, src.Width - 1);
                    leftCorners = RectCorners(0, 0, splitX, src.Height);
                    rightCorners = RectCorners(splitX, 0, src.Width - splitX, src.Height);
                    if (!splitPages) result.Warnings.Add("Two-page spread auto-detected (spine shadow) — split into left/right pages automatically.");
                }

                // Process left — its own spine edge is this half's right edge (leftMat.Cols).
                using var leftMat = WarpQuad(src, leftCorners);
                var leftResult = ProcessSinglePage(leftMat, result, manualOverride, dewarpEnabled, savedDewarp, dewarpManualOverride, spineXHint: leftMat.Cols, binarizeEnabled, meta.Dpi);
                var outLeft = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_1_left.tif");
                WriteTiff(outLeft, leftResult, meta, result.WasBinarized);
                result.OutputFilePaths.Add(outLeft);
                leftResult.Dispose();

                // Process right — its own spine edge is this half's left edge (x = 0).
                using var rightMat = WarpQuad(src, rightCorners);
                var rightResult = ProcessSinglePage(rightMat, result, manualOverride, dewarpEnabled, savedDewarp, dewarpManualOverride, spineXHint: 0, binarizeEnabled, meta.Dpi);
                var outRight = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_2_right.tif");
                WriteTiff(outRight, rightResult, meta, result.WasBinarized);
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

                var processed = ProcessSinglePage(manualCrop ?? src, result, manualOverride, dewarpEnabled, savedDewarp, dewarpManualOverride, binarizeEnabled: binarizeEnabled, dpi: meta.Dpi);
                manualCrop?.Dispose();

                var outName = Path.GetFileNameWithoutExtension(inputPath) + "_processed.tif";
                var outPath = Path.Combine(outputDirectory, outName);
                WriteTiff(outPath, processed, meta, result.WasBinarized);
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
    /// pre-calibrated fixed rectangles as search regions, not final crops: each rectangle is
    /// padded and searched for the actual page boundary within it (see the
    /// <c>searchRegion</c> overload of <see cref="DetectBoundary"/>), getting the same
    /// perspective/curve/skew correction as a normal auto-cropped capture. A rectangle only
    /// degrades to a plain crop-as-drawn when detection confidence within it is too low — never
    /// worse than the previous unconditional-crop behavior, only better on a confident
    /// detection. This is what lets a calibration/copy-stand rig — whose rectangle can only
    /// ever be axis-aligned — still get real trapezoid/curve correction per capture, instead of
    /// baking in whatever keystone or page bow happens to be present that day.</summary>
    public ProcessingResult ProcessFixedFrames(string inputPath, string outputDirectory, string fixedFramesSpec, TiffMetadata? metadata = null, bool dewarpEnabled = false, string? dewarpCurve = null, bool dewarpManualOverride = false, bool binarizeEnabled = false, LensCalibration? lensCalibration = null)
    {
        var result = new ProcessingResult { OriginalFilePath = inputPath };
        var meta = metadata ?? TiffMetadata.Default;
        var savedDewarp = !string.IsNullOrEmpty(dewarpCurve) ? ParseDewarpCurve(dewarpCurve) : null;

        if (!File.Exists(inputPath))
        {
            result.Success = false;
            result.Errors.Add($"Input file not found: {inputPath}");
            return result;
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            using var rawSrc = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (rawSrc.Empty())
            {
                result.Success = false;
                result.Errors.Add("Failed to decode image.");
                return result;
            }

            using var undistorted = lensCalibration is { } calib ? SafeUndistort(rawSrc, calib, result) : null;
            var src = undistorted ?? rawSrc;

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

                var frameResult = new ProcessingResult { OriginalFilePath = inputPath };
                using var processed = ProcessSinglePage(src, frameResult, skipAutoCrop: false, dewarpEnabled, savedDewarp, dewarpManualOverride, binarizeEnabled: binarizeEnabled, dpi: meta.Dpi, searchRegion: rect);

                var outName = $"{Path.GetFileNameWithoutExtension(inputPath)}_frame{(i + 1).ToString("D" + padWidth, CultureInfo.InvariantCulture)}.tif";
                var outPath = Path.Combine(outputDirectory, outName);
                WriteTiff(outPath, processed, meta, frameResult.WasBinarized);
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

    /// <summary>Writes a processed page as TIFF, tagging it with the batch's chosen DPI and
    /// Software/DateTime/Artist metadata so the file's own properties are correct instead of
    /// absent (Explorer/Photoshop default an untagged TIFF to 96 DPI, which has nothing to do
    /// with what was actually captured). This only sets metadata tags — it does not resample or
    /// add pixel detail.
    ///
    /// Writes pixel data and every tag in one single-pass LibTiff.NET "w"-mode session — same
    /// approach <see cref="WriteBitonalTiff"/> already used, now used here too. This method used
    /// to write pixels via <c>Cv2.ImWrite</c> and then reopen the file in "r+" mode purely to
    /// add the Artist/Software/DateTime tags LibTiff doesn't support, finishing with
    /// <c>Tiff.RewriteDirectory()</c> to relocate the now-larger directory. That reopen-and-move
    /// step is the confirmed root cause of a real corruption bug: on at least one real capture,
    /// it silently scrambled the row/strip layout of the already-correctly-processed image,
    /// producing a diagonally-sheared, garbled output with no exception or warning anywhere —
    /// found by bisecting the pipeline stage-by-stage and confirming every processing step
    /// (crop/deskew/dewarp/enhance/sharpen) produced a correct image right up until this write
    /// step. Writing every tag before the first scanline, in one pass, never needs to relocate
    /// anything and has no such failure mode.</summary>
    private static void WriteTiff(string path, Mat mat, TiffMetadata metadata, bool binarized = false)
    {
        if (binarized)
        {
            WriteBitonalTiff(path, mat, metadata);
            return;
        }

        var width = mat.Cols;
        var height = mat.Rows;
        var channels = mat.Channels();
        // Mat.GetArray(out byte[]) only accepts single-channel Mats — reshape to a
        // single-channel (height, width*channels) view first (a metadata-only reinterpretation
        // of the same row-major buffer, not a copy — safe to dispose independently of the
        // caller's own `mat`) so a 3-channel BGR Mat can be read the same way the bitonal
        // (1-channel) path already does.
        using var flatMat = mat.Reshape(1, height);
        flatMat.GetArray(out byte[] pixels);

        using var tiff = Tiff.Open(path, "w");
        if (tiff == null) throw new IOException($"Could not create TIFF: {path}");

        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, channels);
        tiff.SetField(TiffTag.PHOTOMETRIC, channels >= 3 ? Photometric.RGB : Photometric.MINISBLACK);
        tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
        tiff.SetField(TiffTag.FILLORDER, FillOrder.MSB2LSB);
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.ROWSPERSTRIP, height);
        tiff.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
        tiff.SetField(TiffTag.XRESOLUTION, (float)metadata.Dpi);
        tiff.SetField(TiffTag.YRESOLUTION, (float)metadata.Dpi);
        tiff.SetField(TiffTag.SOFTWARE, "MicroCapture");
        tiff.SetField(TiffTag.DATETIME, metadata.TimestampUtc.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(metadata.Author))
            tiff.SetField(TiffTag.ARTIST, metadata.Author);

        var stride = width * channels;
        var rowBuf = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            if (channels == 3)
            {
                // OpenCvSharp Mats are BGR-ordered; TIFF's RGB photometric expects RGB per
                // pixel, so swap R/B per pixel while copying into the scanline buffer.
                for (var x = 0; x < width; x++)
                {
                    var i = rowOffset + x * 3;
                    var o = x * 3;
                    rowBuf[o] = pixels[i + 2];
                    rowBuf[o + 1] = pixels[i + 1];
                    rowBuf[o + 2] = pixels[i];
                }
            }
            else
            {
                Array.Copy(pixels, rowOffset, rowBuf, 0, stride);
            }
            tiff.WriteScanline(rowBuf, y);
        }
    }

    /// <summary>Writes <paramref name="mat"/> (expected 8-bit single-channel, values only 0 or
    /// 255 — see <see cref="ApplySauvolaBinarization"/>) as a genuine 1-bit-per-pixel TIFF with
    /// CCITT Group 4 fax compression: the archival-standard bitonal format, not merely an
    /// 8-bit image that happens to look black-and-white — this is what actually makes the file
    /// size win real. Tag set and bit-packing convention follow BitMiracle.LibTiff.NET's own
    /// official ImageToBitonalTiff sample (Photometric.MINISBLACK: packed bit 1 = white, bit 0
    /// = black). All structural tags must be set before the first scanline is written, so
    /// (unlike <see cref="WriteTiff"/>'s color path) this writes everything — pixels and
    /// metadata tags alike — in one "w"-mode pass rather than reopening the file afterward.</summary>
    private static void WriteBitonalTiff(string path, Mat mat, TiffMetadata metadata)
    {
        var width = mat.Cols;
        var height = mat.Rows;
        mat.GetArray(out byte[] pixels);

        var stride = (width + 7) / 8;
        var packed = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var rowIn = y * width;
            var rowOut = y * stride;
            for (var x = 0; x < width; x++)
            {
                if (pixels[rowIn + x] != 0) // MINISBLACK: bit 1 = white, bit 0 = black.
                    packed[rowOut + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        using var tiff = Tiff.Open(path, "w");
        if (tiff == null) throw new IOException($"Could not create TIFF: {path}");

        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 1);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
        tiff.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
        tiff.SetField(TiffTag.FILLORDER, FillOrder.MSB2LSB);
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.ROWSPERSTRIP, height);
        tiff.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
        tiff.SetField(TiffTag.XRESOLUTION, (float)metadata.Dpi);
        tiff.SetField(TiffTag.YRESOLUTION, (float)metadata.Dpi);
        tiff.SetField(TiffTag.SOFTWARE, "MicroCapture");
        tiff.SetField(TiffTag.DATETIME, metadata.TimestampUtc.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(metadata.Author))
            tiff.SetField(TiffTag.ARTIST, metadata.Author);

        for (var y = 0; y < height; y++)
            tiff.WriteScanline(packed, y * stride, y, 0);
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

    /// <summary>Parses a batch's saved lens-calibration spec —
    /// "fx,fy,cx,cy;k1,k2,p1,p2,k3;calibratedWidth,calibratedHeight" (see
    /// <see cref="FormatLensCalibration"/>) — back into a <see cref="LensCalibration"/>. Returns
    /// null on any malformed spec, same "treat as no saved calibration" convention as
    /// <see cref="ParseDewarpCurve"/>.</summary>
    public static LensCalibration? ParseLensCalibration(string spec)
    {
        try
        {
            var parts = spec.Split(';');
            if (parts.Length != 3) return null;
            var intrinsics = parts[0].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var distCoeffs = parts[1].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var size = parts[2].Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (intrinsics.Length != 4 || size.Length != 2) return null;
            return new LensCalibration(intrinsics[0], intrinsics[1], intrinsics[2], intrinsics[3], distCoeffs, size[0], size[1]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Inverse of <see cref="ParseLensCalibration"/>.</summary>
    public static string FormatLensCalibration(LensCalibration calibration)
    {
        var intrinsics = string.Join(",", new[] { calibration.Fx, calibration.Fy, calibration.Cx, calibration.Cy }.Select(v => v.ToString("G17", CultureInfo.InvariantCulture)));
        var distCoeffs = string.Join(",", calibration.DistCoeffs.Select(v => v.ToString("G17", CultureInfo.InvariantCulture)));
        return $"{intrinsics};{distCoeffs};{calibration.CalibratedWidth},{calibration.CalibratedHeight}";
    }

    /// <summary>Undistorts a raw frame using a one-time lens calibration (see
    /// <see cref="LensCalibration"/>) — the only geometric correction that runs before cropping,
    /// since it corrects the camera's own optics rather than anything about how the page sits
    /// in frame. Scales the focal length/principal point if <paramref name="src"/>'s resolution
    /// differs from the resolution the calibration was computed at; distortion coefficients
    /// themselves don't need scaling. Never throws — same degrade-on-failure shape as every
    /// other pipeline step (see callers in <see cref="Process"/>/<see cref="ProcessFixedFrames"/>).</summary>
    internal static Mat Undistort(Mat src, LensCalibration calib)
    {
        var sx = calib.CalibratedWidth > 0 ? src.Cols / (double)calib.CalibratedWidth : 1.0;
        var sy = calib.CalibratedHeight > 0 ? src.Rows / (double)calib.CalibratedHeight : 1.0;
        using var cameraMatrix = new Mat(3, 3, MatType.CV_64F);
        cameraMatrix.SetArray(new double[]
        {
            calib.Fx * sx, 0, calib.Cx * sx,
            0, calib.Fy * sy, calib.Cy * sy,
            0, 0, 1
        });
        using var distCoeffs = new Mat(1, calib.DistCoeffs.Length, MatType.CV_64F);
        distCoeffs.SetArray(calib.DistCoeffs);
        var undistorted = new Mat();
        Cv2.Undistort(src, undistorted, cameraMatrix, distCoeffs);
        return undistorted;
    }

    /// <summary>Never-throws wrapper around <see cref="Undistort"/> — same degrade-to-unchanged
    /// shape as every other pipeline step (<see cref="TryAutoCrop"/>, <see cref="TryApplyDewarp"/>,
    /// etc.): a bad/mismatched calibration must never turn into a failed capture.</summary>
    private static Mat? SafeUndistort(Mat src, LensCalibration calib, ProcessingResult result)
    {
        try
        {
            var undistorted = Undistort(src, calib);
            result.Warnings.Add("Lens distortion correction applied.");
            return undistorted;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Lens undistortion failed: {ex.Message}");
            return null;
        }
    }

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

    /// <summary>Inverse of <see cref="ParseDewarpCurve"/> — "top:x1,y1,...,x5,y5;bottom:x1,y1,...,x5,y5",
    /// pixel coords in the page's own space. Same delimiter conventions as
    /// <see cref="FormatFixedFrames"/>/<see cref="ParseCropCorners"/>.</summary>
    public static string FormatDewarpCurve(DewarpModel model) =>
        $"top:{FormatPoints(model.TopControlPoints)};bottom:{FormatPoints(model.BottomControlPoints)}";

    private static string FormatPoints(CropPoint[] points) =>
        string.Join(",", points.SelectMany(p => new[]
        {
            p.X.ToString("F1", CultureInfo.InvariantCulture),
            p.Y.ToString("F1", CultureInfo.InvariantCulture)
        }));

    /// <summary>Parses a saved dewarp curve (see <see cref="FormatDewarpCurve"/>) back into its
    /// top/bottom control points. Returns null on any malformed or incomplete spec — callers
    /// should treat that the same as "no saved curve" rather than guess.</summary>
    public static DewarpModel? ParseDewarpCurve(string spec)
    {
        try
        {
            CropPoint[]? top = null;
            CropPoint[]? bottom = null;
            foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var sep = part.IndexOf(':');
                if (sep < 0) continue;
                var label = part[..sep];
                var points = ParsePoints(part[(sep + 1)..]);
                if (points == null) continue;
                if (label == "top") top = points;
                else if (label == "bottom") bottom = points;
            }
            return top != null && bottom != null ? new DewarpModel(top, bottom) : null;
        }
        catch
        {
            return null;
        }
    }

    private static CropPoint[]? ParsePoints(string csv)
    {
        var n = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (n.Length == 0 || n.Length % 2 != 0) return null;
        var points = new CropPoint[n.Length / 2];
        for (var i = 0; i < points.Length; i++)
            points[i] = new CropPoint(n[i * 2], n[i * 2 + 1]);
        return points;
    }

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

    private Mat ProcessSinglePage(Mat input, ProcessingResult result, bool skipAutoCrop, bool dewarpEnabled = false, DewarpModel? savedDewarp = null, bool dewarpManualOverride = false, double? spineXHint = null, bool binarizeEnabled = false, int dpi = 300, Rect? searchRegion = null)
    {
        var working = input.Clone();

        if (!skipAutoCrop)
            working = TryAutoCrop(working, result, searchRegion);

        // Deskew before dewarp: the curve fit assumes text lines run horizontally, so any
        // residual global tilt corrupts the per-column curve sampling if left uncorrected
        // first (both now share the same text-line detection, so this is coarse-to-fine —
        // global rotation resolved before the finer per-column curve).
        working = TryDeskew(working, result);
        working = TryApplyDewarp(working, result, dewarpEnabled, savedDewarp, dewarpManualOverride, spineXHint);

        working = ApplyEnhancement(working);
        working = Sharpen(working);
        // Quality checks must see the real (color/grayscale) capture, not a bilevel result —
        // blur/exposure scores are meaningless once thresholded to pure black-and-white, so
        // binarization runs last, after QC rather than before it.
        RunQualityChecks(working, result);
        working = TryApplyBinarization(working, result, binarizeEnabled, dpi);

        return working;
    }

    // ───────────── AUTO-CROP ─────────────

    private readonly record struct BoundaryDetection(bool Found, double Confidence, Rect PaddedRect, Point2f[]? Quad);

    private readonly record struct ContourPass(Point[][] Contours);

    /// <summary>Shared edge-detection step behind every contour-based detector in this class
    /// (single-document auto-crop, the UI's boundary lookup, and two-page split detection) —
    /// defined once so all of them see the same document edges.
    ///
    /// Two passes, not one: illumination normalization (dividing by a heavily-blurred copy of
    /// the image, to flatten gradual shadows before edge detection) and a genuinely
    /// low-contrast page are in real tension — a page that fills most of the frame with only
    /// a subtle tone difference from its background looks, to a local blur, just like "gradual
    /// lighting variation to remove," so normalizing unconditionally can wash out exactly the
    /// case adaptive thresholding was meant to help. Run the direct (adaptively-thresholded,
    /// no normalization) pass first — it's what correctly handles low-contrast pages — and
    /// only compute the illumination-normalized pass if that finds nothing plausibly
    /// page-sized, which is when a strong cast shadow is the more likely cause. Which pass's
    /// result actually gets used is decided by the caller via <see cref="BuildDetection"/>'s
    /// own confidence score, not by raw contour area here — area alone is too weak a proxy for
    /// "found the right thing," which is exactly why that scoring function exists.</summary>
    private static (ContourPass Direct, ContourPass? Normalized) FindDocumentContourPasses(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        var direct = FindContoursWithAdaptiveCanny(blurred);
        var imageArea = src.Rows * (double)src.Cols;
        var bestDirectArea = direct.Length > 0 ? direct.Max(c => Cv2.ContourArea(c)) : 0;
        if (bestDirectArea / imageArea >= 0.05) return (new ContourPass(direct), null);

        using var illumination = new Mat();
        Cv2.GaussianBlur(gray, illumination, new Size(0, 0), sigmaX: 25);
        using var normalized = new Mat();
        Cv2.Divide(gray, illumination, normalized, scale: 255);
        using var normalizedBlurred = new Mat();
        Cv2.GaussianBlur(normalized, normalizedBlurred, new Size(5, 5), 0);
        var fallback = FindContoursWithAdaptiveCanny(normalizedBlurred);

        return (new ContourPass(direct), new ContourPass(fallback));
    }

    /// <summary>Best (largest-area) detection from one contour pass, or a not-found default
    /// when the pass has no contours at all.</summary>
    private BoundaryDetection BestDetectionFromContours(Point[][] contours, double imageArea, int cols, int rows)
    {
        if (contours.Length == 0) return default;
        var best = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        return BuildDetection(best, imageArea, cols, rows);
    }

    /// <summary>Runs both contour passes against <paramref name="regionMat"/> and keeps
    /// whichever produced the higher-confidence <see cref="BoundaryDetection"/> — the shared
    /// core behind both whole-image and bounded-search-region detection (see
    /// <see cref="DetectBoundary"/>).</summary>
    private BoundaryDetection DetectBoundaryInMat(Mat regionMat)
    {
        var (directPass, normalizedPass) = FindDocumentContourPasses(regionMat);
        var imageArea = regionMat.Rows * (double)regionMat.Cols;
        var best = BestDetectionFromContours(directPass.Contours, imageArea, regionMat.Cols, regionMat.Rows);
        if (normalizedPass is { } np)
        {
            var normalizedBest = BestDetectionFromContours(np.Contours, imageArea, regionMat.Cols, regionMat.Rows);
            if (normalizedBest.Confidence > best.Confidence) best = normalizedBest;
        }
        return best;
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
        var quad = DeriveQuad(contour, perimeter);

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

    /// <summary>Derives a 4-corner quad from a contour, trying <see cref="Cv2.ApproxPolyDP"/> at
    /// increasing epsilon before falling back to the convex hull's 4 extreme corners. This
    /// always returns a quad for any non-degenerate contour — never null — so callers
    /// (specifically <see cref="TryAutoCrop"/>) never need to silently skip perspective
    /// correction just because a real photo's page edge (soft lighting, slight curvature)
    /// didn't collapse cleanly to 4 points at one fixed epsilon, which is exactly the bug that
    /// let a large fraction of real captures bypass trapezoid correction entirely. A
    /// poor-quality hull is naturally penalized by <see cref="BuildDetection"/>'s own
    /// confidence terms (rectangularity, corner-angle score), not by silently degrading to an
    /// uncorrected rectangular crop.</summary>
    private static Point2f[]? DeriveQuad(Point[] contour, double perimeter)
    {
        foreach (var epsilonFactor in new[] { 0.02, 0.03, 0.045, 0.065, 0.08 })
        {
            var poly = Cv2.ApproxPolyDP(contour, perimeter * epsilonFactor, true);
            if (poly.Length == 4)
                return OrderCorners(poly.Select(p => new Point2f(p.X, p.Y)).ToArray());
        }

        var hull = Cv2.ConvexHull(contour);
        if (hull.Length < 3) return null; // Truly degenerate — BuildDetection's own ratio<0.1 gate already excludes most of these.

        return OrderCorners(hull.Select(p => new Point2f(p.X, p.Y)).ToArray());
    }

    /// <summary>Single-document detection used by both the mutating auto-crop pass
    /// (<see cref="TryAutoCrop"/>) and the read-only boundary lookup exposed to the UI
    /// (<see cref="DetectDocumentBoundary"/>): the largest sufficiently-large contour.
    ///
    /// When <paramref name="searchRegion"/> is given (fixed-frame captures — see
    /// <see cref="ProcessFixedFrames"/>), the calibrated rectangle is treated as a search
    /// region rather than a final crop: padded outward so the real page edge (which may sit
    /// slightly outside the operator-drawn rectangle) stays fully visible, detection runs
    /// against just that sub-region, and the result is translated back into the full image's
    /// coordinate space. This is what lets fixed-frame captures get real perspective/curve
    /// correction instead of an unconditional rectangle crop.</summary>
    private BoundaryDetection DetectBoundary(Mat src, Rect? searchRegion = null)
    {
        if (searchRegion is not { } region) return DetectBoundaryInMat(src);

        // Starting point pending validation against real fixed-frame captures (none of the
        // real-photo fixtures gathered so far are fixed-frame shots) — see FixedFrameSearchPadFraction.
        var padded = PadAndClampRect(region, FixedFrameSearchPadFraction, src.Cols, src.Rows);
        using var sub = new Mat(src, padded);
        var detection = DetectBoundaryInMat(sub);
        if (!detection.Found) return detection;

        return detection with
        {
            PaddedRect = new Rect(detection.PaddedRect.X + padded.X, detection.PaddedRect.Y + padded.Y, detection.PaddedRect.Width, detection.PaddedRect.Height),
            Quad = detection.Quad?.Select(p => new Point2f(p.X + padded.X, p.Y + padded.Y)).ToArray()
        };
    }

    private static Rect PadAndClampRect(Rect rect, double padFraction, int maxW, int maxH)
    {
        var padX = (int)Math.Round(rect.Width * padFraction);
        var padY = (int)Math.Round(rect.Height * padFraction);
        var x = Math.Max(0, rect.X - padX);
        var y = Math.Max(0, rect.Y - padY);
        var right = Math.Min(maxW, rect.X + rect.Width + padX);
        var bottom = Math.Min(maxH, rect.Y + rect.Height + padY);
        return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
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
        var (directPass, normalizedPass) = FindDocumentContourPasses(src);
        var direct = TwoPageDetectionFromContours(directPass.Contours, src);
        if (direct.Found) return direct;
        return normalizedPass is { } np ? TwoPageDetectionFromContours(np.Contours, src) : default;
    }

    private TwoPageDetection TwoPageDetectionFromContours(Point[][] contours, Mat src)
    {
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

    /// <summary><paramref name="searchRegion"/>, when given, is a fixed-frame's calibrated
    /// rectangle (see <see cref="ProcessFixedFrames"/>) — the fallback on low/no confidence
    /// becomes that calibrated rectangle instead of the untouched full frame, since that's
    /// what today's fixed-frame path always produces anyway. This makes the search-region path
    /// strictly no-worse than before on a detection failure, and strictly better (real
    /// perspective/curve correction) on success.</summary>
    private Mat TryAutoCrop(Mat src, ProcessingResult result, Rect? searchRegion = null)
    {
        Mat FallbackCrop() => searchRegion is { } r ? src[ClampRectToBounds(r, src.Cols, src.Rows)].Clone() : src;

        try
        {
            var detection = DetectBoundary(src, searchRegion);
            if (!detection.Found)
            {
                result.Warnings.Add(searchRegion != null
                    ? "No document contour detected within calibrated frame — used calibrated rectangle as-is."
                    : "No document contour detected — skipping auto-crop.");
                result.QcVerdict = CombineVerdict(result.QcVerdict, "WARNING");
                return FallbackCrop();
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
                result.Warnings.Add(searchRegion != null
                    ? $"Search-region auto-crop confidence low ({detection.Confidence:P1}) — used calibrated rectangle as-is."
                    : $"Crop confidence low ({detection.Confidence:P1}). Keeping full image — needs manual crop review.");
                result.QcVerdict = CombineVerdict(result.QcVerdict, "WARNING");
                return FallbackCrop();
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
                // DeriveQuad only returns null for a fully degenerate contour — vanishingly
                // rare, but WarpQuad has nothing to work with there.
                cropped = src[detection.PaddedRect].Clone();
                result.Warnings.Add("Document boundary was degenerate; applied rectangular auto-crop.");
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
            return FallbackCrop();
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

    /// <summary>Byte-decoded counterpart to <see cref="DetectDewarpCurve"/> for Crop Review's
    /// curve editor, which seeds itself from an in-memory cropped preview (see
    /// <see cref="CropPreviewRenderer"/>) rather than a file on disk. Returns null on any
    /// failure or low-confidence detection, same as the path-based detectors in this class.</summary>
    public static DewarpModel? DetectDewarpCurveFromBytes(byte[] encodedImage)
    {
        try
        {
            using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
            return mat.Empty() ? null : DetectDewarpCurve(mat, null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Diagnostic-only entry point (not used by <see cref="DetectDewarpCurve"/> itself):
    /// dumps every detected text-line blob and its individual top/bottom edge polynomial fit,
    /// for troubleshooting a page whose dewarp result looks wrong — <see cref="FitAveragedCurve"/>
    /// silently averages across whatever <see cref="DetectTextLineBlobs"/> found, so a single
    /// rogue "line" (e.g. a table rule, not real text) can be invisible from the final
    /// <see cref="DewarpModel"/> alone.</summary>
    public static string DebugDewarpLines(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";

        var (binary, lines) = DetectTextLineBlobs(mat);
        using (binary)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");
            sb.AppendLine($"Lines detected: {lines.Count}");
            foreach (var line in lines)
            {
                var topSamples = SampleLineEdge(binary, line, topEdge: true);
                var bottomSamples = SampleLineEdge(binary, line, topEdge: false);
                var topFit = topSamples.Count >= 8 ? WeightedPolyFit(topSamples.Select(s => (s.X, s.Y, 1.0)).ToList(), 3) : null;
                var bottomFit = bottomSamples.Count >= 8 ? WeightedPolyFit(bottomSamples.Select(s => (s.X, s.Y, 1.0)).ToList(), 3) : null;
                sb.AppendLine($"  rect=({line.X},{line.Y},{line.Width}x{line.Height}) topSamples={topSamples.Count} topFit=[{FormatCoeffs(topFit)}] bottomSamples={bottomSamples.Count} bottomFit=[{FormatCoeffs(bottomFit)}]");
            }
            return sb.ToString();
        }
    }

    /// <summary>Diagnostic-only entry point: runs the real <see cref="DetectDewarpCurve"/> and
    /// dumps the resulting model's control points, for troubleshooting a wrong-looking dewarp
    /// result at the level of "what curve did it actually compute" rather than per-line
    /// intermediate fits (see <see cref="DebugDewarpLines"/>).</summary>
    public static string DebugDewarpModel(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var model = DetectDewarpCurve(mat, null);
        if (model is not { } m) return "No confident curve detected (null model).";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");
        sb.AppendLine("Top:    " + string.Join("  ", m.TopControlPoints.Select(p => $"({p.X:F0},{p.Y:F1})")));
        sb.AppendLine("Bottom: " + string.Join("  ", m.BottomControlPoints.Select(p => $"({p.X:F0},{p.Y:F1})")));
        return sb.ToString();
    }

    /// <summary>Diagnostic-only entry point: reports the raw gutter-shadow and two-page-contour
    /// detection signals for troubleshooting the auto-split promotion in <see cref="Process"/>
    /// against a real photo, without needing a whole batch/queue round-trip.</summary>
    public static string DebugSpreadDetection(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");
        var gutter = DetectGutter(mat);
        sb.AppendLine($"Gutter: fraction={gutter.Fraction:F4} confidence={gutter.Confidence:F4} absoluteDropLevels={gutter.AbsoluteDropLevels:F2}");
        var instance = new ImageProcessor();
        var twoPage = instance.DetectTwoPageBoundaries(mat);
        sb.AppendLine($"TwoPage: found={twoPage.Found} leftConfidence={twoPage.Left.Confidence:F4} rightConfidence={twoPage.Right.Confidence:F4}");
        return sb.ToString();
    }

    private static string FormatCoeffs(double[]? coeffs) =>
        coeffs == null ? "null" : string.Join(",", coeffs.Select(c => c.ToString("F4", CultureInfo.InvariantCulture)));

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

    /// <param name="AbsoluteDropLevels">The raw brightness drop (0-255 grayscale) between the
    /// search window's average and its darkest column. <see cref="Confidence"/> alone is a
    /// *relative* drop (avg-min)/avg, which blows up from ordinary compression/blur noise when
    /// the window's average brightness is itself near zero (a near-black background, not a lit
    /// page) — a real spine shadow always darkens a real midtone-or-brighter page, so callers
    /// deciding whether to trust this as "there really is a spine here" (as opposed to merely
    /// "where would the split line go, given we already know it's a spread") should gate on
    /// this too, not Confidence alone.</param>
    private readonly record struct GutterDetection(double Fraction, double Confidence, double AbsoluteDropLevels);

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
        if (searchEnd <= searchStart) return new GutterDetection(0.5, 0, 0);

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
        return new GutterDetection((double)minIdx / width, confidence, Math.Max(0, windowAvg - minVal));
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

    // ───────────── BOOK CURVE DEWARP ─────────────

    /// <summary>Applies book-curve correction if enabled/overridden, and reports what happened
    /// via <paramref name="result"/>'s warnings — same shape as <see cref="TryAutoCrop"/>:
    /// never throws, degrades to returning <paramref name="src"/> unchanged on any failure or
    /// low-confidence detection.</summary>
    private Mat TryApplyDewarp(Mat src, ProcessingResult result, bool dewarpEnabled, DewarpModel? savedDewarp, bool dewarpManualOverride, double? spineXHint)
    {
        if (!dewarpEnabled && !dewarpManualOverride) return src;
        try
        {
            var model = dewarpManualOverride && savedDewarp is { } saved ? saved : DetectDewarpCurve(src, spineXHint);
            if (model is not { } m)
            {
                if (dewarpEnabled)
                    result.Warnings.Add("No confident book curvature detected — skipping dewarp.");
                return src;
            }

            result.Warnings.Add("Book curve correction applied.");
            return ApplyDewarp(src, m);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Book curve correction failed: {ex.Message}");
            return src;
        }
    }

    /// <summary>Detects text-line-shaped blobs in a page image: dark content merged
    /// horizontally into line shapes via morphological closing (without touching vertical
    /// extent, so each blob's own top/bottom pixels still trace that line's true shape rather
    /// than a morphology-smoothed approximation of it), filtered to blobs wide and thin enough
    /// to plausibly be a text line rather than noise or a heading. Shared by book-curve dewarp
    /// (curvature evidence) and text-line-based deskew (rotation evidence) so both read the
    /// same definition of "what is a text line," ordered top-to-bottom. Caller owns and must
    /// dispose the returned <see cref="Mat"/> — the same binary, morphologically-closed image
    /// <see cref="SampleLineEdge"/> expects.</summary>
    private static (Mat Binary, List<Rect> Lines) DetectTextLineBlobs(Mat page)
    {
        using var gray = new Mat();
        Cv2.CvtColor(page, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);
        using var binary = new Mat();
        Cv2.Threshold(blurred, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var kernelWidth = Math.Max(15, page.Cols / 40);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kernelWidth, 1));
        var closed = new Mat();
        Cv2.MorphologyEx(binary, closed, MorphTypes.Close, kernel);

        Cv2.FindContours(closed, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

        var minWidth = page.Cols * 0.15;
        var lines = contours
            .Select(Cv2.BoundingRect)
            .Where(r => r.Width >= minWidth && r.Width > r.Height * 3)
            .OrderBy(r => r.Y)
            .ToList();

        return (closed, lines);
    }

    /// <summary>Detects book-spine curvature from the page's own text-line shapes and fits a
    /// 5-point top/bottom curve to it — no hardware depth sensing, just the same "cubic sheet"
    /// idea used by tools like page_dewarp: text lines are assumed straight in the undistorted
    /// page, so their observed curvature reveals the page's own bend. Modeled as a per-column
    /// vertical displacement (curvature runs parallel to the spine), which keeps this a 1D
    /// curve fit instead of a full 2D mesh.
    ///
    /// <paramref name="spineXHint"/>, when known (the x-coordinate of this page's own spine
    /// edge — 0 or the page width — passed by split-page callers), anchors the *opposite* edge
    /// as flat even where text evidence is sparse there, so the fit doesn't extrapolate wildly
    /// into a margin with few detected lines. Returns null when there isn't enough line
    /// evidence to trust a curve — callers should skip correction rather than guess.</summary>
    internal static DewarpModel? DetectDewarpCurve(Mat page, double? spineXHint)
    {
        var (binary, lines) = DetectTextLineBlobs(page);
        using (binary)
        {
            // Need enough separate lines to trust a curve shape at all — one or two blobs
            // could just as easily be a heading or noise, not evidence of the page's actual
            // bend.
            if (lines.Count < 3) return null;

            // Require the detected lines to span a healthy fraction of the page width — a
            // curve fit from a narrow sliver of text says little about the page's overall bend.
            var spanWidth = lines.Max(r => r.Width);
            if (spanWidth < page.Cols * 0.35) return null;

            const int degree = 3;
            var topCoeffs = FitAveragedCurve(binary, lines, topEdge: true, page.Cols, spineXHint, degree);
            var bottomCoeffs = FitAveragedCurve(binary, lines, topEdge: false, page.Cols, spineXHint, degree);
            if (topCoeffs == null || bottomCoeffs == null) return null;

            return new DewarpModel(
                SampleControlPoints(topCoeffs, page.Cols),
                SampleControlPoints(bottomCoeffs, page.Cols));
        }
    }

    /// <summary>Fits one edge's (top or bottom) page-curvature polynomial from *every* detected
    /// text line's own samples, not just the extreme line at that edge — trusting a single line
    /// (the old behavior) is a high-variance, easily-wrong estimate of what should be the
    /// strongest available signal.
    ///
    /// Each line only spans a fraction of the page width, so fitting a separate cubic per line
    /// and averaging the resulting *coefficients* is invalid — coefficients from cubics fit
    /// over different, narrow X windows aren't directly comparable, and averaging them produced
    /// exactly the kind of wildly-diverging curve this was meant to fix (confirmed against a
    /// real photo while building this). Instead, every line's samples are pooled into one
    /// dataset — after subtracting that line's own median Y first, so each line contributes
    /// only its *shape* to the combined fit, not its absolute row position — and a single cubic
    /// is fit to the pooled set. Pooled together, lines collectively span close to the full page
    /// width even though each individually covers only part of it, so this one fit is both
    /// numerically well-supported (see <see cref="WeightedPolyFit"/>'s own centering) and
    /// informed by every line's evidence at once, rather than N separate poorly-constrained
    /// per-line extrapolations.
    ///
    /// The vertical baseline is then anchored by shifting this shape curve so it passes through
    /// the real extreme line's own actual (median X, median Y) point — topmost line for the top
    /// edge, bottommost for the bottom edge — rather than evaluating any single line's own fit
    /// at x=0 (the original design here, and the actual root cause of a confirmed real-photo
    /// failure: the topmost detected "line" covered only the page's right ~28%, and a cubic fit
    /// from only that narrow window, evaluated at x=0 — nowhere near its own data — extrapolated
    /// to a Y value more than 6x the page's own height). Anchoring through a real, trusted data
    /// point instead of an extrapolated one removes that failure mode entirely.</summary>
    private static double[]? FitAveragedCurve(Mat binary, List<Rect> lines, bool topEdge, int pageWidth, double? spineXHint, int degree)
    {
        var pooled = new List<(double X, double Y, double W)>();
        var lineMedians = new List<(double X, double Y)>();
        foreach (var line in lines)
        {
            var samples = SampleLineEdge(binary, line, topEdge);
            if (samples.Count < 8) continue;
            var sortedY = samples.Select(s => s.Y).OrderBy(y => y).ToList();
            var medianY = sortedY[sortedY.Count / 2];
            foreach (var s in samples) pooled.Add((s.X, s.Y - medianY, 1.0));
            lineMedians.Add((samples.Average(s => s.X), medianY));
        }
        if (lineMedians.Count < 3) return null;

        // Spine anchor: pin the page edge farthest from the spine flat even where text evidence
        // is sparse there, so the fit doesn't extrapolate wildly into a margin with few detected
        // lines — same purpose the old per-line FitCurveWithSpineAnchor served, now folded
        // directly into the pooled (row-normalized) dataset so it benefits from the same
        // wide-domain numerical support as everything else fit here.
        if (spineXHint is double spineX)
        {
            var farEdgeX = spineX < pageWidth / 2.0 ? (double)pageWidth : 0.0;
            var nearFar = pooled.OrderBy(p => Math.Abs(p.X - farEdgeX)).Take(Math.Min(20, pooled.Count)).ToList();
            if (nearFar.Count > 0)
                pooled.Add((farEdgeX, nearFar.Average(p => p.Y), 8.0 * nearFar.Count));
        }

        var shapeCoeffs = WeightedPolyFit(pooled, degree);
        if (shapeCoeffs == null) return null;

        // `lines` (and therefore lineMedians, built in the same order) is already ordered
        // top-to-bottom by DetectTextLineBlobs.
        var anchor = topEdge ? lineMedians[0] : lineMedians[^1];
        var shapeValueAtAnchorXExcludingConstant = EvalPoly(shapeCoeffs, anchor.X) - shapeCoeffs[0];
        var avgCoeffs = (double[])shapeCoeffs.Clone();
        avgCoeffs[0] = anchor.Y - shapeValueAtAnchorXExcludingConstant;

        return avgCoeffs;
    }

    /// <summary>For each column spanned by <paramref name="rect"/>, the first foreground pixel
    /// scanning from the top (the line's top edge) or from the bottom (its bottom edge),
    /// restricted to the blob's own bounding rows — safe because FindContours already
    /// separated adjacent lines into distinct blobs, so this can't pick up a neighboring
    /// line's ink.</summary>
    private static List<(double X, double Y)> SampleLineEdge(Mat binary, Rect rect, bool topEdge)
    {
        var samples = new List<(double, double)>();
        var yStart = Math.Max(0, rect.Y);
        var yEnd = Math.Min(binary.Rows - 1, rect.Y + rect.Height - 1);
        var xEnd = Math.Min(binary.Cols, rect.X + rect.Width);
        for (var x = Math.Max(0, rect.X); x < xEnd; x++)
        {
            if (topEdge)
            {
                for (var y = yStart; y <= yEnd; y++)
                {
                    if (binary.At<byte>(y, x) != 0) { samples.Add((x, y)); break; }
                }
            }
            else
            {
                for (var y = yEnd; y >= yStart; y--)
                {
                    if (binary.At<byte>(y, x) != 0) { samples.Add((x, y)); break; }
                }
            }
        }
        return samples;
    }

    /// <summary>Weighted least-squares polynomial fit via the normal equations. Degree is small
    /// (cubic) and there are at most a few thousand points, so a hand-rolled Gauss-Jordan solve
    /// is simpler than pulling in a linear-algebra dependency for this one call site.
    ///
    /// Fits internally in <paramref name="points"/>' own X coordinates *shifted to be centered
    /// on zero* (or on the whole page's own center, when multiple fits later get their
    /// coefficients combined — see <see cref="FitAveragedCurve"/>), then converts the result
    /// back to the original raw-X basis before returning, via <see cref="ShiftPolynomialToOrigin"/>.
    /// A raw pixel X coordinate on a several-thousand-pixel-wide photo raised to the 3rd power is
    /// large enough (low-to-mid 10^10s) that the normal equations become severely
    /// ill-conditioned without this — confirmed while building this feature: fitting directly
    /// in raw pixel coordinates produced individually-plausible-looking per-sample residuals but
    /// wildly diverging coefficients (a fitted curve evaluating to y-values many times the
    /// image's own height just outside the fitted line's own narrow column range), corrupting
    /// dewarp on a real photo. Centering first is the standard fix.</summary>
    private static double[]? WeightedPolyFit(List<(double X, double Y, double W)> points, int degree)
    {
        if (points.Count == 0) return null;
        var centerX = points.Average(p => p.X);

        var n = degree + 1;
        var a = new double[n, n];
        var b = new double[n];
        foreach (var (x, y, w) in points)
        {
            var xc = x - centerX;
            var powers = new double[2 * n - 1];
            powers[0] = 1.0;
            for (var i = 1; i < powers.Length; i++) powers[i] = powers[i - 1] * xc;
            for (var i = 0; i < n; i++)
            {
                b[i] += w * powers[i] * y;
                for (var j = 0; j < n; j++)
                    a[i, j] += w * powers[i + j];
            }
        }

        var centeredCoeffs = SolveLinearSystem(a, b, n);
        return centeredCoeffs == null ? null : ShiftPolynomialToOrigin(centeredCoeffs, centerX);
    }

    /// <summary>Converts polynomial coefficients fit in (x - <paramref name="centerX"/>) powers
    /// back into plain x powers, via binomial expansion of each (x - centerX)^k term — so every
    /// caller of <see cref="WeightedPolyFit"/> (and anything that combines multiple fits'
    /// coefficients, like <see cref="FitAveragedCurve"/>) can keep using <see cref="EvalPoly"/>
    /// and friends with a raw x exactly as before, unaware that the fit itself was conditioned
    /// on a centered x internally.</summary>
    private static double[] ShiftPolynomialToOrigin(double[] centeredCoeffs, double centerX)
    {
        var n = centeredCoeffs.Length;
        var result = new double[n];
        for (var k = 0; k < n; k++)
        {
            if (centeredCoeffs[k] == 0) continue;
            for (var i = 0; i <= k; i++)
                result[i] += centeredCoeffs[k] * BinomialCoefficient(k, i) * Math.Pow(-centerX, k - i);
        }
        return result;
    }

    private static double BinomialCoefficient(int n, int k)
    {
        double result = 1;
        for (var i = 0; i < k; i++) result = result * (n - i) / (i + 1);
        return result;
    }

    private static double[]? SolveLinearSystem(double[,] a, double[] b, int n)
    {
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-9) return null; // singular — not enough distinct data

            if (pivot != col)
            {
                for (var j = 0; j < n; j++) (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            var diag = a[col, col];
            for (var j = 0; j < n; j++) a[col, j] /= diag;
            b[col] /= diag;

            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = a[row, col];
                if (factor == 0) continue;
                for (var j = 0; j < n; j++) a[row, j] -= factor * a[col, j];
                b[row] -= factor * b[col];
            }
        }
        return b;
    }

    private static CropPoint[] SampleControlPoints(double[] coeffs, int pageWidth)
    {
        var points = new CropPoint[DewarpModel.ControlPointCount];
        for (var i = 0; i < DewarpModel.ControlPointCount; i++)
        {
            var x = pageWidth <= 1 ? 0 : (double)i / (DewarpModel.ControlPointCount - 1) * (pageWidth - 1);
            points[i] = new CropPoint(x, EvalPoly(coeffs, x));
        }
        return points;
    }

    private static double EvalPoly(double[] coeffs, double x)
    {
        var result = 0.0;
        var power = 1.0;
        foreach (var c in coeffs)
        {
            result += c * power;
            power *= x;
        }
        return result;
    }

    /// <summary>Remaps <paramref name="page"/> so its curved top/bottom content edges (as
    /// modeled by <paramref name="model"/>'s control points) become straight — for each column,
    /// vertically rescales that column's local [top(x), bottom(x)] content band into the page's
    /// overall flattest band (the min top / max bottom across all columns, so no content is cut
    /// off), via a single <see cref="Cv2.Remap"/>. Same shape as <see cref="WarpQuad"/>: builds
    /// a coordinate map once, then one native call.</summary>
    internal static Mat ApplyDewarp(Mat page, DewarpModel model)
    {
        var width = page.Cols;
        var height = page.Rows;
        var topXs = model.TopControlPoints.Select(p => p.X).ToArray();
        var topYs = model.TopControlPoints.Select(p => p.Y).ToArray();
        var bottomXs = model.BottomControlPoints.Select(p => p.X).ToArray();
        var bottomYs = model.BottomControlPoints.Select(p => p.Y).ToArray();

        var topDense = new double[width];
        var bottomDense = new double[width];
        for (var x = 0; x < width; x++)
        {
            topDense[x] = InterpolateSpline(topXs, topYs, x);
            bottomDense[x] = InterpolateSpline(bottomXs, bottomYs, x);
        }

        var flatTop = topDense.Min();
        var flatBottom = bottomDense.Max();
        if (flatBottom - flatTop < 2) return page; // degenerate model — nothing meaningful to correct

        var colTop = new double[width];
        var colScale = new double[width];
        for (var x = 0; x < width; x++)
        {
            var localTop = topDense[x];
            var localSpan = bottomDense[x] - localTop;
            if (localSpan < 2) localSpan = flatBottom - flatTop;
            colTop[x] = localTop;
            colScale[x] = localSpan / (flatBottom - flatTop);
        }

        var mapXData = new float[height * width];
        var mapYData = new float[height * width];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                mapXData[row + x] = x;
                mapYData[row + x] = (float)(colTop[x] + (y - flatTop) * colScale[x]);
            }
        }

        using var mapX = new Mat(height, width, MatType.CV_32FC1);
        using var mapY = new Mat(height, width, MatType.CV_32FC1);
        mapX.SetArray(mapXData);
        mapY.SetArray(mapYData);

        var warped = new Mat();
        Cv2.Remap(page, warped, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Replicate);
        return warped;
    }

    /// <summary>Catmull-Rom interpolation through the (X-sorted) control points, clamped flat
    /// beyond the first/last point. Used both to densify the 5 control points into a per-column
    /// curve for <see cref="ApplyDewarp"/>, and (by the UI) to preview the curve as the operator
    /// drags a control point.</summary>
    public static double InterpolateSpline(double[] xs, double[] ys, double x)
    {
        var n = xs.Length;
        if (n == 0) return 0;
        if (n == 1) return ys[0];
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];

        var i = 0;
        while (i < n - 2 && x > xs[i + 1]) i++;

        var p0 = ys[Math.Max(0, i - 1)];
        var p1 = ys[i];
        var p2 = ys[i + 1];
        var p3 = ys[Math.Min(n - 1, i + 2)];

        var x1 = xs[i];
        var x2 = xs[i + 1];
        var t = x2 > x1 ? (x - x1) / (x2 - x1) : 0;
        var t2 = t * t;
        var t3 = t2 * t;

        return 0.5 * (
            2 * p1 +
            (-p0 + p2) * t +
            (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 +
            (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    // ───────────── DESKEW ─────────────

    /// <summary>Estimates and corrects global page rotation, preferring the same text-line
    /// evidence <see cref="DetectDewarpCurve"/> uses over raw whole-image line detection: each
    /// detected line's own top-edge slope gives a local angle estimate, and the median across
    /// all lines is far less prone to the false-positive risk (page borders, shadows,
    /// background objects visible in a camera-captured photo, not a flatbed scan) that raw
    /// <see cref="Cv2.HoughLinesP(InputArray, double, double, int, double, double)"/> on the
    /// whole binarized image is exposed to. Falls back to that whole-image Hough method
    /// (<see cref="TryDeskewViaHough"/>) when there isn't enough text-line evidence, or when the
    /// per-line angles disagree too much to trust a single global rotation from them.</summary>
    private Mat TryDeskew(Mat src, ProcessingResult result)
    {
        try
        {
            var (binary, lines) = DetectTextLineBlobs(src);
            using (binary)
            {
                if (lines.Count >= 3)
                {
                    var angles = new List<double>();
                    foreach (var line in lines)
                    {
                        var samples = SampleLineEdge(binary, line, topEdge: true);
                        if (samples.Count < 8) continue;
                        var fit = WeightedPolyFit(samples.Select(s => (s.X, s.Y, 1.0)).ToList(), degree: 1);
                        if (fit == null) continue;
                        angles.Add(Math.Atan2(fit[1], 1.0) * 180.0 / Math.PI); // fit[1] = dy/dx slope
                    }

                    if (angles.Count >= 3)
                    {
                        angles.Sort();
                        var medianAngle = angles[angles.Count / 2];
                        var iqr = angles[(int)(angles.Count * 0.75)] - angles[(int)(angles.Count * 0.25)];
                        if (iqr <= MaxDeskewAngleIqrDegrees)
                            return ApplyDeskewAngle(src, medianAngle, result, "text-line");

                        result.Warnings.Add($"Text-line angles inconsistent (IQR {iqr:F1}°) — using whole-image line detection for deskew.");
                    }
                    else
                    {
                        result.Warnings.Add("Not enough reliable text-line angle fits — using whole-image line detection for deskew.");
                    }
                }
                else
                {
                    result.Warnings.Add("Not enough text lines for line-based deskew — using whole-image line detection.");
                }
            }

            return TryDeskewViaHough(src, result);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Deskew failed: {ex.Message}");
            return src;
        }
    }

    /// <summary>Whole-image Hough-line deskew — the original (pre-text-line-based) method, kept
    /// as <see cref="TryDeskew"/>'s fallback for pages with too little/inconsistent text-line
    /// evidence rather than left as the primary method.</summary>
    private Mat TryDeskewViaHough(Mat src, ProcessingResult result)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        Cv2.BitwiseNot(binary, binary);

        var lines = Cv2.HoughLinesP(binary, 1, Math.PI / 180, 100, minLineLength: 100, maxLineGap: 10);
        if (lines.Length == 0)
        {
            result.Warnings.Add("No lines detected for deskew.");
            return src;
        }

        var angles = new double[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            var seg = lines[i];
            angles[i] = Math.Atan2(seg.P2.Y - seg.P1.Y, seg.P2.X - seg.P1.X) * 180.0 / Math.PI;
        }
        Array.Sort(angles);
        var medianAngle = angles[angles.Length / 2];

        return ApplyDeskewAngle(src, medianAngle, result, "whole-image Hough");
    }

    /// <summary>Shared rotate-and-report step behind both <see cref="TryDeskew"/>'s primary
    /// text-line estimate and its <see cref="TryDeskewViaHough"/> fallback, so clamping/
    /// reporting behavior is defined exactly once regardless of which estimate produced the
    /// angle.</summary>
    private Mat ApplyDeskewAngle(Mat src, double angle, ProcessingResult result, string source)
    {
        result.OriginalSkewDegrees = angle;

        if (Math.Abs(angle) > MaxDeskewDegrees)
        {
            result.Warnings.Add($"Skew too large ({angle:F2}°, {source} estimate). Not auto-correcting.");
            return src;
        }

        if (Math.Abs(angle) < 0.1)
            return src; // No meaningful skew.

        var center = new Point2f(src.Cols / 2f, src.Rows / 2f);
        using var rotMat = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(src, rotated, rotMat, src.Size(), InterpolationFlags.Cubic,
            BorderTypes.Constant, Scalar.White);

        result.WasDeskewed = true;
        result.AppliedCorrectionDegrees = angle;
        return rotated;
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

    // ───────────── BINARIZATION ─────────────

    /// <summary>Applies Sauvola binarization if enabled, and reports what happened via
    /// <paramref name="result"/> — same never-throws, degrade-to-unchanged shape as
    /// <see cref="TryApplyDewarp"/>. Runs last in the pipeline (after QC, see
    /// <see cref="ProcessSinglePage"/>) since blur/exposure scores are meaningless once
    /// thresholded to pure black-and-white.</summary>
    private Mat TryApplyBinarization(Mat src, ProcessingResult result, bool binarizeEnabled, int dpi)
    {
        if (!binarizeEnabled) return src;
        try
        {
            var binarized = ApplySauvolaBinarization(src, dpi);
            result.WasBinarized = true;
            result.Warnings.Add("Binarized to black-and-white (Sauvola local threshold).");
            return binarized;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Binarization failed: {ex.Message}");
            return src;
        }
    }

    /// <summary>Sauvola local-adaptive thresholding — the same algorithm Tesseract/Leptonica
    /// switched to for their own best-quality binarization mode, chosen over a plain global
    /// Otsu threshold specifically because it survives uneven lighting/shadow across a page (a
    /// global threshold picks one cutoff for the whole image and fails wherever local
    /// brightness drifts from it — exactly the low-contrast/uneven-lit condition this rig's
    /// live view tends to show). threshold(x,y) = mean(x,y) * (1 + k * (stddev(x,y)/R - 1)),
    /// k=0.34 and R=128 being Sauvola's own recommended defaults (also Leptonica's). Local
    /// mean/stddev are computed per-pixel over a window in O(1) via integral images
    /// (summed-area tables for the image and its square) rather than a naive per-pixel window
    /// scan, which would be far too slow at full capture resolution.
    ///
    /// Three additions over a textbook Sauvola pass, all aimed at the "noisy black dots"
    /// failure mode the literature documents for exactly this algorithm on camera-captured
    /// (not flatbed-scanned) photos:
    /// 1. A light median pre-blur suppresses isolated sensor/JPEG noise pixels before
    ///    thresholding, without softening stroke edges the way a Gaussian blur would.
    /// 2. Near-uniform-variance ("flat") regions — where Sauvola's own formula is documented
    ///    to become numerically unstable and speckle — fall back to a single global Otsu cutoff
    ///    instead of the local formula. Using Otsu's cutoff (not a hardcoded "always white")
    ///    correctly handles a flat dark region (a solid black-filled figure, say) the same as a
    ///    flat bright one.
    /// 3. A final connected-component despeckle pass removes isolated foreground specks too
    ///    small to be real strokes.</summary>
    private static Mat ApplySauvolaBinarization(Mat src, int dpi)
    {
        using var gray = new Mat();
        if (src.Channels() > 1) Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        else src.CopyTo(gray);

        using var denoised = new Mat();
        Cv2.MedianBlur(gray, denoised, 3);

        using var otsuDst = new Mat();
        var globalThreshold = Cv2.Threshold(denoised, otsuDst, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        // Window scales with resolution rather than a fixed pixel count — captures range from
        // a few hundred to several thousand pixels wide, and a fixed small window would be far
        // too fine-grained (noisy per-pixel thresholds) on a large image.
        var longEdge = Math.Max(denoised.Cols, denoised.Rows);
        var windowRadius = Math.Clamp(longEdge / 160, 7, 35);

        using var sum = new Mat();
        using var sqSum = new Mat();
        Cv2.Integral(denoised, sum, sqSum, MatType.CV_64F);

        denoised.GetArray(out byte[] pixels);
        sum.GetArray(out double[] sumData);
        sqSum.GetArray(out double[] sqSumData);

        var rows = denoised.Rows;
        var cols = denoised.Cols;
        var sumStride = cols + 1;
        var output = new byte[rows * cols];

        const double k = 0.34;
        const double r = 128.0; // Expected dynamic range of local standard deviation.
        // Below this local standard deviation there's essentially no local contrast/edge —
        // Sauvola's formula collapses toward mean*(1-k) regardless of genuine content there,
        // which is the documented root cause of speckle in near-uniform background regions.
        const double flatRegionStdDevFloor = 3.0;

        for (var y = 0; y < rows; y++)
        {
            var y0 = Math.Max(0, y - windowRadius);
            var y1 = Math.Min(rows - 1, y + windowRadius);
            var rowOffset = y * cols;
            for (var x = 0; x < cols; x++)
            {
                var x0 = Math.Max(0, x - windowRadius);
                var x1 = Math.Min(cols - 1, x + windowRadius);

                var area = (double)(x1 - x0 + 1) * (y1 - y0 + 1);
                var s = IntegralBoxSum(sumData, sumStride, x0, y0, x1, y1);
                var sq = IntegralBoxSum(sqSumData, sumStride, x0, y0, x1, y1);

                var mean = s / area;
                var variance = Math.Max(0, sq / area - mean * mean);
                var stdDev = Math.Sqrt(variance);

                if (stdDev < flatRegionStdDevFloor)
                {
                    output[rowOffset + x] = pixels[rowOffset + x] > globalThreshold ? (byte)255 : (byte)0;
                    continue;
                }

                var threshold = mean * (1 + k * (stdDev / r - 1));
                output[rowOffset + x] = pixels[rowOffset + x] > threshold ? (byte)255 : (byte)0;
            }
        }

        using var beforeDespeckle = new Mat(rows, cols, MatType.CV_8UC1);
        beforeDespeckle.SetArray(output);
        return DespeckleBinary(beforeDespeckle, dpi);
    }

    /// <summary>Removes isolated foreground specks from a bilevel (0/255) image via connected-
    /// component filtering — the literature-recommended approach over median/morphological
    /// filtering specifically because it preserves genuine thin strokes while dropping isolated
    /// noise pixels, since it filters by connected blob size rather than a fixed spatial
    /// footprint. The area floor scales with DPI (~4px² at 300 DPI) since "how many pixels make
    /// up a real stroke fragment" is a resolution-relative question, not an absolute one.</summary>
    private static Mat DespeckleBinary(Mat bilevel, int dpi)
    {
        using var inverted = new Mat();
        Cv2.BitwiseNot(bilevel, inverted); // Our convention: ink=0/white=255 — ConnectedComponents treats non-zero as foreground.

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var labelCount = Cv2.ConnectedComponentsWithStats(inverted, labels, stats, centroids, PixelConnectivity.Connectivity8);

        var scale = dpi / 300.0;
        var minBlobPixels = Math.Max(2, (int)Math.Round(4.0 * scale * scale));

        var keep = new bool[labelCount];
        for (var label = 0; label < labelCount; label++)
            keep[label] = label == 0 || stats.At<int>(label, (int)ConnectedComponentsTypes.Area) >= minBlobPixels;

        labels.GetArray(out int[] labelData);
        bilevel.GetArray(out byte[] pixels);
        var outputPixels = (byte[])pixels.Clone();
        for (var i = 0; i < labelData.Length; i++)
            if (!keep[labelData[i]]) outputPixels[i] = 255;

        var result = new Mat(bilevel.Rows, bilevel.Cols, MatType.CV_8UC1);
        result.SetArray(outputPixels);
        return result;
    }

    /// <summary>Box sum over the inclusive pixel rect [x0,x1]x[y0,y1] from an OpenCV integral
    /// image's flat row-major data (one row/column larger than the source on each side, per
    /// <see cref="Cv2.Integral(InputArray, OutputArray, OutputArray, int, int)"/>'s
    /// convention) — the standard four-corner summed-area-table lookup.</summary>
    private static double IntegralBoxSum(double[] integral, int stride, int x0, int y0, int x1, int y1)
    {
        var a = integral[y0 * stride + x0];
        var b = integral[y0 * stride + (x1 + 1)];
        var c = integral[(y1 + 1) * stride + x0];
        var d = integral[(y1 + 1) * stride + (x1 + 1)];
        return d - b - c + a;
    }
}
