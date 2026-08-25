using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using BitMiracle.LibTiff.Classic;
using MicroCapture.Core.Models;
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

/// <summary>One operator-drawn fixed-frame rectangle, in the pixel space of the image it was
/// authored against (see Batch.FixedFrameImageWidth/Height) — the live-view feed for frames
/// drawn directly on the live view, or a full-resolution calibration shot for batches set up
/// before live-view editing existed. Either way that reference resolution is what
/// <see cref="ImageProcessor.ProcessFixedFrames"/> projects from when it crops a capture, so the
/// two need never match. Unlike <see cref="CropPoint"/> quads, fixed frames are always
/// axis-aligned — no perspective correction, since they exist for a stationary, straight-down
/// copy-stand shot.</summary>
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

/// <summary>One fixed frame's live-view measurement: how in-focus that region is, and a 24x24
/// grayscale content signature of it (same convention as <see cref="LiveFrameCheck"/>, so the
/// caller's content-difference comparison carries over unchanged). A zeroed value means the
/// region was degenerate or off-image and should be treated as "no information".</summary>
public readonly record struct RegionCheck(double Sharpness, byte[]? ContentSignature);

/// <summary>Result of measuring every fixed frame against one live-view frame — see
/// <see cref="ImageProcessor.CheckLiveRegions"/>. <see cref="Regions"/> is index-aligned with
/// the regions passed in, so a caller can map results straight back onto its frame list.</summary>
public readonly record struct LiveRegionsCheck(bool Decoded, int ImageWidth, int ImageHeight, RegionCheck[] Regions)
{
    public static readonly LiveRegionsCheck None = default;
}

/// <summary>
/// Core image processing pipeline: auto-crop, deskew, perspective correction,
/// enhancement, and QC scoring. All operations preserve the original file.
/// </summary>
public partial class ImageProcessor
{
    // --- Configuration ---
    // The DPI at which a capture's pixel dimensions are left unresampled when no per-rig
    // CameraCalibration measurement exists yet (see MeasuredDpi's fallback below) — used as
    // measuredDpi's default in ResizeForDpi's scale = targetDpi / measuredDpi. This used to be
    // a placeholder 150 with no physical basis, which produced pages roughly 2x their real
    // physical size (confirmed: an 11.75" magazine page came out measuring ~22.3" at the
    // selected DPI). Now set from an actual physical measurement of the rig — an 11.75" ruler
    // captured 3343px tall at 150 DPI selected (i.e. unresampled, scale=1.0 under the old
    // constant): 3343 / 11.75 ≈ 284.5 DPI, rounded to 285. This is still just a fallback for
    // when no CameraCalibration row exists — a real calibration (LensCalibrationViewModel's
    // Measure DPI flow) always takes precedence via MeasuredDpi, and should be preferred for any
    // rig this specific measurement doesn't describe (different lens/distance/camera).
    public const int BaselineDpi = 285;

    /// <summary>Real, physically-measured pixels-per-inch for the rig a job's batch was
    /// captured under, derived from <see cref="CameraCalibration.TargetWidthInches"/>/
    /// <see cref="CameraCalibration.TargetHeightInches"/> (the operator-entered known physical
    /// size of a calibration target) and <see cref="CameraCalibration.MeasuredPixelWidth"/>/
    /// <see cref="CameraCalibration.MeasuredPixelHeight"/> (how many pixels that target spanned
    /// in a captured frame) — averaged across width and height for a single scalar DPI. This
    /// replaces the old assumption that BaselineDpi (150) was itself a real captured resolution;
    /// it never was (no sensor size/capture distance/lens geometry was ever measured), which is
    /// the confirmed root cause of processed pages coming out roughly 2x (or worse) their real
    /// physical size versus what the DPI tag claimed.
    ///
    /// Falls back to <see cref="BaselineDpi"/> when <paramref name="calib"/> is null or any of
    /// the four calibration fields is missing/non-positive — i.e. this rig has never been
    /// physically DPI-calibrated. That fallback keeps the pipeline running (never throws/blocks
    /// a capture), but the resulting DPI is only ever a label in that case, not a measured
    /// physical fact — exactly the pre-existing (uncalibrated) behavior, preserved as a documented
    /// degrade path rather than a silent one.</summary>
    // Sanity bounds on a MeasuredDpi result: below MinPlausibleDpi/above MaxPlausibleDpi almost
    // certainly means a fat-fingered calibration entry (e.g. inches typed where the field meant
    // something else, or an accidental decimal-point slip) rather than a real physical
    // measurement — no real copy-stand rig measures under 20 or over 4x the highest selectable
    // AvailableDpiOptions value. Falling back to BaselineDpi in that case keeps the pipeline from
    // computing an extreme resample scale off a value nobody actually intended.
    private const double MinPlausibleDpi = 20.0;
    private const double MaxPlausibleDpi = 4800.0;

    public static double MeasuredDpi(CameraCalibration? calib)
    {
        if (calib is { TargetWidthInches: > 0, TargetHeightInches: > 0, MeasuredPixelWidth: { } pw, MeasuredPixelHeight: { } ph })
        {
            var dpiX = pw / calib.TargetWidthInches.Value;
            var dpiY = ph / calib.TargetHeightInches.Value;
            var measured = (dpiX + dpiY) / 2.0;
            if (measured is >= MinPlausibleDpi and <= MaxPlausibleDpi)
                return measured;
        }
        return BaselineDpi;
    }

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
    // A real spine sits *between* two page halves, so the darkest column found in
    // DetectGutter's central search band should have genuine bright page content flanking it
    // on both sides within that same band. Without this, a single page shot at an angle whose
    // own left/right edge (against the dark backdrop) happens to fall inside the search band
    // reads as a fake high-confidence "gutter": the darkest column lands right at the search
    // band's own boundary (a one-sided dark-to-bright transition, not an interior dip), while
    // the bright page filling the rest of the band inflates windowAvg enough to still score a
    // large relative drop. Confirmed on a real fixture (Trapezoid_Image003.JPG, a single page
    // photographed at a steep angle): minIdx landed exactly on searchStart at confidence 0.92,
    // and the resulting "left half" split out was 100% black background, no page content at
    // all. All 7 genuine real-fixture spreads have their true spine at least ~800px inside the
    // search band on both sides — comfortably clearing a 5% margin — so this only rejects
    // boundary-riding false positives, not real interior dips.
    public double GutterMinFlankMarginFraction { get; set; } = 0.05;
    // A residual distortion distinct from both global rotation (one angle for the whole page)
    // and book-curve dewarp (per-line horizontal bulge): each text line's own left margin
    // drifts sideways as a near-linear function of its vertical position — a shear, not a
    // rotation or a curve. Confirmed on a real photo (Trapezoid_Image001's right half): text-
    // line angles were too inconsistent (high IQR) to trust a single global rotation, which is
    // exactly what TryDeskew's own sanity gate correctly detected — but the *left margin's own
    // x-position*, tracked line-by-line, showed a clean ~2° trend the angle-based method
    // couldn't see because a pure shear leaves each individual line's own slope near zero (only
    // its horizontal position moves), so it hides from an angle-only estimator. Needs more line
    // support than global-angle deskew (MinShearLines vs. the 3-line minimum above) because
    // fitting a trend, not a median, is more exposed to indentation/short-line noise.
    public int MinShearLines { get; set; } = 8;
    // Theil-Sen residual tolerance (px) for a line to count as following the fitted margin
    // trend rather than being paragraph-indentation/short-line noise.
    public double ShearInlierPx { get; set; } = 15.0;
    public double MinShearInlierFraction { get; set; } = 0.6;
    // Below this, the implied lean isn't worth correcting (matches ApplyDeskewAngle's own 0.1°
    // no-op floor, raised because a shear fit is noisier evidence than a direct angle median).
    public double MinShearCorrectionDegrees { get; set; } = 0.3;
    // Sanity clamp mirroring MaxDeskewDegrees — a fitted trend implying more lean than this is
    // more likely a bad fit than a real physical shear this severe.
    public double MaxShearCorrectionDegrees { get; set; } = 10.0;
    // A margin shear (above) only moves *where* a line sits — it cannot change a line's own
    // orientation, because it never adjusts Y as a function of X. Confirmed necessary on a real
    // photo (Trapezoid_Image001's right half): even after both boundary-curve rectification and
    // margin-shear correction, individual text lines were still visibly tilted, and by a
    // *different* amount near the top than near the bottom — a rotation that itself varies with
    // vertical position, which needs a real 2D field (Y depends on both X and Y), not a shear.
    public int MinRotationFieldLines { get; set; } = 8;
    // Theil-Sen residual tolerance for a line's own fitted slope (dy/dx, not degrees) to count
    // as following the fitted rotation-vs-Y trend. ~0.03 is about 1.7°.
    public double RotationFieldInlierSlope { get; set; } = 0.03;
    // Lower than MinShearInlierFraction (0.6) — individual per-line slope fits are noisier
    // evidence than a line's own left-edge position (more exposed to font/glyph irregularity),
    // so demanding the same majority is unrealistic. Starting point pending validation.
    public double MinRotationFieldInlierFraction { get; set; } = 0.5;
    // Below this implied top-to-bottom span, the trend isn't worth correcting.
    public double MinRotationFieldRangeDegrees { get; set; } = 0.5;
    // Sanity clamp mirroring MaxShearCorrectionDegrees.
    public double MaxRotationFieldRangeDegrees { get; set; } = 10.0;
    // Otsu's own between-class/total-variance separability score for the brightness-based
    // foreground segmentation pass (FindContoursByBrightness) — below this, the split isn't a
    // real bimodal "bright page on dark backdrop" separation (e.g. a light-colored desk, or a
    // uniformly-lit scene with no real backdrop), and the pass should contribute nothing rather
    // than trust a meaningless threshold. Starting point pending validation against real
    // fixtures.
    public double BrightnessSeparationMinScore { get; set; } = 0.35;
    // YCrCb skin-tone range (Chai & Ngan) used to exclude a hand holding the page from the
    // brightness-segmented foreground blob, so the detected page shape isn't extended/distorted
    // by skin pixels that are similarly bright to the page itself. Deliberately generous —
    // this only needs to keep the detected quad's shape sane, not achieve pixel-perfect hand
    // removal (that's the separate, not-yet-built finger-removal/inpainting feature). Starting
    // point pending validation against real fixtures.
    public double SkinCrLow { get; set; } = 135.0;
    public double SkinCrHigh { get; set; } = 180.0;
    public double SkinCbLow { get; set; } = 85.0;
    public double SkinCbHigh { get; set; } = 135.0;
    // Perpendicular distance (pixels) from an approximate quad edge within which a Canny edge
    // pixel is considered evidence for that edge's true line, in RefineQuadCorners. Starting
    // point pending validation against real fixtures.
    public double CornerRefinementBandPx { get; set; } = 20.0;
    // Minimum edge-pixel inliers required before trusting a refined line fit for one corner;
    // below this, that corner keeps its original (unrefined) position rather than fitting a
    // line from too little evidence. Mirrors DetectDewarpCurve's own >= 8 samples bar.
    public int CornerRefinementMinInliers { get; set; } = 8;
    // A single straight line per edge (RefineQuadCorners/CornerRefinementBandPx above) can only
    // ever approximate a genuinely curved or trapezoidal edge — it can't reproduce one. This
    // family of tunables governs a stronger alternative: fit each of the quad's 4 edges as its
    // own curve (a degree-2 polynomial across many sample points along that edge, not just its
    // 2 endpoints), then map all 4 fitted edges directly onto the output rectangle's edges via
    // boundary (Coons-patch) interpolation — same physical idea as RefineQuadCorners, just not
    // limited to a straight line. Confirmed necessary on a real photo (Trapezoid_Image001's
    // right half): even after straight-line corner refinement, the page was still visibly
    // slanted, because its true edges are not straight lines under this camera angle.
    // Wider band than corner refinement's — a curve fit needs points spread along the *whole*
    // edge, not just near 2 endpoints, so it must tolerate more perpendicular scatter to gather
    // enough of them.
    public double BoundaryCurveBandPx { get; set; } = 30.0;
    // Needs more support than a 2-point line (CornerRefinementMinInliers) — fitting a degree-2
    // curve from too few points overfits noise into a fake bend. Starting point pending
    // validation against real fixtures.
    public int BoundaryCurveMinInliers { get; set; } = 24;
    // A fitted edge curve implying a perpendicular bend larger than this fraction of the
    // page's own relevant dimension is more likely an overfit to noise than a real physical
    // page edge — reject and fall back rather than trust it. 20% is a generous ceiling: the
    // real defect this was built for (Trapezoid_Image001) implied roughly 3-5%.
    public double BoundaryCurveMaxOffsetFraction { get; set; } = 0.2;
    // Below this, a fitted edge's "bend" is indistinguishable from Canny edge-point jitter
    // (the band search itself tolerates up to BoundaryCurveBandPx=30px of scatter) — not a
    // real correction worth paying for. Matters most for an already-cropped page (e.g. the
    // second TryAutoCrop pass an auto-split half gets inside ProcessSinglePage): re-fitting
    // curves against fresh edges of an already-rectified page can "find" a few noise pixels
    // of fake bend and pay for a whole extra Cv2.Remap pass to correct essentially nothing.
    public double BoundaryCurveNegligibleOffsetPx { get; set; } = 2.0;
    // Boundary-curve rectification (above) guarantees the page's *outer* rectangle comes out
    // straight — it does not guarantee interior content does too, because a Coons-patch blend
    // of only 4 edge curves can't represent every real page's actual per-line distortion.
    // Confirmed by the user's own annotated screenshot: straight reference lines drawn over the
    // rectified output show text still visibly bowing away from them, on every real fixture
    // tested. This family of tunables governs the user's own proposed fix, applied literally:
    // detect every text line's own actual shape (reusing the same evidence
    // DetectDewarpCurve/TryDeskew already collect) and flatten each one individually to its own
    // median row — "no change in Y across a line's own width" — rather than trusting a smooth
    // blend of a handful of curves to get the interior right too. See TryApplyLineMesh.
    // Needs more lines than the coarse dewarp curve fit's minimum (3) — a sparse mesh leaves
    // large unsupported gaps between anchor lines that a blend would have to paper over anyway,
    // defeating the point of doing this per-line instead.
    public int MinMeshLines { get; set; } = 6;
    // Matches the sample-count bar used everywhere else a single line's own edge is fit
    // (FitAveragedCurve, TryDeskewRotationField) — fewer points makes a single line's own shape
    // too noisy to trust as a flattening target.
    public int MeshLineMinSamples { get; set; } = 8;
    // Same degree as the coarse top/bottom curve fit (FitAveragedCurve) — this pass reuses that
    // exact "cubic sheet" assumption, just pooling every detected line's own residual instead of
    // only the topmost/bottommost line's.
    public int MeshCurveDegree { get; set; } = 3;
    // Column-averaging window (each side) for SampleLineCentroid's ink-centroid signal — wide
    // enough to average out single-glyph-scale jitter (roughly a character's width or a bit
    // more), narrow enough to still track real curvature that develops gradually across a line
    // rather than smoothing it away too.
    public int MeshCentroidSmoothWindowPx { get; set; } = 20;
    // A line implying more correction than this fraction of the page's own height is more
    // likely a false "line" detection (a photo, a rule, a table border) than real residual
    // curvature this late in the pipeline (after boundary rectification and the coarse dewarp
    // curve have already run) — decline the whole mesh rather than trust it.
    public double MeshMaxDisplacementFraction { get; set; } = 0.06;
    // Phase-2 item from the user's original three-item request, deferred until the auto-split
    // and keystone/curve work above was done and reviewed: automatic finger/hand removal via
    // inpainting. Reuses SkinCrLow/High, SkinCbLow/High (above) for color classification — same
    // physical skin-tone range boundary detection already uses, just consumed directly instead
    // of dilated-and-inverted. See TryRemoveFingers for why a candidate must touch the page's
    // own outer edge: a printed photo of a person (e.g. a real fixture — a magazine spread with
    // a photo of swimmers) is skin-toned but fully interior, never touching the crop boundary,
    // while a real finger holding the book physically enters the frame from outside it. Area-
    // gated on both ends: too small is noise, too large is more likely a mis-classified skin-
    // toned interior photo/cover than a real finger.
    public double FingerMinAreaFraction { get; set; } = 0.0015;
    public double FingerMaxAreaFraction { get; set; } = 0.05;
    // How close a candidate blob's bounding rect must sit to the page's own outer edge to count
    // as "entering from outside" rather than an interior skin-toned false positive. Small and
    // exact on purpose — a real leftover finger sliver directly abuts the crop boundary; a
    // generous margin here would start admitting interior content again.
    public int FingerEdgeTouchMarginPx { get; set; } = 4;
    // Grown a little past the raw skin-color boundary before inpainting — real fingers have a
    // soft, sometimes motion-blurred edge in a real photo, and the classifier's own boundary
    // rides right at the color transition rather than past it.
    public int FingerMaskDilatePx { get; set; } = 6;
    // Cv2.Inpaint's own neighborhood radius (px) — the standard default for Telea inpainting on
    // a several-thousand-pixel-wide document photo; a document's background is locally flat
    // (blank margin/page color) right where a finger typically intrudes, so inpainting doesn't
    // need to reconstruct fine texture, just extend the surrounding flat tone inward.
    public double FingerInpaintRadiusPx { get; set; } = 8.0;
    // Second half of the same deferred Phase-2 request: bleedthrough/show-through suppression —
    // faint mirrored content from the reverse side of a thin page ghosting through, confirmed on
    // two real fixtures (Trapezoid_Image002.JPG, a magazine spread; IMG_0022.JPG). Unlike the
    // geometric fixes above, this is a genuinely lossy, judgment-call content change (it can
    // erase legitimately faint real content — a light watermark, pencil annotation, faded
    // historical ink — not just ghosting), so unlike auto-crop/dewarp/mesh/finger-removal it is
    // gated behind an explicit opt-in flag (bleedthroughEnabled, mirroring binarizeEnabled's own
    // pattern) rather than always attempted. Algorithm: estimate each pixel's local background
    // via a wide Gaussian blur (same "illumination normalization" idea already used in
    // FindDocumentContourPasses, just reused for the opposite purpose — here the blur radius is
    // deliberately wide enough to treat individual letter strokes as foreground, not background),
    // then soft-threshold each pixel's depth below that background: shallow depth (typical of
    // bleedthrough, which is heavily attenuated by the paper's own opacity) gets pulled back
    // toward the background/erased, deep depth (real ink) is left alone. A soft (linear ramp)
    // threshold, not a hard cutoff, so borderline pixels degrade gently instead of a visible
    // hard edge appearing where the classification flips.
    //
    // Confirmed effective on real grayscale text bleed-through (IMG_0022.JPG right half, a
    // mirrored ghost of the reverse page's own printed text/diagram): visibly fainter after,
    // real ink alongside it unchanged. Confirmed NOT effective on Trapezoid_Image002.JPG's
    // colored graphic bleed-through (a compass/map image showing through glossy magazine
    // stock) — that's a different physical phenomenon (translucent color transfer through the
    // sheet, not localized ink absorption), sits at a shallower, more uniform depth this
    // per-pixel local-contrast technique doesn't isolate well. This method addresses text/ink
    // show-through specifically, not colored-image ghosting — a real, known gap, not yet solved.
    public double BleedthroughBlurSigmaFraction { get; set; } = 0.03;
    public double BleedthroughBlurSigmaMinPx { get; set; } = 25.0;
    // Depth (0-255 gray levels) below which content is treated as pure bleedthrough/noise and
    // fully suppressed. Deliberately conservative (low) — this runs on every pixel of every page
    // when enabled, so a value too high would start eating legitimately faint real content;
    // real bleedthrough measured on the confirmed fixtures above sits well under this.
    // Retuned from an initial (10/35) guess against real measured depth data: on a confirmed
    // real fixture (IMG_0022.JPG right half), the ghost region's own depth distribution (median
    // 6.8, 90th percentile 21.8) and a real-ink text region's (median 18.4, 75th percentile
    // 68.8) overlap substantially at the low end — thin/anti-aliased stroke-edge pixels read at
    // similar depth to bleedthrough — so no threshold here fully separates them. These values
    // suppress most of the confirmed ghost's mass while leaving the *bulk* of real ink (which
    // sits far higher, 68+) untouched; some thinning of real text's own edge pixels is an
    // accepted, visible trade-off, not eliminated.
    public double BleedthroughSuppressLowDepth { get; set; } = 15.0;
    // Depth above which content is certainly real ink and is left fully unchanged. Between low
    // and high, suppression ramps linearly rather than cutting sharply.
    public double BleedthroughSuppressHighDepth { get; set; } = 45.0;
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

    /// <summary>Per-region live-view check driving auto-capture in fixed-frame mode. Unlike
    /// <see cref="CheckLiveFrame"/> there is deliberately NO boundary/contour requirement: the
    /// operator's drawn frames already ARE the region of interest, and a frame may legitimately
    /// cover a sub-region with no clean detectable edge at all. So this only measures, per
    /// region, how sharp it is and what its content looks like.
    ///
    /// <para>Sharpness is computed over each region's own sub-image rather than the whole frame
    /// (which is what <see cref="CheckLiveFrame"/> does) — otherwise one out-of-focus frame
    /// sitting beside a sharp one would pass the focus gate on the sharp one's score.</para>
    ///
    /// <para>Regions are given as FRACTIONS of the frame (0..1), not pixels, so the caller never
    /// has to know the live stream's pixel dimensions — which differ from the space its frames
    /// are stored in whenever a batch was calibrated at full resolution — and so a stream that
    /// changes resolution mid-session degrades gracefully instead of silently misaligning.</para>
    ///
    /// <para>Signatures use the same 24x24 grayscale convention as <see cref="CheckLiveFrame"/>,
    /// so the caller's existing content-difference comparison and its threshold carry over
    /// unchanged.</para></summary>
    public static LiveRegionsCheck CheckLiveRegions(byte[] encodedImage, FixedFrameRect[] regionFractions)
    {
        try
        {
            if (regionFractions.Length == 0) return LiveRegionsCheck.None;

            using var image = Cv2.ImDecode(encodedImage, ImreadModes.Grayscale);
            if (image.Empty()) return LiveRegionsCheck.None;

            var regions = new RegionCheck[regionFractions.Length];
            for (var i = 0; i < regionFractions.Length; i++)
            {
                regions[i] = MeasureRegion(image, regionFractions[i]);
            }

            return new LiveRegionsCheck(true, image.Width, image.Height, regions);
        }
        catch
        {
            // Same advisory contract as CheckLiveFrame — never interrupt the live stream.
            return LiveRegionsCheck.None;
        }
    }

    /// <summary>Measures one fractional sub-region's focus and content signature. A degenerate
    /// or off-image region yields a zeroed <see cref="RegionCheck"/> rather than throwing, so
    /// one bad frame can't blind the caller to the others.</summary>
    private static RegionCheck MeasureRegion(Mat image, FixedFrameRect fraction)
    {
        try
        {
            var x = (int)Math.Round(fraction.X * image.Width);
            var y = (int)Math.Round(fraction.Y * image.Height);
            var w = (int)Math.Round(fraction.Width * image.Width);
            var h = (int)Math.Round(fraction.Height * image.Height);

            var safeX = Math.Clamp(x, 0, Math.Max(0, image.Width - 1));
            var safeY = Math.Clamp(y, 0, Math.Max(0, image.Height - 1));
            var safeW = Math.Clamp(w, 1, image.Width - safeX);
            var safeH = Math.Clamp(h, 1, image.Height - safeY);
            if (safeW < 2 || safeH < 2) return default;

            using var region = new Mat(image, new Rect(safeX, safeY, safeW, safeH));

            using var laplacian = new Mat();
            Cv2.Laplacian(region, laplacian, MatType.CV_64F);
            Cv2.MeanStdDev(laplacian, out _, out var stddev);
            var sharpness = stddev.Val0 * stddev.Val0;

            using var thumb = new Mat();
            Cv2.Resize(region, thumb, new Size(24, 24), 0, 0, InterpolationFlags.Area);
            thumb.GetArray(out byte[] pixels);

            return new RegionCheck(sharpness, pixels);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Run the full processing pipeline on a captured image.
    /// Original file is never modified. A processed derivative is created.
    /// </summary>
    public ProcessingResult Process(string inputPath, string outputDirectory, bool splitPages = false, bool manualOverride = false, string? leftCrop = null, string? rightCrop = null, TiffMetadata? metadata = null, bool dewarpEnabled = false, string? dewarpCurve = null, bool dewarpManualOverride = false, bool binarizeEnabled = false, LensCalibration? lensCalibration = null, bool bleedthroughEnabled = false, bool hasManualAdjustments = false, int rotationDegrees = 0, bool flipHorizontal = false, bool flipVertical = false, double brightness = 0, double contrast = 0, double saturation = 0, double sharpness = 0, double whiteBalance = 0, double measuredDpi = BaselineDpi, string captureFormat = "TIFF", string? outputFileNameOverride = null)
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

            // Canonical pipeline (see AltBoundaryPipeline.cs): Method 4 (Gx sign-change count +
            // gutter-anchored span + RANSAC-guided continuity walk + finger bridging +
            // strength/straightness widen-retry) side-edge detection, combined with the
            // top/bottom continuity trace + gutter-notch split + cubic-bow single-pass remap —
            // this is the only automatic (non-manual-override) boundary/dewarp path now; the
            // old contour/confidence-based TryAutoCrop + TryDeskew + TryApplyDewarp +
            // TryApplyLineMesh chain (still used below for the manualOverride path, where the
            // operator's own drawn quad IS the boundary and there's nothing left to
            // auto-detect) chained up to 5 sequential lossy Cubic-interpolation resamples —
            // crop, up to 2 deskew sub-corrections, book-curve dewarp, and an unconditional
            // line-mesh pass — each compounding blur on the last. Method 4's flatten does ALL
            // geometric correction (crop rectification + curve straightening + width/perspective
            // correction + tilt fix) in exactly ONE Cv2.Remap call with InterpolationFlags.Linear,
            // matching the notebook exactly — this is the fix for that compounding-blur behavior.
            //
            // The spine-shadow gutter signal (DetectGutter) still decides split-vs-single: this
            // is a real, validated signal (see its own doc comment — confirmed against the
            // operator's real photo fixtures, all 4 genuine spreads scored gutter confidence
            // 0.23-0.79 and the one genuine single trapezoid page scored 0.05, cleanly on either
            // side of GutterConfidenceThreshold) and the notebook itself never covers this
            // decision (it only ever processes already-split single-page images), so there is no
            // notebook behavior to defer to here — kept exactly as before rather than re-derived.
            // Skipped entirely under manualOverride: an operator who already reviewed the crop
            // in Crop Review made an explicit choice that shouldn't be second-guessed.
            //
            // dewarpEnabled (the "Book Curve Correction" checkbox) gates the ENTIRE Method 4
            // pipeline here — boundary detection, spread splitting, and curve-straightening are
            // one inseparable operation in AltFlattenSpread/AltFlattenSinglePage, so there is no
            // way to keep splitting while skipping the curve remap. Unchecked means the raw
            // captured frame passes straight to FinishPageProcessing (finger removal,
            // bleedthrough, enhancement, binarize, DPI resample) with no crop, no split, no
            // dewarp — a two-page spread captured with the checkbox off comes out as a single
            // unsplit page, same as any other capture. This mirrors ProcessFixedFrames's
            // passthroughFixedFrames branch for how to build a ProcessingResult without Method
            // 4, except FinishPageProcessing still runs here (that branch skips it; this
            // shouldn't, since "no curve correction" isn't "no adjustments").
            if (!manualOverride && !dewarpEnabled)
            {
                using var passthroughFinished = FinishPageProcessing(src, result, binarizeEnabled, meta.Dpi, measuredDpi, bleedthroughEnabled, hasManualAdjustments, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);
                var outPassthrough = WritePageOutput(outputDirectory, Path.GetFileNameWithoutExtension(inputPath), passthroughFinished, meta, result.WasBinarized, captureFormat, singlePageInputPath: inputPath);
                result.OutputFilePaths.Add(outPassthrough);
                result.WasCropped = false;
                result.Success = true;
                return result;
            }

            if (!manualOverride)
            {
                var autoGutter = DetectGutter(src, GutterMinFlankMarginFraction);
                var autoSplit = splitPages
                    || (autoGutter.Confidence >= GutterConfidenceThreshold && autoGutter.AbsoluteDropLevels >= GutterAbsoluteDropThreshold);

                if (autoSplit)
                {
                    var altSpread = AltFlattenSpread(src);
                    using var leftFlat = altSpread.Left.Flattened;
                    using var rightFlat = altSpread.Right.Flattened;
                    // Method 4 doesn't expose a separate "found real edges" flag on its result
                    // the way the legacy TryAutoCrop path does (result.WasCropped, set only
                    // inside that method) — a split always produces a real geometric flatten of
                    // each half, so "differs from the pre-split half's own raw dimensions" is
                    // the honest available signal here.
                    result.WasCropped = leftFlat.Cols != src.Cols / 2 || leftFlat.Rows != src.Rows
                        || rightFlat.Cols != src.Cols / 2 || rightFlat.Rows != src.Rows;

                    using var leftFinished = FinishPageProcessing(leftFlat, result, binarizeEnabled, meta.Dpi, measuredDpi, bleedthroughEnabled, hasManualAdjustments, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);
                    var outLeft = WritePageOutput(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_1_left", leftFinished, meta, result.WasBinarized, captureFormat);
                    result.OutputFilePaths.Add(outLeft);

                    using var rightFinished = FinishPageProcessing(rightFlat, result, binarizeEnabled, meta.Dpi, measuredDpi, bleedthroughEnabled, hasManualAdjustments, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);
                    var outRight = WritePageOutput(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_2_right", rightFinished, meta, result.WasBinarized, captureFormat);
                    result.OutputFilePaths.Add(outRight);

                    if (!splitPages) result.Warnings.Add("Two-page spread auto-detected (spine shadow) — split into left/right pages automatically.");
                    result.Success = true;
                    return result;
                }
                else
                {
                    var altSingle = AltFlattenSinglePage(src);
                    using var flat = altSingle.Flattened;
                    // Same reasoning as the split branch above: "output dimensions differ from
                    // the raw source" is the honest, available signal for whether Method 4
                    // found and applied a real geometric correction versus degrading to an
                    // effective pass-through on a featureless/low-signal capture.
                    result.WasCropped = flat.Cols != src.Cols || flat.Rows != src.Rows;
                    using var finished = FinishPageProcessing(flat, result, binarizeEnabled, meta.Dpi, measuredDpi, bleedthroughEnabled, hasManualAdjustments, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);

                    var outPath = WritePageOutput(outputDirectory, Path.GetFileNameWithoutExtension(inputPath), finished, meta, result.WasBinarized, captureFormat, singlePageInputPath: inputPath);
                    result.OutputFilePaths.Add(outPath);
                    result.Success = true;
                    return result;
                }
            }

            // --- Manual-override path only, from here down: the operator's own saved crop
            // quad(s) already ARE the page boundary (drawn/confirmed in Crop Review), so there's
            // nothing for Method 4 to auto-detect — this keeps using WarpQuad + the legacy
            // deskew/dewarp/mesh touch-ups (ProcessSinglePage) exactly as before. manualOverride
            // is always true below this point (the automatic/non-override path already
            // returned above), so the old auto-two-page-contour branch that used to sit here is
            // gone — it only ever ran for a non-manual capture, which now goes through Method 4
            // instead (DetectTwoPageBoundaries is otherwise unused by this method now, but is
            // still called by the UI's own boundary lookups elsewhere in this file).
            var gutter = DetectGutter(src, GutterMinFlankMarginFraction);

            if (splitPages)
            {
                // Split logic. Each side is parsed to its 4 corners; a plain saved strip
                // (today's UI) is an axis-aligned quad and takes WarpQuad's cheap-crop path.
                Point2f[] leftCorners, rightCorners;
                if (!string.IsNullOrEmpty(leftCrop) && !string.IsNullOrEmpty(rightCrop))
                {
                    leftCorners = ParseCropCorners(leftCrop, src.Width, src.Height);
                    rightCorners = ParseCropCorners(rightCrop, src.Width, src.Height);
                }
                else
                {
                    // No saved manual quads yet for this split (e.g. Crop Review's split
                    // toggle was just turned on) — best-effort automatic gutter detection,
                    // falling back to an even 50/50 split when no confident spine shadow is
                    // found.
                    var splitX = gutter.Confidence >= GutterConfidenceThreshold
                        ? (int)Math.Round(src.Width * gutter.Fraction)
                        : src.Width / 2;
                    splitX = Math.Clamp(splitX, 1, src.Width - 1);
                    leftCorners = RectCorners(0, 0, splitX, src.Height);
                    rightCorners = RectCorners(splitX, 0, src.Width - splitX, src.Height);
                }

                // Process left — its own spine edge is this half's right edge (leftMat.Cols).
                using var leftMat = WarpQuad(src, leftCorners);
                var leftResult = ProcessSinglePage(leftMat, result, manualOverride, dewarpEnabled, savedDewarp, dewarpManualOverride, spineXHint: leftMat.Cols, binarizeEnabled, meta.Dpi, measuredDpi: measuredDpi, bleedthroughEnabled: bleedthroughEnabled, hasManualAdjustments: hasManualAdjustments, rotationDegrees: rotationDegrees, flipHorizontal: flipHorizontal, flipVertical: flipVertical, brightness: brightness, contrast: contrast, saturation: saturation, sharpness: sharpness, whiteBalance: whiteBalance);
                var outLeft = WritePageOutput(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_1_left", leftResult, meta, result.WasBinarized, captureFormat);
                result.OutputFilePaths.Add(outLeft);
                leftResult.Dispose();

                // Process right — its own spine edge is this half's left edge (x = 0).
                using var rightMat = WarpQuad(src, rightCorners);
                var rightResult = ProcessSinglePage(rightMat, result, manualOverride, dewarpEnabled, savedDewarp, dewarpManualOverride, spineXHint: 0, binarizeEnabled, meta.Dpi, measuredDpi: measuredDpi, bleedthroughEnabled: bleedthroughEnabled, hasManualAdjustments: hasManualAdjustments, rotationDegrees: rotationDegrees, flipHorizontal: flipHorizontal, flipVertical: flipVertical, brightness: brightness, contrast: contrast, saturation: saturation, sharpness: sharpness, whiteBalance: whiteBalance);
                var outRight = WritePageOutput(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_2_right", rightResult, meta, result.WasBinarized, captureFormat);
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

                var processed = ProcessSinglePage(manualCrop ?? src, result, manualOverride, dewarpEnabled, savedDewarp, dewarpManualOverride, binarizeEnabled: binarizeEnabled, dpi: meta.Dpi, measuredDpi: measuredDpi, bleedthroughEnabled: bleedthroughEnabled, hasManualAdjustments: hasManualAdjustments, rotationDegrees: rotationDegrees, flipHorizontal: flipHorizontal, flipVertical: flipVertical, brightness: brightness, contrast: contrast, saturation: saturation, sharpness: sharpness, whiteBalance: whiteBalance);
                manualCrop?.Dispose();

                // outputFileNameOverride lets a caller force a distinct output basename when
                // several jobs share the same inputPath (one fixed-frame source image processed
                // as N independent per-frame jobs — see MainWindowViewModel.CaptureAsync).
                // WritePageOutput's singlePageInputPath collision-avoidance naming derives its
                // name purely from inputPath, so without this override every sibling frame job
                // computes the exact same output filename and overwrites the previous frame's
                // output — leaving only the last-processed frame's image behind under every page
                // number (confirmed operator-visible bug: N distinct frames captured, exported
                // PDF repeats one image N times). Passing the override skips that inputPath-based
                // naming entirely in favor of the override name, which the caller has already
                // made unique per frame.
                var outPath = outputFileNameOverride != null
                    ? WritePageOutput(outputDirectory, outputFileNameOverride, processed, meta, result.WasBinarized, captureFormat)
                    : WritePageOutput(outputDirectory, Path.GetFileNameWithoutExtension(inputPath), processed, meta, result.WasBinarized, captureFormat, singlePageInputPath: inputPath);
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
    /// <param name="frameReferenceWidth">Width of the image <paramref name="fixedFramesSpec"/>'s
    /// coordinates were authored against (<c>Batch.FixedFrameImageWidth</c>) — the live-view feed
    /// for frames drawn directly on the live view, or a full-res calibration shot for batches
    /// calibrated before live-view editing existed. 0 means "unknown", which falls back to
    /// treating frame coordinates as direct capture pixels (the historical behavior).</param>
    /// <param name="frameReferenceHeight">Height counterpart of <paramref name="frameReferenceWidth"/>.</param>
    public ProcessingResult ProcessFixedFrames(string inputPath, string outputDirectory, string fixedFramesSpec, TiffMetadata? metadata = null, bool dewarpEnabled = false, string? dewarpCurve = null, bool dewarpManualOverride = false, bool binarizeEnabled = false, LensCalibration? lensCalibration = null, bool bleedthroughEnabled = false, bool hasManualAdjustments = false, int rotationDegrees = 0, bool flipHorizontal = false, bool flipVertical = false, double brightness = 0, double contrast = 0, double saturation = 0, double sharpness = 0, double whiteBalance = 0, bool passthroughFixedFrames = true, int frameReferenceWidth = 0, int frameReferenceHeight = 0, double measuredDpi = BaselineDpi, string captureFormat = "TIFF")
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

            // Frames are stored in the pixel space of whatever image they were authored against
            // (Batch.FixedFrameImageWidth/Height), which is NOT necessarily this capture's own
            // resolution — frames drawn on the live view are authored at live-feed size (~960px)
            // while the capture is full-res (~6000px), and even a calibration-shot batch can
            // differ if the batch shoots a different JPEG size than the calibration shot forced.
            // Project them onto THIS capture's real resolution before cropping.
            //
            // Scaling the array once, here, means both the passthrough crop below and the
            // Method 4 padded-search branch inherit correct geometry: FixedFrameSearchPadFraction
            // is a fraction OF THE FRAME, so the padding stays proportional automatically. This
            // is why the scale belongs at the array and not at ClampRectToBounds, which then
            // becomes a genuine rounding-slop safety net rather than the load-bearing step.
            //
            // src (not rawSrc) is the denominator: SafeUndistort is a remap that preserves
            // dimensions, so either way this is the resolution the crop rects land in. A zero
            // reference (batches saved before reference dims were recorded) degrades to the
            // historical behavior of treating frame coordinates as direct capture pixels.
            var frameScaleX = frameReferenceWidth > 0 ? (double)src.Cols / frameReferenceWidth : 1.0;
            var frameScaleY = frameReferenceHeight > 0 ? (double)src.Rows / frameReferenceHeight : 1.0;
            if (frameScaleX != 1.0 || frameScaleY != 1.0)
            {
                for (var i = 0; i < frames.Length; i++)
                {
                    frames[i] = new FixedFrameRect(
                        frames[i].X * frameScaleX, frames[i].Y * frameScaleY,
                        frames[i].Width * frameScaleX, frames[i].Height * frameScaleY);
                }
            }

            var padWidth = Math.Max(2, frames.Length.ToString(CultureInfo.InvariantCulture).Length);
            for (var i = 0; i < frames.Length; i++)
            {
                var rect = ClampRectToBounds(new Rect(
                    (int)Math.Round(frames[i].X), (int)Math.Round(frames[i].Y),
                    (int)Math.Round(frames[i].Width), (int)Math.Round(frames[i].Height)), src.Cols, src.Rows);

                var frameFileNameNoExt = $"{Path.GetFileNameWithoutExtension(inputPath)}_frame{(i + 1).ToString("D" + padWidth, CultureInfo.InvariantCulture)}";

                if (passthroughFixedFrames)
                {
                    // Fixed frames are a calibrated, operator-trusted capture region on a
                    // copy-stand rig — the operator's intent is "save exactly what's in this
                    // rectangle," not run boundary detection/crop/dewarp/enhancement on top of
                    // it. No Method 4, no FinishPageProcessing tail (finger removal,
                    // bleedthrough, CLAHE, sharpen, binarize) — just crop to the calibrated rect
                    // and resample to the target DPI so the output's pixel dimensions match its
                    // resolution tag (see ResizeForDpi/WriteTiff's own doc comments). Never
                    // binarized on this path, so captureFormat's TIFF/JPG choice always applies.
                    using var cropped = new Mat(src, rect).Clone();
                    using var resized = ResizeForDpi(cropped, meta.Dpi, measuredDpi);
                    var framePath = WritePageOutput(outputDirectory, frameFileNameNoExt, resized, meta, binarized: false, captureFormat);
                    result.OutputFilePaths.Add(framePath);
                    continue;
                }

                // Method 4 single-page flatten within a padded search region around the
                // calibrated rectangle — a fixed frame is always a single page (no per-shot
                // manual quad concept exists for a copy-stand rig), so this is always the
                // automatic-detection path, same as Process's own !manualOverride branch. The
                // calibrated rectangle may not exactly match the real page edge, so the trace
                // needs margin around it to find the true boundary (FixedFrameSearchPadFraction,
                // same convention the old searchRegion path used).
                var frameResult = new ProcessingResult { OriginalFilePath = inputPath };
                Mat flat;
                if (!dewarpEnabled)
                {
                    // Book Curve Correction off: skip Method 4 entirely, same as Process()'s
                    // own automatic-path skip-branch — take the calibrated rectangle as-is with
                    // no boundary search, no split, no curve-straighten.
                    flat = new Mat(src, rect).Clone();
                }
                else
                {
                    var padded = ClampRectToBounds(new Rect(
                        (int)Math.Round(rect.X - rect.Width * FixedFrameSearchPadFraction),
                        (int)Math.Round(rect.Y - rect.Height * FixedFrameSearchPadFraction),
                        (int)Math.Round(rect.Width * (1 + 2 * FixedFrameSearchPadFraction)),
                        (int)Math.Round(rect.Height * (1 + 2 * FixedFrameSearchPadFraction))), src.Cols, src.Rows);
                    var sub = new Mat(src, padded);
                    var altResult = AltFlattenSinglePage(sub);
                    // A featureless/low-signal capture (nothing for Method 4 to find anywhere in
                    // the padded search region — confirmed on a flat solid-color synthetic image)
                    // makes AltFlattenPage's own defensive fallback return an unrefined pass-through
                    // of the padded region, not the operator's actual calibrated rectangle — worse
                    // than just trusting the calibration directly, since it includes the extra
                    // search margin and none of the correction the padding exists to allow for. Fall
                    // back to a plain crop of the calibrated rect itself in that case; it's the
                    // rig's own known-good boundary, not a heuristic guess.
                    if (altResult.FoundRealEdges)
                    {
                        flat = altResult.Flattened;
                        sub.Dispose();
                    }
                    else
                    {
                        altResult.Flattened.Dispose();
                        sub.Dispose();
                        flat = new Mat(src, rect).Clone();
                    }
                }
                string detectedFramePath;
                using (flat)
                using (var finished = FinishPageProcessing(flat, frameResult, binarizeEnabled, meta.Dpi, measuredDpi, bleedthroughEnabled, hasManualAdjustments, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance))
                {
                    detectedFramePath = WritePageOutput(outputDirectory, frameFileNameNoExt, finished, meta, frameResult.WasBinarized, captureFormat);
                }
                result.OutputFilePaths.Add(detectedFramePath);

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

    /// <summary>Single-page (non-split, non-fixed-frame) output filename — now the bare capture
    /// basename (e.g. "{basename}.tif") instead of the old "{basename}_processed.tif", now that
    /// the derivative is written into the same folder as the (retained-until-export) original
    /// rather than a separate "Processed" subfolder: the suffix existed to disambiguate the two
    /// files sharing one folder, but the extension alone already does that in the common case
    /// (a camera-native .jpg/.cr2/.cr3 original vs. a .tif or .jpg processed output).
    ///
    /// Falls back to the old "_processed" suffix ONLY when the bare name would exactly collide
    /// with <paramref name="inputPath"/> itself — same basename AND same extension, e.g. a
    /// JPG-format capture with JPG-format processed output both landing on "{basename}.jpg" —
    /// since the original is deliberately left on disk until batch export and must never be
    /// silently overwritten by its own derivative. Split-page (_1_left/_2_right) and fixed-frame
    /// (_frameNN) naming is unaffected by this — those inherently produce multiple files per
    /// original and can't collapse to a bare basename regardless.</summary>
    private static string SinglePageOutputFileName(string inputPath, string targetExtension)
    {
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var bareName = baseName + targetExtension;
        var wouldCollide = string.Equals(bareName, Path.GetFileName(inputPath), StringComparison.OrdinalIgnoreCase);
        return wouldCollide ? baseName + "_processed" + targetExtension : bareName;
    }

    /// <summary>Writes a processed page as TIFF, tagging it with the batch's chosen DPI and
    /// Software/DateTime/Artist metadata so the file's own properties are correct instead of
    /// absent (Explorer/Photoshop default an untagged TIFF to 96 DPI, which has nothing to do
    /// with what was actually captured). This only sets metadata tags — <paramref name="mat"/>'s
    /// pixel dimensions are expected to already reflect the chosen DPI by the time this is
    /// called (see <see cref="ResizeForDpi"/>, applied upstream in every caller).
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

    /// <summary>Writes a processed page as JPEG — the per-capture-sticky alternative to
    /// <see cref="WriteTiff"/> (see <see cref="WritePageOutput"/>, which decides between the
    /// two). Quality 95 matches the existing SkiaSharp emergency-fallback path elsewhere in this
    /// class (the OpenCV-unavailable degrade path), kept consistent rather than picking a new
    /// number. Uses <see cref="Cv2.ImEncode"/> directly (the same OpenCV encode-to-bytes-then-
    /// write-file pattern this class's PNG debug dumps already use) since <paramref name="mat"/>
    /// is already an OpenCV Mat here — no need to bridge through SkiaSharp the way the raw-bytes
    /// emergency fallback does. JPEG has no equivalent of TIFF's Author/DateTime tags in the
    /// simple form <see cref="WriteTiff"/> sets them, so those are necessarily lost for JPG
    /// output — an accepted tradeoff of choosing JPG over TIFF, not an oversight. DPI is NOT
    /// similarly dropped, though: see <see cref="StampJfifDensity"/>, called below, which patches
    /// the standard JFIF density field OpenCV's encoder otherwise leaves unset.</summary>
    private static void WriteJpeg(string path, Mat mat, TiffMetadata metadata)
    {
        var jpegParams = new int[] { (int)ImwriteFlags.JpegQuality, 95 };
        if (!Cv2.ImEncode(".jpg", mat, out var bytes, jpegParams))
            throw new IOException($"Could not encode JPEG: {path}");
        StampJfifDensity(bytes, metadata.Dpi);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>PNG counterpart of <see cref="WriteJpeg"/> — same DPI-tag concern (OpenCV's PNG
    /// encoder writes no density chunk either), fixed the same way <see cref="StampPngDensity"/>
    /// already fixes it for BatchExportService's own PNG export path.</summary>
    private static void WritePng(string path, Mat mat, TiffMetadata metadata)
    {
        if (!Cv2.ImEncode(".png", mat, out var bytes))
            throw new IOException($"Could not encode PNG: {path}");
        StampPngDensity(ref bytes, metadata.Dpi);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>OpenCV's JPEG encoder has no DPI/density parameter (unlike its TIFF encoder,
    /// see WriteTiff's XRESOLUTION/YRESOLUTION tags) — without this, every JPG-format capture
    /// would silently lose its DPI, and any tool reading physical page size from the file would
    /// default to 72/96 DPI, reintroducing the exact wrong-physical-size problem the DPI
    /// calibration feature exists to fix. libjpeg (which both OpenCV and SkiaSharp use here)
    /// always writes a standard JFIF APP0 segment as the first thing after the SOI marker for a
    /// baseline JPEG, at a fixed offset, so patching its units/density fields in place — rather
    /// than re-encoding — is safe as long as the expected marker bytes are verified first.
    /// Layout: FFD8 (SOI) FFE0 (APP0) LenHi LenLo "JFIF\0" VerMajor VerMinor Units(1) XDensity(2,
    /// big-endian) YDensity(2, big-endian) ... units=1 means "dots per inch".</summary>
    // Internal (not private): BatchExportService's JPG/PNG export branch encodes through
    // SkiaSharp directly (a different path from WriteJpeg's OpenCV encode above, needed there
    // since PDF/JPG/PNG export re-encodes an already-processed source image rather than a fresh
    // OpenCV Mat) and calls this same stamping logic so exported JPGs don't lose their DPI either.
    internal static void StampJfifDensity(byte[] jpegBytes, int dpi)
    {
        const int unitsOffset = 13; // FFD8 FFE0 LenHi LenLo 'J''F''I''F'\0 VerMajor VerMinor
        if (jpegBytes.Length < unitsOffset + 5) return;
        if (jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8 || jpegBytes[2] != 0xFF || jpegBytes[3] != 0xE0) return;
        if (jpegBytes[6] != (byte)'J' || jpegBytes[7] != (byte)'F' || jpegBytes[8] != (byte)'I' ||
            jpegBytes[9] != (byte)'F' || jpegBytes[10] != 0x00) return;

        var clampedDpi = (ushort)Math.Clamp(dpi, 1, ushort.MaxValue);
        jpegBytes[unitsOffset] = 1; // units = dots per inch
        jpegBytes[unitsOffset + 1] = (byte)(clampedDpi >> 8);
        jpegBytes[unitsOffset + 2] = (byte)(clampedDpi & 0xFF);
        jpegBytes[unitsOffset + 3] = (byte)(clampedDpi >> 8);
        jpegBytes[unitsOffset + 4] = (byte)(clampedDpi & 0xFF);
    }

    /// <summary>PNG has no equivalent of JPEG's JFIF density field baked into a fixed early
    /// offset — physical resolution instead lives in an optional pHYs ancillary chunk (pixels per
    /// meter, not inch — PNG's own unit choice) that SkiaSharp's SKImage.Encode never writes,
    /// silently dropping DPI for every PNG export the same way JPG would without
    /// <see cref="StampJfifDensity"/>. Unlike JFIF's fixed-offset patch, PNG chunks are
    /// variable-length and CRC32-checked, so this inserts a fresh pHYs chunk (rather than
    /// patching bytes in place) immediately after the mandatory IHDR chunk (always the first
    /// chunk after PNG's 8-byte signature, per the PNG spec) — the standard, spec-legal
    /// placement, since pHYs may appear anywhere before the first IDAT.</summary>
    internal static void StampPngDensity(ref byte[] pngBytes, int dpi)
    {
        const int signatureLength = 8; // 89 50 4E 47 0D 0A 1A 0A
        if (pngBytes.Length < signatureLength + 8) return;
        ReadOnlySpan<byte> pngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!pngBytes.AsSpan(0, signatureLength).SequenceEqual(pngSignature)) return;

        // IHDR is always immediately after the signature, and its total on-disk size (Length(4) +
        // Type(4) + 13-byte payload + CRC(4)) is fixed since IHDR's payload is a constant 13 bytes.
        const int ihdrChunkSize = 4 + 4 + 13 + 4;
        var ihdrTypeSpan = pngBytes.AsSpan(signatureLength + 4, 4);
        if (ihdrTypeSpan[0] != (byte)'I' || ihdrTypeSpan[1] != (byte)'H' || ihdrTypeSpan[2] != (byte)'D' || ihdrTypeSpan[3] != (byte)'R') return;
        var insertAt = signatureLength + ihdrChunkSize;
        if (pngBytes.Length < insertAt) return;

        var pixelsPerMeter = (uint)Math.Round(Math.Clamp(dpi, 1, 100000) / 0.0254);
        var payload = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), pixelsPerMeter); // X pixels per unit
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), pixelsPerMeter); // Y pixels per unit
        payload[8] = 1; // unit specifier = meters

        var chunkType = new byte[] { (byte)'p', (byte)'H', (byte)'Y', (byte)'s' };
        var crcInput = new byte[chunkType.Length + payload.Length];
        chunkType.CopyTo(crcInput, 0);
        payload.CopyTo(crcInput, chunkType.Length);
        var crc = Crc32(crcInput);

        var chunk = new byte[4 + 4 + payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)payload.Length);
        chunkType.CopyTo(chunk, 4);
        payload.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + payload.Length, 4), crc);

        var result = new byte[pngBytes.Length + chunk.Length];
        pngBytes.AsSpan(0, insertAt).CopyTo(result);
        chunk.CopyTo(result, insertAt);
        pngBytes.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
        pngBytes = result;
    }

    /// <summary>Standard zlib/PNG CRC-32 (polynomial 0xEDB88320), computed the reference way
    /// (no lookup-table precompute, since this runs once per export, not per pixel).</summary>
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>Decides between <see cref="WriteTiff"/> and <see cref="WriteJpeg"/> for one
    /// processed page and returns the exact path written (including the extension that decision
    /// implies), so every caller in <see cref="Process"/>/<see cref="ProcessFixedFrames"/> gets
    /// the right output format without duplicating this rule at each call site. Binarized (pure
    /// black-and-white) output is always written as TIFF regardless of <paramref name="captureFormat"/>
    /// — a 1-bit CCITT-G4 bitonal image has no meaningful JPEG equivalent (JPEG is a lossy
    /// photographic codec; forcing bilevel content through it would reintroduce exactly the
    /// compression artifacts binarizing was meant to eliminate, and lose the genuine file-size
    /// win of true 1-bit packing).
    ///
    /// <paramref name="singlePageInputPath"/>, when non-null, applies the single-page collision-
    /// avoidance naming rule (<see cref="SinglePageOutputFileName"/>) against the resolved
    /// extension instead of using <paramref name="fileNameNoExt"/> verbatim — pass null for
    /// split-page (_1_left/_2_right) and fixed-frame (_frameNN) outputs, whose suffixed names
    /// already can't collide with the original regardless of extension.</summary>
    private static string WritePageOutput(string outputDirectory, string fileNameNoExt, Mat mat, TiffMetadata metadata, bool binarized, string captureFormat, string? singlePageInputPath = null)
    {
        // Binarized (pure black-and-white) output is always written as TIFF regardless of
        // captureFormat — see this method's own class-level remarks on WritePageOutput's
        // original JPG-vs-TIFF decision; PNG is excluded from that override for the same reason.
        var useJpeg = !binarized && string.Equals(captureFormat, "JPG", StringComparison.OrdinalIgnoreCase);
        var usePng = !binarized && string.Equals(captureFormat, "PNG", StringComparison.OrdinalIgnoreCase);
        var extension = useJpeg ? ".jpg" : usePng ? ".png" : ".tif";
        var finalFileName = singlePageInputPath != null
            ? SinglePageOutputFileName(singlePageInputPath, extension)
            : fileNameNoExt + extension;
        var path = Path.Combine(outputDirectory, finalFileName);
        if (useJpeg)
            WriteJpeg(path, mat, metadata);
        else if (usePng)
            WritePng(path, mat, metadata);
        else
            WriteTiff(path, mat, metadata, binarized);
        return path;
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

    /// <summary>Inverse of <see cref="ParseFixedFrames"/> — the format the UI's live-view frame
    /// editor saves onto Batch.FixedFrames.</summary>
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

    /// <summary>Reads just a captured frame's pixel dimensions — used by the lens-calibration
    /// UI's DPI-measurement step (MicroCapture.UI.ViewModels.LensCalibrationViewModel) so it can
    /// measure a calibration target's pixel span without the UI project needing its own
    /// OpenCvSharp dependency, mirroring how every other OpenCV-touching operation this project
    /// exposes to the UI goes through a static ImageProcessor/LensCalibrationService entry point
    /// instead. Returns (0, 0) if the file can't be decoded.</summary>
    public static (int Width, int Height) GetImagePixelDimensions(string path)
    {
        using var mat = Cv2.ImRead(path, ImreadModes.Grayscale);
        return mat.Empty() ? (0, 0) : (mat.Cols, mat.Rows);
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

    /// <summary>Manual-crop-quad path only: the operator already drew the exact page boundary
    /// in Crop Review, so there's nothing for Method 4 to auto-detect here — this still runs
    /// deskew/book-curve-dewarp/line-mesh (all opt-in/low-risk geometric touch-ups on top of an
    /// already-correct crop) before handing off to <see cref="FinishPageProcessing"/> for the
    /// shared tail (finger/bleedthrough/enhance/manual-adjust/QC/resize/binarize). The automatic
    /// (non-manual-override) capture path no longer calls this — see
    /// <see cref="ProcessAutoDetected"/>, which runs the Method 4 single-pass flatten instead of
    /// TryAutoCrop/TryDeskew/TryApplyDewarp/TryApplyLineMesh.</summary>
    private Mat ProcessSinglePage(Mat input, ProcessingResult result, bool skipAutoCrop, bool dewarpEnabled = false, DewarpModel? savedDewarp = null, bool dewarpManualOverride = false, double? spineXHint = null, bool binarizeEnabled = false, int dpi = 300, double measuredDpi = BaselineDpi, Rect? searchRegion = null, bool bleedthroughEnabled = false, bool hasManualAdjustments = false, int rotationDegrees = 0, bool flipHorizontal = false, bool flipVertical = false, double brightness = 0, double contrast = 0, double saturation = 0, double sharpness = 0, double whiteBalance = 0)
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
        // Not gated behind dewarpEnabled (the book-curve toggle): the residual bow this fixes
        // shows up on flat trapezoid/keystone photos as much as curved book pages — confirmed
        // on the user's own annotated Trapezoid_Image001 screenshot, which has no book
        // curvature at all. See TryApplyLineMesh's own doc comment.
        working = TryApplyLineMesh(working, result) ?? working;

        return FinishPageProcessing(working, result, binarizeEnabled, dpi, measuredDpi, bleedthroughEnabled, hasManualAdjustments, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);
    }

    /// <summary>Shared tail of every page-processing path (Method 4 auto-detect AND the manual-
    /// crop-quad path above): finger removal, bleedthrough suppression, enhancement/sharpen,
    /// optional manual (operator) adjustments, quality checks, DPI resample, and optional
    /// binarization — every real, independently-valuable feature that has nothing to do with
    /// HOW the page's geometric boundary/curvature correction was produced. Takes ownership of
    /// <paramref name="working"/> (disposes intermediate Mats as it reassigns the local, exactly
    /// as the old inline tail in <see cref="ProcessSinglePage"/> did) and returns the final Mat
    /// for the caller to write out.</summary>
    private Mat FinishPageProcessing(Mat working, ProcessingResult result, bool binarizeEnabled, int dpi, double measuredDpi, bool bleedthroughEnabled, bool hasManualAdjustments, int rotationDegrees, bool flipHorizontal, bool flipVertical, double brightness, double contrast, double saturation, double sharpness, double whiteBalance)
    {
        working = TryRemoveFingers(working, result) ?? working;
        // Before enhancement/sharpen so a sharpen pass doesn't crisp up leftover ghosting, and
        // before binarization so Sauvola's own local threshold isn't skewed by it either.
        working = TryRemoveBleedthrough(working, result, bleedthroughEnabled);

        working = ApplyEnhancement(working);
        working = Sharpen(working);
        // Manual (operator-driven) adjustments layer on top of the automatic CLAHE
        // enhancement/sharpen above rather than replacing them — see
        // ApplyManualAdjustments's own doc comment for the fixed operation order. Skipped
        // entirely (not just a no-op call) when the job was never touched in the Adjust UI,
        // so untouched pages are provably unaffected by this feature's existence.
        if (hasManualAdjustments)
        {
            var adjusted = ApplyManualAdjustments(working, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);
            working.Dispose();
            working = adjusted;
        }
        // Quality checks must see the real (color/grayscale) capture, not a bilevel result —
        // blur/exposure scores are meaningless once thresholded to pure black-and-white, so
        // binarization runs last, after QC rather than before it.
        RunQualityChecks(working, result);
        // Resize before binarization (not after): DespeckleBinary's blob-size floor is scaled
        // against actual pixel density, so it needs to see the image at its final pixel grid,
        // not the pre-resize one. Everything before this point (crop/deskew/dewarp/mesh/QC)
        // deliberately runs at native capture resolution, where all of this class's other
        // pixel-footprint tunables (Sharpen's sigma, CLAHE tile size, etc.) were tuned.
        working = ResizeForDpi(working, dpi, measuredDpi);
        working = TryApplyBinarization(working, result, binarizeEnabled, dpi, measuredDpi);

        return working;
    }

    // ───────────── AUTO-CROP ─────────────

    // SourcePass is diagnostic-only (which candidate pass produced this detection) — never fed
    // back into BuildDetection's confidence math, so every existing call site (default/plain
    // constructor) keeps compiling unchanged.
    private readonly record struct BoundaryDetection(bool Found, double Confidence, Rect PaddedRect, Point2f[]? Quad, string? SourcePass = null);

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
        var best = BestDetectionFromContours(directPass.Contours, imageArea, regionMat.Cols, regionMat.Rows) with { SourcePass = "direct-canny" };
        if (normalizedPass is { } np)
        {
            var normalizedBest = BestDetectionFromContours(np.Contours, imageArea, regionMat.Cols, regionMat.Rows) with { SourcePass = "normalized-canny" };
            if (normalizedBest.Confidence > best.Confidence) best = normalizedBest;
        }

        // Tried and reverted: when neither pass found anything ratio-qualifying, retry with a
        // much heavier dilation (FindContoursWithHeavyDilation, still below) to bridge a
        // fragmented real-page edge into one closed loop. It did rescue genuine full-page
        // detections on some real photos — but on others it latched onto a small, cleanly-
        // rectangular *wrong* region instead (a paragraph of justified text, a printed table),
        // which scores deceptively well on BuildDetection's rectangularity/corner-angle terms
        // despite covering as little as 11% of the frame. Two of those false latches produced
        // an output that was nearly pure black — worse than useless. "Only runs when the normal
        // passes already found nothing" made this *additive to detection*, but not *additive to
        // safety*: the honest fallback it replaced (keep the full frame, flag low confidence for
        // manual review) was a safe failure; a confident-looking wrong or blank crop is not.
        // Caught by the user visually spot-checking the published gallery, not by anything
        // automated — see the memory note on this for the real per-image tally before reverting.
        // A real fix needs a way to tell a genuine (but small/oddly-shaped) page from a small
        // wrong object BEFORE trusting the aggressive pass, not merely a "found vs not" gate.
        //
        // The brightness pass below is not a retry of that same idea with a different knob — it
        // reads a different physical signal (page brightness vs. dark backdrop, not edge
        // gradient), so it can't inherit that failure mode the same way: a printed
        // paragraph/table has no brightness contrast against its own page, so it can't form a
        // separate "largest bright blob" the way it formed a separate "sharpest edge" under
        // heavy dilation. It also runs unconditionally (not gated behind "only when the others
        // found nothing") and is scored through the exact same BuildDetection confidence
        // function as every other candidate — no source gets a trust bonus, the evidence has to
        // win on its own merits, with its own dedicated safety gates
        // (BrightnessSeparationScore, the all-four-edges-touched guard) for the failure modes
        // that *are* specific to a brightness signal (glare, no real backdrop).
        var brightnessContours = FindContoursByBrightness(regionMat, BrightnessSeparationMinScore, SkinCrLow, SkinCrHigh, SkinCbLow, SkinCbHigh);
        var brightnessBest = BestDetectionFromContours(brightnessContours, imageArea, regionMat.Cols, regionMat.Rows) with { SourcePass = "brightness" };
        if (brightnessBest.Confidence > best.Confidence) best = brightnessBest;

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

    private static Point[][] FindContoursWithHeavyDilation(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);

        var (low, high) = AutoCannyThresholds(blurred);
        using var edged = new Mat();
        Cv2.Canny(blurred, edged, low, high);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
        using var dilated = new Mat();
        Cv2.Dilate(edged, dilated, kernel, iterations: 4);

        Cv2.FindContours(dilated, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return contours;
    }

    /// <summary>Foreground segmentation by raw brightness rather than edge gradient: every real
    /// capture fixture this app has (flat copy-stand and V-cradle alike) photographs the page
    /// against a solid dark backdrop, which makes page-vs-backdrop a much stronger, more direct
    /// separator than page-edge-gradient — the latter is what fragments on real photos (hand
    /// holding the book, spine shadow, paper texture), which is why <see
    /// cref="FindContoursWithAdaptiveCanny"/> alone misses or mis-shapes the page on a large
    /// fraction of real captures. This is a genuinely different signal, not another tuning knob
    /// on the same fragile one — an important distinction given the heavy-dilation attempt at
    /// bridging fragmented Canny edges (see the "Tried and reverted" note in
    /// <see cref="DetectBoundaryInMat"/>) confidently latched onto small wrong rectangular
    /// objects (a paragraph, a printed table) instead of rescuing real misses.
    ///
    /// Never trusts a meaningless split: <see cref="BrightnessSeparationScore"/> gates on Otsu's
    /// own separability metric first (handles a light-colored desk / no real dark backdrop), and
    /// any candidate whose bounding rect touches all four image edges is discarded (handles a
    /// blown-out/glare backdrop merging with the page into one full-frame blob) — both failure
    /// modes degrade to "this pass contributes nothing," letting the existing Canny passes or an
    /// honest "not found" arbitrate instead, never a confidently-wrong brightness-based guess.</summary>
    private static Point[][] FindContoursByBrightness(Mat src, double separationMinScore,
        double skinCrLow, double skinCrHigh, double skinCbLow, double skinCbHigh)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        using var mask = new Mat();
        var otsuThreshold = Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        if (BrightnessSeparationScore(gray, otsuThreshold) < separationMinScore)
            return Array.Empty<Point[]>();

        using (var skin = SkinExclusionMask(src, skinCrLow, skinCrHigh, skinCbLow, skinCbHigh))
            Cv2.BitwiseAnd(mask, skin, mask);

        // Opening (erode then dilate) before closing — removes thin bright artifacts (a
        // lighting reflection/sheen streak on the black backdrop, confirmed on a real photo:
        // Trapezoid_Image001's right half, a ~21px-wide reflection ran from the true page edge
        // another ~300px down into the backdrop and was bright enough to cross Otsu's threshold
        // directly) before they can reach the contour at all. Sized well below the closing
        // kernel below: large enough to erase a several-pixel-wide streak completely, small
        // enough that a real page's own bulk survives erosion+dilation intact (only rounding
        // true corners slightly, which corner refinement downstream already corrects for).
        var openKernelSize = Math.Max(15, Math.Min(src.Cols, src.Rows) / 80);
        using var openKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(openKernelSize, openKernelSize));
        using var opened = new Mat();
        Cv2.MorphologyEx(mask, opened, MorphTypes.Open, openKernel);

        // Square closing (unlike DetectTextLineBlobs's deliberately horizontal-only kernel):
        // the page interior needs bridging in both axes to merge across printed text/photos
        // that locally dip below the brightness threshold, not just across a text line's own
        // width.
        var kernelSize = Math.Max(21, Math.Min(src.Cols, src.Rows) / 30);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kernelSize, kernelSize));
        using var closed = new Mat();
        Cv2.MorphologyEx(opened, closed, MorphTypes.Close, kernel);

        Cv2.FindContours(closed, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        return contours.Where(c =>
        {
            var rect = Cv2.BoundingRect(c);
            var touchesAllFour = rect.X <= 1 && rect.Y <= 1
                && rect.Right >= src.Cols - 1 && rect.Bottom >= src.Rows - 1;
            return !touchesAllFour;
        }).ToArray();
    }

    /// <summary>Otsu's between-class/total-variance separability score at <paramref
    /// name="threshold"/> (0 = no real separation — a near-uniform histogram; 1 = a perfect
    /// two-spike histogram). Otsu's own threshold-selection always returns *a* number even when
    /// the underlying image has no real bimodal brightness split to find (a uniformly-lit desk,
    /// a blown-out frame) — this score is what lets <see cref="FindContoursByBrightness"/> tell
    /// those cases apart from a genuine bright-page-on-dark-backdrop split, instead of trusting
    /// Otsu blindly.</summary>
    private static double BrightnessSeparationScore(Mat gray, double threshold)
    {
        gray.GetArray(out byte[] pixels);
        if (pixels.Length == 0) return 0;

        long sumBelow = 0, sumAbove = 0;
        long countBelow = 0, countAbove = 0;
        foreach (var p in pixels)
        {
            if (p <= threshold) { sumBelow += p; countBelow++; }
            else { sumAbove += p; countAbove++; }
        }
        if (countBelow == 0 || countAbove == 0) return 0;

        var meanBelow = sumBelow / (double)countBelow;
        var meanAbove = sumAbove / (double)countAbove;
        var weightBelow = countBelow / (double)pixels.Length;
        var weightAbove = countAbove / (double)pixels.Length;
        var betweenClassVariance = weightBelow * weightAbove * Math.Pow(meanAbove - meanBelow, 2);

        var grandMean = (sumBelow + sumAbove) / (double)pixels.Length;
        double totalVariance = 0;
        foreach (var p in pixels) totalVariance += Math.Pow(p - grandMean, 2);
        totalVariance /= pixels.Length;

        return totalVariance > 0 ? Math.Clamp(betweenClassVariance / totalVariance, 0, 1) : 0;
    }

    /// <summary>Raw per-pixel skin classification via a YCrCb chrominance range (Cr/Cb held
    /// stable across a hand moving between lit and shadowed parts of the same real photo, unlike
    /// HSV's saturation/value channels) — 255 = classified as skin, 0 = not. Shared by
    /// <see cref="SkinExclusionMask"/> (boundary detection: dilated and inverted, generously) and
    /// <see cref="TryRemoveFingers"/> (inpainting: used directly, since erasing real page content
    /// by mistake is a worse failure than boundary detection's own generous exclusion).</summary>
    private static Mat RawSkinMask(Mat src, double crLow, double crHigh, double cbLow, double cbHigh)
    {
        using var ycrcb = new Mat();
        Cv2.CvtColor(src, ycrcb, ColorConversionCodes.BGR2YCrCb);
        var skin = new Mat();
        Cv2.InRange(ycrcb, new Scalar(0, crLow, cbLow), new Scalar(255, crHigh, cbHigh), skin);
        return skin;
    }

    /// <summary>255 = keep (not skin), 0 = exclude (skin), so the result can be
    /// <see cref="Cv2.BitwiseAnd(Mat, Mat, Mat)"/>ed directly against a foreground mask. The skin
    /// mask is dilated before exclusion — deliberately generous, since this only needs to keep
    /// the detected page quad's shape sane where a hand overlaps its edge, not achieve
    /// pixel-perfect hand removal; any resulting bite out of the true edge is what
    /// <see cref="RefineQuadCorners"/> is for.</summary>
    private static Mat SkinExclusionMask(Mat src, double crLow, double crHigh, double cbLow, double cbHigh)
    {
        using var skin = RawSkinMask(src, crLow, crHigh, cbLow, cbHigh);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        var dilatedSkin = new Mat();
        Cv2.Dilate(skin, dilatedSkin, kernel, iterations: 1);

        var notSkin = new Mat();
        Cv2.BitwiseNot(dilatedSkin, notSkin);
        dilatedSkin.Dispose();
        return notSkin;
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
                // Try the strongest correction first: map each of the 4 edges' own actual
                // *shape* (curve, not just 2 endpoints) onto the output rectangle. A corner-only
                // homography (below) cannot rectify an edge that isn't a straight line — confirmed
                // on a real photo (Trapezoid_Image001's right half) still visibly bent after
                // straight-line corner refinement alone. RectifyWithBoundaryCurves never throws
                // and returns null whenever it can't trust the evidence, so falling back to the
                // corner-only path is always safe.
                var boundaryRectified = RectifyWithBoundaryCurves(src, detection.Quad, BoundaryCurveBandPx, BoundaryCurveMinInliers, BoundaryCurveMaxOffsetFraction, BoundaryCurveNegligibleOffsetPx);
                if (boundaryRectified != null)
                {
                    cropped = boundaryRectified;
                    result.Warnings.Add("Document boundary detected and perspective-corrected (multi-point edge rectification).");
                }
                else
                {
                    // Refine the approximate polygon-approximation corners against fresh edge
                    // evidence before warping — a real, non-degenerate quad can still be visibly
                    // keystoned in the output if its corners are imprecise. RefineQuadCorners never
                    // throws and falls back per-corner to the original vertex when it can't trust
                    // its own evidence, so this is always safe to call.
                    var refinedQuad = RefineQuadCorners(src, detection.Quad, CornerRefinementBandPx, CornerRefinementMinInliers);
                    cropped = WarpQuad(src, refinedQuad);
                    result.Warnings.Add("Document boundary detected and perspective-corrected.");
                }
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

    /// <summary>Reports the exact same per-line (Y, slope) data and Theil-Sen fit
    /// <see cref="TryDeskewRotationField"/> computes internally (degree-1 fit per line, not
    /// <see cref="DebugDewarpLines"/>'s degree-3 curve fit, which is a different, noisier
    /// signal) — for diagnosing why the rotation-field check did or didn't fire on a specific
    /// image, without guessing from a differently-fit diagnostic.</summary>
    public string DebugDeskewRotationField(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";

        var (binary, lines) = DetectTextLineBlobs(mat);
        using (binary)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");
            sb.AppendLine($"Lines detected: {lines.Count}");

            var lineAngles = new List<(double Y, double Slope, double AngleDeg)>();
            foreach (var line in lines)
            {
                var samples = SampleLineEdge(binary, line, topEdge: true);
                if (samples.Count < 8) continue;
                var fit = WeightedPolyFit(samples.Select(s => (s.X, s.Y, 1.0)).ToList(), degree: 1);
                if (fit == null) continue;
                var slope = fit[1];
                var angleDeg = Math.Atan2(slope, 1.0) * 180.0 / Math.PI;
                lineAngles.Add((line.Y + line.Height / 2.0, slope, angleDeg));
                sb.AppendLine($"  Y={line.Y + line.Height / 2.0:F0} slope={slope:F4} angle={angleDeg:F2}deg (rect width={line.Width})");
            }

            if (lineAngles.Count < MinRotationFieldLines)
            {
                sb.AppendLine($"Only {lineAngles.Count} usable lines, need >= {MinRotationFieldLines} — would decline.");
                return sb.ToString();
            }

            var minYGap = mat.Rows * 0.02;
            var slopeTrendCandidates = new List<double>();
            for (var i = 0; i < lineAngles.Count; i++)
                for (var j = i + 1; j < lineAngles.Count; j++)
                {
                    var dy = lineAngles[j].Y - lineAngles[i].Y;
                    if (Math.Abs(dy) < minYGap) continue;
                    slopeTrendCandidates.Add((lineAngles[j].Slope - lineAngles[i].Slope) / dy);
                }
            slopeTrendCandidates.Sort();
            var b = slopeTrendCandidates.Count > 0 ? slopeTrendCandidates[slopeTrendCandidates.Count / 2] : 0;

            var intercepts = lineAngles.Select(p => p.Slope - b * p.Y).OrderBy(v => v).ToList();
            var a = intercepts.Count > 0 ? intercepts[intercepts.Count / 2] : 0;

            var inliers = lineAngles.Count(p => Math.Abs(p.Slope - (a + b * p.Y)) <= RotationFieldInlierSlope);
            var inlierFraction = inliers / (double)lineAngles.Count;

            var topY = lineAngles.Min(p => p.Y);
            var bottomY = lineAngles.Max(p => p.Y);
            var slopeAtTopDeg = Math.Atan(a + b * topY) * 180.0 / Math.PI;
            var slopeAtBottomDeg = Math.Atan(a + b * bottomY) * 180.0 / Math.PI;
            var rangeDeg = Math.Abs(slopeAtBottomDeg - slopeAtTopDeg);

            sb.AppendLine($"Theil-Sen fit: slope(Y) = {a:F5} + {b:F7}*Y");
            sb.AppendLine($"Inliers: {inliers}/{lineAngles.Count} ({inlierFraction:P0}), need >= {MinRotationFieldInlierFraction:P0}");
            sb.AppendLine($"Implied span top-to-bottom: {rangeDeg:F2}deg (min={MinRotationFieldRangeDegrees}, max={MaxRotationFieldRangeDegrees})");
            sb.AppendLine(inlierFraction >= MinRotationFieldInlierFraction && rangeDeg >= MinRotationFieldRangeDegrees && rangeDeg <= MaxRotationFieldRangeDegrees
                ? "Would APPLY the rotation field."
                : "Would DECLINE (falls through to margin-shear / Hough).");

            return sb.ToString();
        }
    }

    /// <summary>Reports what <see cref="TryApplyLineMesh"/> would do to a page — per-line X
    /// coverage, median Y, and each line's own max absolute displacement from that median —
    /// without needing to run the full pipeline to see whether the mesh step would apply,
    /// decline, or which lines contributed.</summary>
    /// <summary>Byte-decoded, crop/dewarp-free entry point into <see cref="TryApplyLineMesh"/>
    /// alone — for SmokeTest to verify the mesh step's own actual pixel output (not just its
    /// text diagnostic) without the rest of the pipeline (auto-crop, boundary rectification)
    /// obscuring what changed. Returns null on any failure or decline, same convention as every
    /// other Try*/Detect* entry point in this class.</summary>
    public byte[]? ApplyLineMeshFromBytes(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return null;
        var result = new ProcessingResult();
        using var warped = TryApplyLineMesh(mat, result);
        if (warped == null) return null;
        Cv2.ImEncode(".png", warped, out var bytes);
        return bytes;
    }

    public string DebugLineMesh(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");

        var (binary, lines) = DetectTextLineBlobs(mat);
        using (binary)
        {
            sb.AppendLine($"Lines detected: {lines.Count} (need >= {MinMeshLines})");

            var pooled = new List<(double X, double Y, double W)>();
            var usable = 0;
            foreach (var line in lines)
            {
                var samples = SampleLineCentroid(binary, line, MeshCentroidSmoothWindowPx);
                if (samples.Count < MeshLineMinSamples)
                {
                    sb.AppendLine($"  Y~{line.Y + line.Height / 2}: {samples.Count} samples, below minimum ({MeshLineMinSamples}), skipped.");
                    continue;
                }
                var sortedY = samples.Select(s => s.Y).OrderBy(y => y).ToList();
                var medianY = sortedY[sortedY.Count / 2];
                var maxDisp = samples.Max(s => Math.Abs(s.Y - medianY));
                foreach (var s in samples) pooled.Add((s.X, s.Y - medianY, 1.0));
                usable++;
                sb.AppendLine($"  Y~{line.Y + line.Height / 2}: {samples.Count} samples, medianY={medianY:F1}, ownMaxAbsDisp={maxDisp:F1}px, X=[{samples.Min(s => s.X):F0},{samples.Max(s => s.X):F0}]");
            }

            sb.AppendLine($"Usable lines: {usable} (need >= {MinMeshLines})");
            if (usable < MinMeshLines)
            {
                sb.AppendLine("Would DECLINE (not enough usable lines).");
                return sb.ToString();
            }

            var coeffs = WeightedPolyFit(pooled, MeshCurveDegree);
            if (coeffs == null)
            {
                sb.AppendLine("Would DECLINE (pooled fit failed — singular system).");
                return sb.ToString();
            }

            var maxAbsDelta = 0.0;
            var minX = 0;
            var maxX = mat.Cols - 1;
            for (var x = minX; x <= maxX; x += Math.Max(1, mat.Cols / 200))
                maxAbsDelta = Math.Max(maxAbsDelta, Math.Abs(EvalPoly(coeffs, x)));

            var maxAllowed = mat.Rows * MeshMaxDisplacementFraction;
            sb.AppendLine($"Pooled fit coeffs: {FormatCoeffs(coeffs)}");
            sb.AppendLine($"Max |fitted column delta| across width: {maxAbsDelta:F1}px (limit {maxAllowed:F1}px)");
            sb.AppendLine(maxAbsDelta <= maxAllowed
                ? "Would APPLY the text-line mesh correction."
                : "Would DECLINE (fitted correction too large).");
        }
        return sb.ToString();
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
        var instance = new ImageProcessor();
        var gutter = DetectGutter(mat, instance.GutterMinFlankMarginFraction);
        sb.AppendLine($"Gutter: fraction={gutter.Fraction:F4} confidence={gutter.Confidence:F4} absoluteDropLevels={gutter.AbsoluteDropLevels:F2}");
        var twoPage = instance.DetectTwoPageBoundaries(mat);
        sb.AppendLine($"TwoPage: found={twoPage.Found} leftConfidence={twoPage.Left.Confidence:F4} rightConfidence={twoPage.Right.Confidence:F4}");
        return sb.ToString();
    }

    /// <summary>Diagnostic-only entry point: reports both contour passes' best area ratio and
    /// resulting confidence, for troubleshooting why TryAutoCrop declined to crop a specific
    /// image (e.g. a post-split half that still includes background clutter).</summary>
    public string DebugBoundaryDetection(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");
        var (directPass, normalizedPass) = FindDocumentContourPasses(mat);
        var imageArea = mat.Rows * (double)mat.Cols;

        sb.AppendLine($"Direct pass: {directPass.Contours.Length} contour(s)");
        foreach (var c in directPass.Contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(3))
        {
            var d = BuildDetection(c, imageArea, mat.Cols, mat.Rows);
            sb.AppendLine($"  ratio={Cv2.ContourArea(c) / imageArea:F4} found={d.Found} confidence={d.Confidence:F4} rect={d.PaddedRect}");
        }

        if (normalizedPass is { } np)
        {
            sb.AppendLine($"Normalized pass: {np.Contours.Length} contour(s)");
            foreach (var c in np.Contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(3))
            {
                var d = BuildDetection(c, imageArea, mat.Cols, mat.Rows);
                sb.AppendLine($"  ratio={Cv2.ContourArea(c) / imageArea:F4} found={d.Found} confidence={d.Confidence:F4} rect={d.PaddedRect}");
            }
        }
        else
        {
            sb.AppendLine("Normalized pass: not run (direct pass already found a large-enough contour)");
        }

        var directBest = BestDetectionFromContours(directPass.Contours, imageArea, mat.Cols, mat.Rows);
        var normalizedBest = normalizedPass is { } np2 ? BestDetectionFromContours(np2.Contours, imageArea, mat.Cols, mat.Rows) : default;
        if (!directBest.Found && !normalizedBest.Found)
        {
            var aggressive = FindContoursWithHeavyDilation(mat);
            sb.AppendLine($"Aggressive (heavy-dilation) pass, kept dormant/not scored — see the 'Tried and reverted' note in DetectBoundaryInMat: {aggressive.Length} contour(s)");
            foreach (var c in aggressive.OrderByDescending(c => Cv2.ContourArea(c)).Take(5))
            {
                var d = BuildDetection(c, imageArea, mat.Cols, mat.Rows);
                sb.AppendLine($"  ratio={Cv2.ContourArea(c) / imageArea:F4} found={d.Found} confidence={d.Confidence:F4} rect={d.PaddedRect}");
            }
        }

        using var brightnessGray = new Mat();
        Cv2.CvtColor(mat, brightnessGray, ColorConversionCodes.BGR2GRAY);
        using var brightnessMaskPreview = new Mat();
        var otsuThreshold = Cv2.Threshold(brightnessGray, brightnessMaskPreview, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var separationScore = BrightnessSeparationScore(brightnessGray, otsuThreshold);
        var brightnessContours = FindContoursByBrightness(mat, BrightnessSeparationMinScore, SkinCrLow, SkinCrHigh, SkinCbLow, SkinCbHigh);
        sb.AppendLine($"Brightness pass: separationScore={separationScore:F4} (min={BrightnessSeparationMinScore}) otsuThreshold={otsuThreshold:F1} {brightnessContours.Length} contour(s)");
        foreach (var c in brightnessContours.OrderByDescending(c => Cv2.ContourArea(c)).Take(3))
        {
            var d = BuildDetection(c, imageArea, mat.Cols, mat.Rows);
            sb.AppendLine($"  ratio={Cv2.ContourArea(c) / imageArea:F4} found={d.Found} confidence={d.Confidence:F4} rect={d.PaddedRect}");
        }

        var best = DetectBoundaryInMat(mat);
        sb.AppendLine($"Winner: found={best.Found} confidence={best.Confidence:F4} sourcePass={best.SourcePass} (MediumConfidenceThreshold={MediumConfidenceThreshold})");
        return sb.ToString();
    }

    /// <summary>Reports the raw <see cref="DeriveQuad"/> corners against what
    /// <see cref="RefineQuadCorners"/> moves them to, plus each edge's own line-fit inlier
    /// count — for diagnosing cases where a real, non-degenerate crop still comes out visibly
    /// keystoned (e.g. <c>Trapezoid_Image001</c>'s right half) without guessing from pixels.
    /// Edge index 0 is quad[0]-&gt;quad[1] (top), 1 is quad[1]-&gt;quad[2] (right), 2 is
    /// quad[2]-&gt;quad[3] (bottom), 3 is quad[3]-&gt;quad[0] (left/spine-side on a post-split
    /// right half).</summary>
    public string DebugCornerRefinement(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");

        var detection = DetectBoundary(mat);
        if (!detection.Found || detection.Quad == null)
        {
            sb.AppendLine($"No quad detection (found={detection.Found}).");
            return sb.ToString();
        }

        var quad = detection.Quad;
        sb.AppendLine($"Confidence: {detection.Confidence:F4}");
        for (var i = 0; i < 4; i++)
            sb.AppendLine($"Raw quad[{i}]: ({quad[i].X:F1}, {quad[i].Y:F1})");

        var edgePoints = GetCannyEdgePoints(mat);

        for (var i = 0; i < 4; i++)
        {
            var line = FitQuadEdge(edgePoints, quad[i], quad[(i + 1) % 4], CornerRefinementBandPx, CornerRefinementMinInliers);
            sb.AppendLine(line is { } l
                ? $"Edge {i} (quad[{i}]->quad[{(i + 1) % 4}]): inliers={l.Inliers} dir=({l.Vx:F3},{l.Vy:F3})"
                : $"Edge {i} (quad[{i}]->quad[{(i + 1) % 4}]): no line (insufficient evidence, corners kept as-is)");
        }

        var refined = RefineQuadCorners(mat, quad, CornerRefinementBandPx, CornerRefinementMinInliers);
        for (var i = 0; i < 4; i++)
        {
            var dx = refined[i].X - quad[i].X;
            var dy = refined[i].Y - quad[i].Y;
            sb.AppendLine($"Refined quad[{i}]: ({refined[i].X:F1}, {refined[i].Y:F1})  moved=({dx:F1}, {dy:F1})");
        }

        sb.AppendLine();
        sb.AppendLine("--- Boundary-curve rectification (multi-point per edge) ---");
        for (var i = 0; i < 4; i++)
        {
            var curve = FitEdgeCurve(edgePoints, quad[i], quad[(i + 1) % 4], BoundaryCurveBandPx, BoundaryCurveMinInliers);
            sb.AppendLine(curve is { } c
                ? $"Edge {i}: inliers={c.Inliers} maxOffsetPx={c.MaxAbsOffset:F1}"
                : $"Edge {i}: no curve (insufficient evidence)");
        }
        var rectified = RectifyWithBoundaryCurves(mat, quad, BoundaryCurveBandPx, BoundaryCurveMinInliers, BoundaryCurveMaxOffsetFraction);
        sb.AppendLine(rectified != null
            ? $"Boundary-curve rectification: SUCCEEDED, output {rectified.Cols}x{rectified.Rows}"
            : "Boundary-curve rectification: declined (falls back to corner-based WarpQuad)");
        rectified?.Dispose();

        return sb.ToString();
    }

    /// <summary>Dumps every geometric point this class's boundary/split detection actually
    /// found, as plain "label,x,y" CSV lines — for drawing a literal overlay on the source
    /// image instead of describing detection results in prose. Includes: the gutter split
    /// column (full-frame images only — a post-split half has nothing to split further), the
    /// raw quad corners, and <paramref name="pointsPerEdge"/> points sampled along each of the
    /// 4 fitted boundary curves (falling back to the straight-line quad edge, unlabeled as a
    /// curve, when that edge's curve fit declines).</summary>
    public string DebugBoundaryPoints(byte[] encodedImage, int pointsPerEdge = 16)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"page,{mat.Cols},{mat.Rows}");

        var gutter = DetectGutter(mat, GutterMinFlankMarginFraction);
        sb.AppendLine($"gutter_fraction,{gutter.Fraction:F4},{gutter.Confidence:F4}");

        var detection = DetectBoundary(mat);
        if (!detection.Found || detection.Quad == null)
        {
            sb.AppendLine($"no_quad_detection,found={detection.Found}");
            return sb.ToString();
        }
        var quad = detection.Quad;
        for (var i = 0; i < 4; i++)
            sb.AppendLine($"raw_corner_{i},{quad[i].X:F1},{quad[i].Y:F1}");

        var edgePoints = GetCannyEdgePoints(mat);
        var edgeDefs = new (string Name, Point2f A, Point2f B)[]
        {
            ("top", quad[0], quad[1]),
            ("right", quad[1], quad[2]),
            ("bottom", quad[3], quad[2]),
            ("left", quad[0], quad[3]),
        };
        foreach (var (name, a, b) in edgeDefs)
        {
            var curve = FitEdgeCurve(edgePoints, a, b, BoundaryCurveBandPx, BoundaryCurveMinInliers);
            for (var i = 0; i < pointsPerEdge; i++)
            {
                var t = pointsPerEdge <= 1 ? 0.0 : i / (double)(pointsPerEdge - 1);
                var p = curve is { } c ? EvalEdgeCurve(c, t) : new Point2f((float)(a.X + (b.X - a.X) * t), (float)(a.Y + (b.Y - a.Y) * t));
                sb.AppendLine($"edge_{name}_{(curve != null ? "curve" : "line")},{p.X:F1},{p.Y:F1}");
            }
        }

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

    /// <summary>Shared Canny edge-pixel extraction behind every corner/edge-curve refinement
    /// step in this class (<see cref="RefineQuadCorners"/>, <see cref="RectifyWithBoundaryCurves"/>,
    /// <see cref="DebugCornerRefinement"/>) — one definition of "what counts as edge evidence."</summary>
    private static Point[] GetCannyEdgePoints(Mat src)
    {
        using var gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 0);
        var (low, high) = AutoCannyThresholds(blurred);
        using var edged = new Mat();
        Cv2.Canny(blurred, edged, low, high);

        using var edgePointsMat = new Mat();
        Cv2.FindNonZero(edged, edgePointsMat);
        if (edgePointsMat.Empty()) return Array.Empty<Point>();
        edgePointsMat.GetArray(out Point[] edgePoints);
        return edgePoints;
    }

    /// <summary>Refines a detected quad's 4 corners against fresh Canny edge evidence, instead
    /// of trusting <see cref="DeriveQuad"/>'s raw <see cref="Cv2.ApproxPolyDP"/> vertices
    /// directly. Fixes a real, confirmed failure mode distinct from "no contour found at all":
    /// a genuine, non-degenerate detection can still be visibly keystoned in the output because
    /// polygon approximation over a somewhat-fragmented real edge lands close to, but not
    /// exactly on, the true corner (confirmed on a real photo — Trapezoid_Image001's right
    /// half, 54% confidence, real page content, still visibly slanted after "correction").
    ///
    /// For each of the quad's 4 edges, fits a line to nearby Canny edge pixels via
    /// <see cref="Cv2.FitLine(System.Collections.Generic.IEnumerable{Point2f}, DistanceTypes, double, double, double)"/>
    /// — deliberately not this file's existing <see cref="WeightedPolyFit"/>, which assumes a
    /// near-horizontal `y = f(x)` line (safe for <see cref="DetectDewarpCurve"/>/<see
    /// cref="TryDeskew"/>'s text lines, but undefined/unstable for a near-vertical quad side,
    /// which any of these 4 edges plausibly is) — then intersects each pair of adjacent fitted
    /// lines to get a refined corner. Refinement is independent per corner: an edge with too
    /// little evidence (occluded by a hand, off-frame) just leaves its two corners at their
    /// original position rather than corrupting the other 3. Never throws — any failure returns
    /// <paramref name="quad"/> unchanged, matching this file's existing defensive style
    /// (<see cref="TryAutoCrop"/>'s own try/catch/fallback shape).</summary>
    private static Point2f[] RefineQuadCorners(Mat src, Point2f[] quad, double bandPx, int minInliers)
    {
        if (quad.Length != 4) return quad;
        try
        {
            var edgePoints = GetCannyEdgePoints(src);
            if (edgePoints.Length == 0) return quad;

            // lines[i] is the fitted line for the edge quad[i] -> quad[(i+1)%4].
            var lines = new EdgeLine?[4];
            for (var i = 0; i < 4; i++)
                lines[i] = FitQuadEdge(edgePoints, quad[i], quad[(i + 1) % 4], bandPx, minInliers);

            var refined = new Point2f[4];
            for (var i = 0; i < 4; i++)
            {
                var incoming = lines[(i + 3) % 4]; // edge ending at this corner
                var outgoing = lines[i]; // edge starting at this corner
                refined[i] = incoming is { } inLine && outgoing is { } outLine
                    && IntersectLines(inLine, outLine) is { } corner
                    ? corner
                    : quad[i];
            }

            var originalArea = Math.Abs(Cv2.ContourArea(quad));
            var refinedArea = Math.Abs(Cv2.ContourArea(refined));
            if (originalArea <= 0 || refinedArea < originalArea * 0.5 || refinedArea > originalArea * 1.5)
                return quad; // Final sanity gate — a per-corner-plausible refinement can still be globally wrong.

            return refined;
        }
        catch
        {
            return quad;
        }
    }

    private readonly record struct EdgeLine(double Vx, double Vy, double X0, double Y0, int Inliers);

    /// <summary>Fits one quad edge's true line from nearby Canny edge pixels: keeps points
    /// within <paramref name="bandPx"/> perpendicular distance of the approximate edge segment
    /// and whose projection onto it falls within the segment (plus a small margin, so a
    /// neighboring edge's pixels near the corner don't contaminate this one's fit). Returns null
    /// when there's too little evidence to trust — the caller then keeps that edge's original
    /// corners rather than fitting a line from noise.</summary>
    private static EdgeLine? FitQuadEdge(Point[] edgePoints, Point2f a, Point2f b, double bandPx, int minInliers)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return null;

        var ux = dx / length;
        var uy = dy / length;
        var nx = -uy;
        var ny = ux;
        var margin = Math.Max(bandPx, length * 0.03);

        var inliers = new List<Point2f>();
        foreach (var p in edgePoints)
        {
            var px = p.X - a.X;
            var py = p.Y - a.Y;
            var t = px * ux + py * uy;
            if (t < -margin || t > length + margin) continue;
            var d = px * nx + py * ny;
            if (Math.Abs(d) > bandPx) continue;
            inliers.Add(new Point2f(p.X, p.Y));
        }

        if (inliers.Count < minInliers) return null;

        var fit = Cv2.FitLine(inliers, DistanceTypes.L2, 0, 0.01, 0.01);
        return new EdgeLine(fit.Vx, fit.Vy, fit.X1, fit.Y1, inliers.Count);
    }

    /// <summary>Intersection of two lines, each given as a point plus direction vector. Returns
    /// null when the lines are near-parallel (the quad's two edges meeting at this corner were
    /// fit to evidence that doesn't actually converge — untrustworthy, not a real corner).</summary>
    private static Point2f? IntersectLines(EdgeLine first, EdgeLine second)
    {
        var denom = first.Vx * second.Vy - first.Vy * second.Vx;
        if (Math.Abs(denom) < 1e-6) return null;

        var dx = second.X0 - first.X0;
        var dy = second.Y0 - first.Y0;
        var t = (dx * second.Vy - dy * second.Vx) / denom;

        return new Point2f((float)(first.X0 + t * first.Vx), (float)(first.Y0 + t * first.Vy));
    }

    // ───────────── BOUNDARY-CURVE RECTIFICATION (multi-point per edge) ─────────────

    /// <summary>One quad edge's true shape as a degree-2 curve in its own local (t, d)
    /// frame — t along the edge from <c>A</c> (t=0) to its paired corner (t=Length), d
    /// perpendicular to it — rather than <see cref="EdgeLine"/>'s single straight line.
    /// <see cref="Coeffs"/> is corrected so the curve evaluates to exactly d=0 at both t=0 and
    /// t=Length: a Coons patch needs each pair of edges meeting at a corner to agree on that
    /// corner's exact position, so the fit's own linear trend between its endpoint values is
    /// subtracted out (keeping the fitted bend, discarding only what would otherwise create a
    /// seam) rather than forcing the raw least-squares fit through the corners directly.</summary>
    private readonly record struct EdgeCurve(Point2f A, double Ux, double Uy, double Nx, double Ny, double Length, double[] Coeffs, int Inliers, double MaxAbsOffset);

    /// <summary>Fits edge <paramref name="a"/>-&gt;<paramref name="b"/> as a curve from every
    /// nearby Canny edge pixel along its whole length, not just 2 endpoints — the direct
    /// generalization of <see cref="FitQuadEdge"/> from a line to a curve. Returns null when
    /// there's too little evidence to trust a shape this specific, matching every other
    /// evidence-gated fit in this file.</summary>
    private static EdgeCurve? FitEdgeCurve(Point[] edgePoints, Point2f a, Point2f b, double bandPx, int minInliers)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return null;

        var ux = dx / length;
        var uy = dy / length;
        var nx = -uy;
        var ny = ux;
        var margin = Math.Max(bandPx, length * 0.03);

        var samples = new List<(double T, double D, double W)>();
        foreach (var p in edgePoints)
        {
            var px = p.X - a.X;
            var py = p.Y - a.Y;
            var t = px * ux + py * uy;
            if (t < -margin || t > length + margin) continue;
            var d = px * nx + py * ny;
            if (Math.Abs(d) > bandPx) continue;
            samples.Add((t, d, 1.0));
        }
        if (samples.Count < minInliers) return null;

        // Require inliers spread across most of the edge, not clustered in one region — a
        // degree-2 fit can look tight at its sample points while swinging wildly in a gap
        // those samples never covered (confirmed on a real photo: IMG_0022's right half —
        // see MaxAbsOffset's own doc comment below for what this fed into).
        var minT = samples.Min(s => s.T);
        var maxT = samples.Max(s => s.T);
        if (minT > length * 0.15 || maxT < length * 0.85) return null;

        var coeffs = WeightedPolyFit(samples, degree: 2);
        if (coeffs == null) return null;

        var d0 = EvalPoly(coeffs, 0);
        var dEnd = EvalPoly(coeffs, length);
        var corrected = (double[])coeffs.Clone();
        corrected[0] -= d0;
        corrected[1] -= (dEnd - d0) / length;

        // Evaluated at dense, uniform steps across the *whole* edge — not just at the inlier
        // sample points, which was the actual bug: a degree-2 fit can sit tight against every
        // sample it was measured against while still swinging far out between sparse regions of
        // them, and checking error only at those same points can never catch that. Confirmed on
        // a real photo (IMG_0022's right half): the sample-point-only check passed comfortably
        // while the curve, evaluated between samples, swept far enough off the real edge to
        // sample from background/off-page pixels in the final remap — the "dark swoosh"
        // artifact this was built to catch.
        const int denseSteps = 50;
        var maxAbsOffset = 0.0;
        for (var i = 0; i <= denseSteps; i++)
        {
            var t = length * i / denseSteps;
            maxAbsOffset = Math.Max(maxAbsOffset, Math.Abs(EvalPoly(corrected, t)));
        }
        return new EdgeCurve(a, ux, uy, nx, ny, length, corrected, samples.Count, maxAbsOffset);
    }

    private static Point2f EvalEdgeCurve(EdgeCurve curve, double t01)
    {
        var t = t01 * curve.Length;
        var d = EvalPoly(curve.Coeffs, t);
        return new Point2f((float)(curve.A.X + t * curve.Ux + d * curve.Nx), (float)(curve.A.Y + t * curve.Uy + d * curve.Ny));
    }

    /// <summary>Rectifies a detected page by mapping all 4 of its actual edge *shapes* — not
    /// just 4 corners — onto the output rectangle's edges, via boundary (Coons-patch)
    /// interpolation: every output pixel is a bilinear blend of where the 4 fitted edge curves
    /// place it, corrected so the 4 corners land exactly on the rectangle's own corners. This
    /// is what <see cref="WarpQuad"/>'s single 4-point homography structurally cannot do — a
    /// homography can only exactly rectify a flat quad with straight edges, and confirmed on a
    /// real photo (Trapezoid_Image001's right half) that isn't enough: even after
    /// <see cref="RefineQuadCorners"/>'s straight-line-per-edge refinement, real edges under a
    /// steep camera angle are visibly *not* straight, so a corner-only correction leaves a
    /// residual bend no amount of corner nudging can remove.
    ///
    /// Returns null (never throws) whenever any of the 4 edges lacks enough evidence to trust a
    /// curve, or a fitted curve implies an implausibly large bend — callers should fall back to
    /// <see cref="RefineQuadCorners"/> + <see cref="WarpQuad"/> in that case, exactly as if this
    /// method didn't exist.</summary>
    private static Mat? RectifyWithBoundaryCurves(Mat src, Point2f[] quad, double bandPx, int minInliers, double maxOffsetFraction, double negligibleOffsetPx = 0.0)
    {
        if (quad.Length != 4) return null;
        try
        {
            var edgePoints = GetCannyEdgePoints(src);
            if (edgePoints.Length == 0) return null;

            var top = FitEdgeCurve(edgePoints, quad[0], quad[1], bandPx, minInliers);
            var bottom = FitEdgeCurve(edgePoints, quad[3], quad[2], bandPx, minInliers);
            var left = FitEdgeCurve(edgePoints, quad[0], quad[3], bandPx, minInliers);
            var right = FitEdgeCurve(edgePoints, quad[1], quad[2], bandPx, minInliers);
            if (top is not { } t || bottom is not { } bo || left is not { } le || right is not { } ri)
                return null;

            var width = Math.Max(1, (int)Math.Round(Math.Max(Distance(quad[0], quad[1]), Distance(quad[3], quad[2]))));
            var height = Math.Max(1, (int)Math.Round(Math.Max(Distance(quad[0], quad[3]), Distance(quad[1], quad[2]))));

            // A wildly bending fit (noise overfit past minInliers, not a real page edge) would
            // distort more than it corrects — same defensive spirit as RefineQuadCorners' own
            // area sanity gate.
            if (t.MaxAbsOffset > height * maxOffsetFraction || bo.MaxAbsOffset > height * maxOffsetFraction
                || le.MaxAbsOffset > width * maxOffsetFraction || ri.MaxAbsOffset > width * maxOffsetFraction)
                return null;

            // All 4 edges are already within noise-floor of straight — skip paying for a full
            // Cv2.Remap pass to "correct" what's indistinguishable from Canny jitter. Return the
            // unmodified source so the caller's own null-fallback path (WarpQuad) still runs,
            // but without this method itself compounding resample blur for nothing.
            if (negligibleOffsetPx > 0
                && t.MaxAbsOffset <= negligibleOffsetPx && bo.MaxAbsOffset <= negligibleOffsetPx
                && le.MaxAbsOffset <= negligibleOffsetPx && ri.MaxAbsOffset <= negligibleOffsetPx)
                return null;

            var (tl, tr, br, bl) = (quad[0], quad[1], quad[2], quad[3]);

            var topPts = new Point2f[width];
            var bottomPts = new Point2f[width];
            for (var x = 0; x < width; x++)
            {
                var u = width <= 1 ? 0.0 : x / (double)(width - 1);
                topPts[x] = EvalEdgeCurve(t, u);
                bottomPts[x] = EvalEdgeCurve(bo, u);
            }
            var leftPts = new Point2f[height];
            var rightPts = new Point2f[height];
            for (var y = 0; y < height; y++)
            {
                var v = height <= 1 ? 0.0 : y / (double)(height - 1);
                leftPts[y] = EvalEdgeCurve(le, v);
                rightPts[y] = EvalEdgeCurve(ri, v);
            }

            var mapXData = new float[height * width];
            var mapYData = new float[height * width];
            for (var y = 0; y < height; y++)
            {
                var v = height <= 1 ? 0.0 : y / (double)(height - 1);
                var (lpx, lpy) = (leftPts[y].X, leftPts[y].Y);
                var (rpx, rpy) = (rightPts[y].X, rightPts[y].Y);
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var u = width <= 1 ? 0.0 : x / (double)(width - 1);
                    var (tpx, tpy) = (topPts[x].X, topPts[x].Y);
                    var (bpx, bpy) = (bottomPts[x].X, bottomPts[x].Y);

                    var sx = (1 - v) * tpx + v * bpx + (1 - u) * lpx + u * rpx
                        - ((1 - u) * (1 - v) * tl.X + u * (1 - v) * tr.X + (1 - u) * v * bl.X + u * v * br.X);
                    var sy = (1 - v) * tpy + v * bpy + (1 - u) * lpy + u * rpy
                        - ((1 - u) * (1 - v) * tl.Y + u * (1 - v) * tr.Y + (1 - u) * v * bl.Y + u * v * br.Y);

                    mapXData[row + x] = (float)sx;
                    mapYData[row + x] = (float)sy;
                }
            }

            using var mapX = new Mat(height, width, MatType.CV_32FC1);
            using var mapY = new Mat(height, width, MatType.CV_32FC1);
            mapX.SetArray(mapXData);
            mapY.SetArray(mapYData);

            var warped = new Mat();
            Cv2.Remap(src, warped, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Replicate);
            return warped;
        }
        catch
        {
            return null;
        }
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
    private static GutterDetection DetectGutter(Mat src, double minFlankMarginFraction)
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

        // Reject a "dip" that's really just the search band's own boundary riding a one-sided
        // background-to-page transition (see GutterMinFlankMarginFraction doc comment) — a real
        // spine has genuine interior margin on both sides within the band.
        int marginPx = (int)(width * minFlankMarginFraction);
        bool hasBothFlanks = (minIdx - searchStart) >= marginPx && (searchEnd - minIdx) >= marginPx;
        if (!hasBothFlanks) confidence = 0;

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
            var gutter = DetectGutter(src, GutterMinFlankMarginFraction);
            if (gutter.Confidence < GutterConfidenceThreshold) return 50.0;
            return Math.Clamp(gutter.Fraction * 100.0, 1.0, 99.0);
        }
        catch
        {
            return 50.0;
        }
    }

    /// <summary>The real split-vs-single decision <see cref="Process"/> itself makes for a fresh
    /// (non-manual-override) capture — see the <c>autoSplit</c> local at the top of <see
    /// cref="Process"/>. Unlike <see cref="DetectGutterSplitPercent"/> (confidence only), this also
    /// checks <c>AbsoluteDropLevels</c>, so it's the only faithful way to know, from outside
    /// <see cref="Process"/>, whether Method 4 would treat a given image as a spread.</summary>
    public bool DetectAutoSplit(string imagePath, bool splitPagesRequested)
    {
        if (!File.Exists(imagePath)) return splitPagesRequested;
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return splitPagesRequested;
            var gutter = DetectGutter(src, GutterMinFlankMarginFraction);
            return splitPagesRequested
                || (gutter.Confidence >= GutterConfidenceThreshold && gutter.AbsoluteDropLevels >= GutterAbsoluteDropThreshold);
        }
        catch
        {
            return splitPagesRequested;
        }
    }

    /// <summary>Path-based wrapper around <see cref="AltDetectSpreadBoundary"/> for callers (Crop
    /// Review's preview) that only have a file path, not an OpenCvSharp <see cref="Mat"/> — mirrors
    /// the existing <see cref="DetectDocumentBoundary"/>/<see cref="DetectSplitPageBoundaries"/>
    /// path-based convention. Returns null on any load/decode failure.</summary>
    public AltSpreadBoundary? DetectSpreadBoundaryMethod4(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return null;
            return AltDetectSpreadBoundary(src);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Path-based wrapper around <see cref="AltDetectSinglePageBoundary"/>, mirroring
    /// <see cref="DetectSpreadBoundaryMethod4"/> for the single-page case. Returns null on any
    /// load/decode failure.</summary>
    public AltSinglePageBoundary? DetectSinglePageBoundaryMethod4(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return null;
            return AltDetectSinglePageBoundary(src);
        }
        catch
        {
            return null;
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

    /// <summary>The user's own proposed fix for residual interior curvature that boundary-curve
    /// rectification (<see cref="RectifyWithBoundaryCurves"/>) and the coarse top/bottom dewarp
    /// curve (<see cref="ApplyDewarp"/>) both leave behind: every text line should have "no
    /// change in Y across its own width," and a real photo's residual bow after those two steps
    /// turned out (confirmed against the user's own annotated screenshot, and by inspecting
    /// several real fixtures column-by-column) to be one *systematic, page-wide* drift shared by
    /// every line — not an independent per-line shape — exactly the same physical assumption
    /// <see cref="FitAveragedCurve"/> already makes for the coarse top/bottom curves, just
    /// applied here from every detected line's own residual instead of only the topmost/bottommost.
    ///
    /// First attempt at this built an independent per-line piecewise map (each line's own local
    /// samples as separate anchors, blended between lines per column). That produced ghosted,
    /// shredded output on real photos: DetectTextLineBlobs's blobs aren't reliably one-per-visual-row
    /// (a drop cap or indent can split one printed line into two blobs with overlapping X ranges),
    /// and — even after fixing that into a provably non-folding map — different lines cover
    /// different X sub-ranges, so the set of "covering" anchors changes column to column and the
    /// blended correction jumps discontinuously at those boundaries, visible as blocky seams.
    /// Pooling every line's row-normalized samples into ONE smooth fit (this method) sidesteps
    /// both failure modes by construction: one continuous function of X, no per-line boundaries,
    /// same proven technique and centered/conditioned <see cref="WeightedPolyFit"/> call
    /// <see cref="FitAveragedCurve"/> already uses safely on real photos.
    ///
    /// Pure per-column vertical shift (mapX untouched, same family as <see cref="ApplyDewarp"/>).
    /// Returns null (never throws, never partially applies) whenever there isn't enough
    /// trustworthy evidence — callers should keep the input unchanged in that case, exactly as
    /// if this step didn't run.</summary>
    private Mat? TryApplyLineMesh(Mat src, ProcessingResult result)
    {
        try
        {
            var (binary, lines) = DetectTextLineBlobs(src);
            using (binary)
            {
                if (lines.Count < MinMeshLines) return null;

                // Same pooling trick as FitAveragedCurve: each line contributes only its own
                // *shape* (samples minus that line's own median Y), not its absolute row
                // position, so lines at very different heights don't fight each other in one
                // fit — the fit becomes "the typical per-column bend," not "the average row."
                //
                // Uses the smoothed ink-centroid signal (SampleLineCentroid), not
                // SampleLineEdge's raw topmost-pixel-per-column: confirmed on a real photo that
                // the topmost-pixel signal is dominated by per-glyph noise (ascenders, capitals,
                // punctuation) at the same order of magnitude (tens of px) as the real residual
                // curve this is trying to measure, which a low-degree pooled fit legitimately
                // (and correctly) treats as unshared noise and averages away — so the fit found
                // almost nothing even though individual lines looked visibly bent. Centroid +
                // local smoothing suppresses that glyph-level jitter before it ever reaches the
                // fit.
                var pooled = new List<(double X, double Y, double W)>();
                var usableLines = 0;
                foreach (var line in lines)
                {
                    var samples = SampleLineCentroid(binary, line, MeshCentroidSmoothWindowPx);
                    if (samples.Count < MeshLineMinSamples) continue;
                    var sortedY = samples.Select(s => s.Y).OrderBy(y => y).ToList();
                    var medianY = sortedY[sortedY.Count / 2];
                    foreach (var s in samples) pooled.Add((s.X, s.Y - medianY, 1.0));
                    usableLines++;
                }
                if (usableLines < MinMeshLines) return null;

                var coeffs = WeightedPolyFit(pooled, MeshCurveDegree);
                if (coeffs == null) return null;

                var width = src.Cols;
                var height = src.Rows;

                // A fitted residual implying more correction than this fraction of the page's
                // own height is more likely an overfit/false-line artifact than real residual
                // curvature this late in the pipeline (after boundary rectification and the
                // coarse dewarp curve have already run) — decline rather than trust it, same
                // defensive spirit as every other evidence-gated fit in this file.
                var maxAbsDelta = 0.0;
                for (var x = 0; x < width; x++)
                    maxAbsDelta = Math.Max(maxAbsDelta, Math.Abs(EvalPoly(coeffs, x)));
                if (maxAbsDelta > height * MeshMaxDisplacementFraction)
                {
                    result.Warnings.Add($"Text-line mesh implies too large a correction ({maxAbsDelta:F0}px) — declining, likely a false line detection.");
                    return null;
                }

                var colDelta = new float[width];
                for (var x = 0; x < width; x++) colDelta[x] = (float)EvalPoly(coeffs, x);

                var mapXData = new float[height * width];
                var mapYData = new float[height * width];
                for (var y = 0; y < height; y++)
                {
                    var row = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        mapXData[row + x] = x;
                        mapYData[row + x] = y + colDelta[x];
                    }
                }

                using var mapX = new Mat(height, width, MatType.CV_32FC1);
                using var mapY = new Mat(height, width, MatType.CV_32FC1);
                mapX.SetArray(mapXData);
                mapY.SetArray(mapYData);

                var warped = new Mat();
                Cv2.Remap(src, warped, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Replicate);
                result.Warnings.Add($"Text-line mesh correction applied ({usableLines} lines, max {maxAbsDelta:F0}px).");
                return warped;
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Text-line mesh correction failed: {ex.Message}");
            return null;
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

    /// <summary>Per-column mean row of foreground pixels within <paramref name="rect"/>'s own
    /// rows, smoothed by averaging over <paramref name="smoothWindowPx"/> neighboring columns —
    /// a far more robust "where is this line, at this column" signal than
    /// <see cref="SampleLineEdge"/>'s topmost-foreground-pixel for detecting genuine sub-glyph-
    /// scale curvature (see <see cref="TryApplyLineMesh"/>): the topmost pixel jumps between a
    /// letter's cap-height and x-height depending purely on which glyph happens to sit at that
    /// column, noise at the same order of magnitude as the real curvature signal being measured;
    /// the ink centroid moves far less per glyph, and the smoothing window further suppresses
    /// what's left. Skips columns with no foreground pixel in range rather than inventing one.</summary>
    private static List<(double X, double Y)> SampleLineCentroid(Mat binary, Rect rect, int smoothWindowPx)
    {
        var yStart = Math.Max(0, rect.Y);
        var yEnd = Math.Min(binary.Rows - 1, rect.Y + rect.Height - 1);
        var xStart = Math.Max(0, rect.X);
        var xEnd = Math.Min(binary.Cols, rect.X + rect.Width);
        if (xEnd <= xStart) return new List<(double, double)>();

        var raw = new double?[xEnd - xStart];
        for (var x = xStart; x < xEnd; x++)
        {
            double sum = 0;
            var count = 0;
            for (var y = yStart; y <= yEnd; y++)
            {
                if (binary.At<byte>(y, x) == 0) continue;
                sum += y;
                count++;
            }
            if (count > 0) raw[x - xStart] = sum / count;
        }

        var samples = new List<(double, double)>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] is not { } _) continue;
            double sum = 0;
            var count = 0;
            for (var j = Math.Max(0, i - smoothWindowPx); j <= Math.Min(raw.Length - 1, i + smoothWindowPx); j++)
            {
                if (raw[j] is not { } v) continue;
                sum += v;
                count++;
            }
            samples.Add((xStart + i, sum / count));
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
                if (lines.Count < 3)
                {
                    result.Warnings.Add("Not enough text lines for line-based deskew — using whole-image line detection.");
                    return TryDeskewViaHough(src, result);
                }

                var lineAngles = new List<(double Y, double Slope, double AngleDeg)>();
                foreach (var line in lines)
                {
                    var samples = SampleLineEdge(binary, line, topEdge: true);
                    if (samples.Count < 8) continue;
                    var fit = WeightedPolyFit(samples.Select(s => (s.X, s.Y, 1.0)).ToList(), degree: 1);
                    if (fit == null) continue;
                    var slope = fit[1]; // dy/dx
                    lineAngles.Add((line.Y + line.Height / 2.0, slope, Math.Atan2(slope, 1.0) * 180.0 / Math.PI));
                }

                if (lineAngles.Count < 3)
                {
                    result.Warnings.Add("Not enough reliable text-line angle fits — using whole-image line detection for deskew.");
                    return TryDeskewViaHough(src, result);
                }

                var sortedAngles = lineAngles.Select(p => p.AngleDeg).OrderBy(a => a).ToList();
                var medianAngle = sortedAngles[sortedAngles.Count / 2];
                var iqr = sortedAngles[(int)(sortedAngles.Count * 0.75)] - sortedAngles[(int)(sortedAngles.Count * 0.25)];
                var iqrOk = iqr <= MaxDeskewAngleIqrDegrees;

                // Trustworthy, consistent angle evidence with a real rotation to apply — the
                // simple, cheap, well-tested case.
                if (iqrOk && Math.Abs(medianAngle) >= 0.1)
                    return ApplyDeskewAngle(src, medianAngle, result, "text-line");

                if (!iqrOk)
                    result.Warnings.Add($"Text-line angles inconsistent (IQR {iqr:F1}°) — checking for a per-line rotation trend before falling back.");

                // Inconsistent (or individually negligible) per-line angles don't rule out a
                // real defect a single global angle is structurally blind to: each line
                // rotated by its own amount that varies with vertical position (confirmed on a
                // real photo, Trapezoid_Image001's right half — individual lines were still
                // visibly tilted, by a *different* amount near the top than near the bottom,
                // even after separately fixing the page's own boundary shape and the block's
                // cross-line margin position). Try the broader rotation-field fit first, then
                // re-check for a residual margin shear on its result — the two correct
                // different things (orientation vs. position) and can coexist.
                var working = TryDeskewRotationField(src, lineAngles, result) ?? src;

                var (binary2, lines2) = DetectTextLineBlobs(working);
                using (binary2)
                {
                    if (TryDeskewShear(working, lines2, result) is { } sheared)
                        working = sheared;
                }

                if (!ReferenceEquals(working, src))
                    return working;

                if (!iqrOk)
                {
                    result.Warnings.Add("No confident rotation-field or margin-shear correction either — using whole-image line detection for deskew.");
                    return TryDeskewViaHough(src, result);
                }

                // IQR was trustworthy and reported no net rotation; neither of the other two
                // signals found anything either. Nothing to correct — not a fallback case.
                return src;
            }
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

    /// <summary>Detects and corrects a margin shear: each text line's own left edge drifting
    /// sideways as a near-linear function of its vertical position, distinct from both global
    /// rotation (<see cref="ApplyDeskewAngle"/>) and book-curve dewarp's per-line horizontal
    /// bulge. Fits the trend via Theil-Sen (median of pairwise slopes) rather than ordinary
    /// least squares — robust against indented paragraph-opening lines, which would otherwise
    /// bias a direct regression. Returns null (never corrects) unless enough lines actually
    /// follow the fitted trend; a shear this method invents from noise would be worse than
    /// leaving the page alone.</summary>
    private Mat? TryDeskewShear(Mat src, List<Rect> lines, ProcessingResult result)
    {
        if (lines.Count < MinShearLines) return null;

        var points = lines.Select(r => (Y: r.Y + r.Height / 2.0, X: (double)r.X)).ToList();
        var minYGap = src.Rows * 0.02;

        var slopes = new List<double>();
        for (var i = 0; i < points.Count; i++)
            for (var j = i + 1; j < points.Count; j++)
            {
                var dy = points[j].Y - points[i].Y;
                if (Math.Abs(dy) < minYGap) continue;
                slopes.Add((points[j].X - points[i].X) / dy);
            }
        if (slopes.Count == 0) return null;

        slopes.Sort();
        var k = slopes[slopes.Count / 2];

        var intercepts = points.Select(p => p.X - k * p.Y).OrderBy(v => v).ToList();
        var a = intercepts[intercepts.Count / 2];

        var inliers = points.Count(p => Math.Abs(p.X - (a + k * p.Y)) <= ShearInlierPx);
        var inlierFraction = inliers / (double)points.Count;
        if (inlierFraction < MinShearInlierFraction) return null;

        var angleDeg = Math.Atan(k) * 180.0 / Math.PI;
        result.OriginalSkewDegrees = angleDeg;
        if (Math.Abs(angleDeg) > MaxShearCorrectionDegrees)
        {
            result.Warnings.Add($"Margin shear too large ({angleDeg:F2}°, {inliers}/{points.Count} lines fit) — not auto-correcting.");
            return null;
        }
        if (Math.Abs(angleDeg) < MinShearCorrectionDegrees) return null;

        // Pivot at the single best-fit line (smallest residual to the trend), not the page's
        // geometric center. A shear is one straight-line correction, so the pivot choice
        // doesn't change whether it flattens the margin (it always does) — it only decides
        // which part of the page keeps its original position, shifting the rest. The already-
        // cropped page boundary is a real rectangle, and this content shear necessarily
        // disagrees with it somewhere; anchoring at the line the fit is most confident about
        // keeps that disagreement away from a line that may already have been fine (confirmed
        // on a real photo: pivoting at the center visibly tilted an already-flat top margin to
        // average against a genuinely drifting bottom one — anchoring at the best-supported
        // line instead of an arbitrary midpoint avoids guessing which end of the page is
        // "correct").
        var pivotY = points.MinBy(p => Math.Abs(p.X - (a + k * p.Y))).Y;

        var corrected = ApplyMarginShear(src, k, pivotY);
        result.WasDeskewed = true;
        result.AppliedCorrectionDegrees = angleDeg;
        result.Warnings.Add($"Margin shear corrected ({angleDeg:F2}°, {inliers}/{points.Count} text lines fit the trend).");
        return corrected;
    }

    /// <summary>Applies a horizontal shear that grows linearly with distance from
    /// <paramref name="yCenter"/> — removes a margin drift of <paramref name="k"/> pixels of X
    /// per pixel of Y, pivoted at the page's own vertical center (matching
    /// <see cref="ApplyDeskewAngle"/>'s rotation pivot) rather than at one edge, so the page
    /// stays centered instead of sliding sideways.</summary>
    private static Mat ApplyMarginShear(Mat src, double k, double yCenter)
    {
        var width = src.Cols;
        var height = src.Rows;
        var mapXData = new float[height * width];
        var mapYData = new float[height * width];
        for (var y = 0; y < height; y++)
        {
            var shift = (float)(k * (y - yCenter));
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                mapXData[row + x] = x + shift;
                mapYData[row + x] = y;
            }
        }

        using var mapX = new Mat(height, width, MatType.CV_32FC1);
        using var mapY = new Mat(height, width, MatType.CV_32FC1);
        mapX.SetArray(mapXData);
        mapY.SetArray(mapYData);

        var warped = new Mat();
        Cv2.Remap(src, warped, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.White);
        return warped;
    }

    /// <summary>Detects and corrects a rotation *field*: each text line's own orientation
    /// (not just its position) drifting as a near-linear function of vertical position — the
    /// defect a single global angle (<see cref="ApplyDeskewAngle"/>) and a margin shear
    /// (<see cref="TryDeskewShear"/>) are each structurally blind to, since neither ever makes
    /// a line's own content rotate: a global angle applies the *same* rotation everywhere, and
    /// a shear only ever moves X as a function of Y, never Y as a function of X — which is what
    /// an actual rotated line requires. Fits the trend via Theil-Sen (median of pairwise slope
    /// deltas) for the same reason <see cref="TryDeskewShear"/> does: robust against individual
    /// noisy per-line fits (short lines, glyph irregularity) that would bias a direct
    /// regression. Returns null unless enough lines actually follow the fitted trend.</summary>
    private Mat? TryDeskewRotationField(Mat src, List<(double Y, double Slope, double AngleDeg)> lineAngles, ProcessingResult result)
    {
        if (lineAngles.Count < MinRotationFieldLines) return null;

        var minYGap = src.Rows * 0.02;
        var slopeTrendCandidates = new List<double>();
        for (var i = 0; i < lineAngles.Count; i++)
            for (var j = i + 1; j < lineAngles.Count; j++)
            {
                var dy = lineAngles[j].Y - lineAngles[i].Y;
                if (Math.Abs(dy) < minYGap) continue;
                slopeTrendCandidates.Add((lineAngles[j].Slope - lineAngles[i].Slope) / dy);
            }
        if (slopeTrendCandidates.Count == 0) return null;

        slopeTrendCandidates.Sort();
        var b = slopeTrendCandidates[slopeTrendCandidates.Count / 2]; // d(slope)/dY

        var intercepts = lineAngles.Select(p => p.Slope - b * p.Y).OrderBy(v => v).ToList();
        var a = intercepts[intercepts.Count / 2]; // slope at Y=0

        var inliers = lineAngles.Count(p => Math.Abs(p.Slope - (a + b * p.Y)) <= RotationFieldInlierSlope);
        var inlierFraction = inliers / (double)lineAngles.Count;
        if (inlierFraction < MinRotationFieldInlierFraction) return null;

        var topY = lineAngles.Min(p => p.Y);
        var bottomY = lineAngles.Max(p => p.Y);
        var slopeAtTopDeg = Math.Atan(a + b * topY) * 180.0 / Math.PI;
        var slopeAtBottomDeg = Math.Atan(a + b * bottomY) * 180.0 / Math.PI;
        var rangeDeg = Math.Abs(slopeAtBottomDeg - slopeAtTopDeg);
        if (rangeDeg > MaxRotationFieldRangeDegrees)
        {
            result.Warnings.Add($"Rotation-field trend too large ({rangeDeg:F2}° span, {inliers}/{lineAngles.Count} lines fit) — not auto-correcting.");
            return null;
        }
        if (rangeDeg < MinRotationFieldRangeDegrees) return null;

        var xPivot = src.Cols / 2.0;
        var corrected = ApplyRotationField(src, a, b, xPivot);
        result.WasDeskewed = true;
        result.AppliedCorrectionDegrees = rangeDeg;
        result.Warnings.Add($"Per-line rotation trend corrected ({rangeDeg:F2}° span top-to-bottom, {inliers}/{lineAngles.Count} lines fit).");
        return corrected;
    }

    /// <summary>Applies a rotation whose angle varies linearly with row (<paramref name="a"/> +
    /// <paramref name="b"/> * y, both in dy/dx slope units), pivoted at
    /// <paramref name="xPivot"/> so the column that stays put is the page's own center —
    /// matching <see cref="ApplyDeskewAngle"/>'s rotation pivot — rather than one edge. Each
    /// row's own local slope is evaluated at its *output* Y as a first-order approximation
    /// (exact for a true small-angle field, which is what this is gated to via
    /// <see cref="MaxRotationFieldRangeDegrees"/>).</summary>
    private static Mat ApplyRotationField(Mat src, double a, double b, double xPivot)
    {
        var width = src.Cols;
        var height = src.Rows;
        var mapXData = new float[height * width];
        var mapYData = new float[height * width];
        for (var y = 0; y < height; y++)
        {
            var localSlope = a + b * y;
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                mapXData[row + x] = x;
                mapYData[row + x] = (float)(y + localSlope * (x - xPivot));
            }
        }

        using var mapX = new Mat(height, width, MatType.CV_32FC1);
        using var mapY = new Mat(height, width, MatType.CV_32FC1);
        mapX.SetArray(mapXData);
        mapY.SetArray(mapYData);

        var warped = new Mat();
        Cv2.Remap(src, warped, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.White);
        return warped;
    }

    // ───────────── FINGER / HAND REMOVAL ─────────────

    /// <summary>Automatically detects and inpaints away a finger/hand intruding on the page from
    /// outside the frame — the first half of the Phase-2 item deferred from the user's original
    /// three-item request (finger/hand removal + bleedthrough correction), started only now that
    /// the auto-split and keystone/curve work ahead of it has been done and reviewed.
    ///
    /// Runs after every geometric correction (crop, deskew, dewarp, text-line mesh) so it
    /// inpaints the final page geometry, not something that then gets warped again; runs before
    /// enhancement/sharpen so the inpainted patch doesn't pick up an unsharp halo, and before QC
    /// so blur/exposure scoring sees the cleaned page.
    ///
    /// The hard problem here isn't detecting skin color — it's not destroying real page content
    /// that happens to be skin-toned (a printed photo of a person is extremely common in real
    /// scanned material; one of this project's own real fixtures is a magazine page with a photo
    /// of swimmers). The single gate that separates the two reliably: a real finger physically
    /// enters the frame from *outside* the true page, so any leftover sliver of it in the final
    /// cropped output must still sit directly against that crop's own outer edge. A skin-toned
    /// region fully surrounded by real page content, however plausible its color, is never a
    /// finger — it's rejected here regardless of how finger-shaped it looks.
    ///
    /// Returns null (never throws, never partially applies) whenever there's no qualifying
    /// candidate — callers should keep the input unchanged in that case.</summary>
    private Mat? TryRemoveFingers(Mat src, ProcessingResult result)
    {
        try
        {
            using var skin = RawSkinMask(src, SkinCrLow, SkinCrHigh, SkinCbLow, SkinCbHigh);
            Cv2.FindContours(skin, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Length == 0) return null;

            var imageArea = src.Rows * (double)src.Cols;
            var minArea = imageArea * FingerMinAreaFraction;
            var maxArea = imageArea * FingerMaxAreaFraction;

            using var combinedMask = new Mat(src.Rows, src.Cols, MatType.CV_8UC1, Scalar.Black);
            var qualifying = 0;
            double totalArea = 0;
            foreach (var c in contours)
            {
                var area = Cv2.ContourArea(c);
                if (area < minArea || area > maxArea) continue;

                var rect = Cv2.BoundingRect(c);
                var touchesEdge = rect.X <= FingerEdgeTouchMarginPx
                    || rect.Y <= FingerEdgeTouchMarginPx
                    || rect.Right >= src.Cols - FingerEdgeTouchMarginPx
                    || rect.Bottom >= src.Rows - FingerEdgeTouchMarginPx;
                if (!touchesEdge) continue;

                Cv2.DrawContours(combinedMask, new[] { c }, -1, Scalar.White, thickness: -1);
                qualifying++;
                totalArea += area;
            }
            if (qualifying == 0) return null;

            // An aggregate check on top of the per-blob max: several separately-qualifying
            // regions could each pass individually while together implying an implausible
            // fraction of the page is skin — decline entirely rather than trust a majority-skin
            // page (more likely a systematic misclassification, e.g. very warm lighting, than
            // several real fingers).
            if (totalArea > maxArea)
            {
                result.Warnings.Add($"Finger/hand candidates cover too much of the frame ({totalArea / imageArea:P1}) — declining, likely a false detection.");
                return null;
            }

            using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(FingerMaskDilatePx * 2 + 1, FingerMaskDilatePx * 2 + 1));
            using var dilatedMask = new Mat();
            Cv2.Dilate(combinedMask, dilatedMask, dilateKernel);

            var inpainted = new Mat();
            Cv2.Inpaint(src, dilatedMask, inpainted, FingerInpaintRadiusPx, InpaintMethod.Telea);
            result.Warnings.Add($"Removed {qualifying} finger/hand region(s) touching the page edge ({totalArea / imageArea:P2} of frame).");
            return inpainted;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Finger/hand removal failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Byte-decoded entry point into <see cref="TryRemoveFingers"/> alone — for
    /// SmokeTest and manual diagnostics to check the finger-removal step's own pixel output in
    /// isolation, same pattern as <see cref="ApplyLineMeshFromBytes"/>.</summary>
    public byte[]? RemoveFingersFromBytes(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return null;
        var result = new ProcessingResult();
        using var cleaned = TryRemoveFingers(mat, result);
        if (cleaned == null) return null;
        Cv2.ImEncode(".png", cleaned, out var bytes);
        return bytes;
    }

    /// <summary>Reports what <see cref="TryRemoveFingers"/> would do to a page — every skin-color
    /// candidate blob's area and whether it qualifies (area range + edge-touching), without
    /// needing to run the full pipeline or actually inpaint anything.</summary>
    public string DebugFingerRemoval(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");

        using var skin = RawSkinMask(mat, SkinCrLow, SkinCrHigh, SkinCbLow, SkinCbHigh);
        Cv2.FindContours(skin, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        sb.AppendLine($"Skin-color candidate blobs: {contours.Length}");

        var imageArea = mat.Rows * (double)mat.Cols;
        var minArea = imageArea * FingerMinAreaFraction;
        var maxArea = imageArea * FingerMaxAreaFraction;
        var qualifying = 0;
        double totalArea = 0;
        foreach (var c in contours.OrderByDescending(c => Cv2.ContourArea(c)))
        {
            var area = Cv2.ContourArea(c);
            var rect = Cv2.BoundingRect(c);
            var touchesEdge = rect.X <= FingerEdgeTouchMarginPx
                || rect.Y <= FingerEdgeTouchMarginPx
                || rect.Right >= mat.Cols - FingerEdgeTouchMarginPx
                || rect.Bottom >= mat.Rows - FingerEdgeTouchMarginPx;
            var inAreaRange = area >= minArea && area <= maxArea;
            var qualifies = inAreaRange && touchesEdge;
            if (qualifies) { qualifying++; totalArea += area; }
            sb.AppendLine($"  rect=({rect.X},{rect.Y},{rect.Width}x{rect.Height}) area={area:F0} ({area / imageArea:P2}) touchesEdge={touchesEdge} inAreaRange={inAreaRange} -> {(qualifies ? "QUALIFIES" : "skipped")}");
        }
        sb.AppendLine($"Qualifying regions: {qualifying}, total area {totalArea / imageArea:P2} (max allowed {maxArea / imageArea:P2})");
        sb.AppendLine(qualifying > 0 && totalArea <= maxArea ? "Would REMOVE via inpainting." : "Would leave the page unchanged.");
        return sb.ToString();
    }

    // ───────────── BLEEDTHROUGH SUPPRESSION ─────────────

    /// <summary>Gates <see cref="ApplyBleedthroughSuppression"/> behind the explicit opt-in flag
    /// — see the tunables' own doc comment for why this, unlike every other Phase-1/2 correction
    /// in this file, is not attempted automatically.</summary>
    private Mat TryRemoveBleedthrough(Mat src, ProcessingResult result, bool bleedthroughEnabled)
    {
        if (!bleedthroughEnabled) return src;
        try
        {
            var cleaned = ApplyBleedthroughSuppression(src, BleedthroughBlurSigmaFraction, BleedthroughBlurSigmaMinPx, BleedthroughSuppressLowDepth, BleedthroughSuppressHighDepth);
            result.Warnings.Add("Bleedthrough/show-through suppression applied.");
            return cleaned;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Bleedthrough suppression failed: {ex.Message}");
            return src;
        }
    }

    /// <summary>Suppresses faint reverse-side ghosting while leaving real ink alone, via a local
    /// background estimate and a soft per-pixel depth threshold — see the tunables' own doc
    /// comment (<see cref="BleedthroughBlurSigmaFraction"/> etc.) for the full rationale. Works
    /// identically per color channel (no separate luminance step): the local-background blur
    /// already runs per-channel, and real ink is dark in every channel while paper-color
    /// bleedthrough is shallow in every channel, so treating channels independently doesn't
    /// introduce any color cast.</summary>
    private static Mat ApplyBleedthroughSuppression(Mat src, double blurSigmaFraction, double blurSigmaMinPx, double lowDepth, double highDepth)
    {
        var sigma = Math.Max(blurSigmaMinPx, Math.Min(src.Cols, src.Rows) * blurSigmaFraction);
        using var background = new Mat();
        Cv2.GaussianBlur(src, background, new Size(0, 0), sigmaX: sigma);

        // Mat.GetArray(out byte[]) only accepts single-channel Mats — reshape to a
        // single-channel (height, width*channels) view first, same pattern WriteTiff already
        // uses for a 3-channel BGR Mat (a metadata-only reinterpretation of the same row-major
        // buffer, so operating on it per-byte is still correct per-channel).
        using var srcFlat = src.Reshape(1, src.Rows);
        using var bgFlat = background.Reshape(1, src.Rows);
        srcFlat.GetArray(out byte[] srcPixels);
        bgFlat.GetArray(out byte[] bgPixels);
        var outPixels = new byte[srcPixels.Length];
        var range = highDepth - lowDepth;

        for (var i = 0; i < srcPixels.Length; i++)
        {
            var bg = bgPixels[i];
            var depth = bg - srcPixels[i]; // positive where darker than local background (real content); negative/zero left untouched
            if (depth <= 0)
            {
                outPixels[i] = srcPixels[i];
                continue;
            }

            var ramp = range > 0 ? Math.Clamp((depth - lowDepth) / range, 0.0, 1.0) : (depth >= highDepth ? 1.0 : 0.0);
            var suppressedDepth = depth * ramp;
            outPixels[i] = (byte)Math.Clamp(bg - suppressedDepth, 0, 255);
        }

        var result = new Mat(src.Rows, src.Cols, src.Type());
        using (var resultFlat = result.Reshape(1, src.Rows))
            resultFlat.SetArray(outPixels);
        return result;
    }

    /// <summary>Byte-decoded entry point into <see cref="ApplyBleedthroughSuppression"/> alone —
    /// for SmokeTest and manual diagnostics, same pattern as <see cref="ApplyLineMeshFromBytes"/>
    /// and <see cref="RemoveFingersFromBytes"/>. Always applies (bypasses the opt-in gate) since
    /// the point of this entry point is to inspect the algorithm itself.</summary>
    public byte[] ApplyBleedthroughSuppressionFromBytes(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        using var cleaned = ApplyBleedthroughSuppression(mat, BleedthroughBlurSigmaFraction, BleedthroughBlurSigmaMinPx, BleedthroughSuppressLowDepth, BleedthroughSuppressHighDepth);
        Cv2.ImEncode(".png", cleaned, out var bytes);
        return bytes;
    }

    /// <summary>Reports the local-background/depth statistics <see cref="ApplyBleedthroughSuppression"/>
    /// would act on for a page — depth histogram bucketed against the low/high thresholds — for
    /// judging whether the current tunables are conservative enough on a real photo without
    /// having to eyeball a full-resolution diff.</summary>
    public string DebugBleedthrough(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");

        var sigma = Math.Max(BleedthroughBlurSigmaMinPx, Math.Min(mat.Cols, mat.Rows) * BleedthroughBlurSigmaFraction);
        sb.AppendLine($"Background blur sigma: {sigma:F1}px");
        using var background = new Mat();
        Cv2.GaussianBlur(mat, background, new Size(0, 0), sigmaX: sigma);

        using var srcFlat = mat.Reshape(1, mat.Rows);
        using var bgFlat = background.Reshape(1, mat.Rows);
        srcFlat.GetArray(out byte[] srcPixels);
        bgFlat.GetArray(out byte[] bgPixels);

        long belowLow = 0, ramped = 0, aboveHigh = 0, noDepth = 0;
        double suppressedTotal = 0;
        for (var i = 0; i < srcPixels.Length; i++)
        {
            var depth = bgPixels[i] - srcPixels[i];
            if (depth <= 0) { noDepth++; continue; }
            if (depth < BleedthroughSuppressLowDepth) belowLow++;
            else if (depth > BleedthroughSuppressHighDepth) aboveHigh++;
            else ramped++;
            suppressedTotal += Math.Max(0, Math.Min(1, (depth - BleedthroughSuppressLowDepth) / (BleedthroughSuppressHighDepth - BleedthroughSuppressLowDepth))) * depth;
        }
        var total = srcPixels.Length;
        sb.AppendLine($"No depth (>= background): {noDepth} ({100.0 * noDepth / total:F1}%)");
        sb.AppendLine($"Fully suppressed (depth < {BleedthroughSuppressLowDepth}): {belowLow} ({100.0 * belowLow / total:F1}%)");
        sb.AppendLine($"Ramped (partial suppression): {ramped} ({100.0 * ramped / total:F1}%)");
        sb.AppendLine($"Untouched real ink (depth > {BleedthroughSuppressHighDepth}): {aboveHigh} ({100.0 * aboveHigh / total:F1}%)");
        sb.AppendLine($"Average suppressed depth per touched byte: {(suppressedTotal / Math.Max(1, belowLow + ramped)):F2}");
        return sb.ToString();
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

    // ───────────── MANUAL ADJUSTMENTS ─────────────

    /// <summary>Applies the operator-driven post-capture edit stack (Crop Review's Adjust
    /// mode) — rotate, flip, white balance, brightness/contrast, saturation, sharpness, in
    /// that fixed order. Runs after the automatic pipeline (crop/dewarp/CLAHE
    /// enhancement/unsharp-mask) and before the final TIFF write, so it corrects the
    /// pipeline's own output rather than feeding a different image into detection/QC. Order is
    /// deliberate and must not change: rotate/flip first since every later step assumes the
    /// final orientation; white balance before tone so a color cast doesn't skew the
    /// brightness/contrast read; saturation after tone since it's evaluated in the corrected
    /// image's own HSV space; sharpness last so it sharpens the final tonal result rather than
    /// being partially undone by a later contrast pass.</summary>
    /// <summary>Re-applies the adjustment stack directly on top of an already-processed
    /// derivative, in place — for a page whose raw original has already been deleted (see
    /// BatchExportService.DeleteOriginals, which runs once a batch is exported) and so can no
    /// longer go through the normal crop/dewarp/adjust-from-scratch pipeline in Process(). No
    /// crop, dewarp, QC, or CLAHE — just rotate/flip/tone/color/sharpen against whatever pixels
    /// are already there, overwriting the same file. Each call's adjustment values are a fresh
    /// delta applied to the derivative's current state (not restated from zero), so calling this
    /// twice compounds — e.g. two 90° rotations bake in as 180° total. That's the intended
    /// behavior for "edit an already-exported page again," not a bug: there is no original left
    /// to recompute the edit from scratch against.</summary>
    public static bool ReapplyAdjustmentsToDerivative(string derivativePath, int rotationDegrees, bool flipHorizontal, bool flipVertical, double brightness, double contrast, double saturation, double sharpness, double whiteBalance)
    {
        if (!File.Exists(derivativePath)) return false;
        try
        {
            using var src = Cv2.ImRead(derivativePath, ImreadModes.Color);
            if (src.Empty()) return false;

            using var adjusted = ApplyManualAdjustments(src, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);
            Cv2.ImWrite(derivativePath, adjusted);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ImageProcessor] ReapplyAdjustmentsToDerivative failed for '{derivativePath}': {ex}");
            return false;
        }
    }

    public static Mat ApplyManualAdjustments(Mat src, int rotationDegrees, bool flipHorizontal, bool flipVertical, double brightness, double contrast, double saturation, double sharpness, double whiteBalance)
    {
        var working = src.Clone();

        var normalizedRotation = AdjustmentGeometry.NormalizeRotation(rotationDegrees);
        if (normalizedRotation != 0)
        {
            var flag = normalizedRotation switch
            {
                90 => RotateFlags.Rotate90Clockwise,
                180 => RotateFlags.Rotate180,
                270 => RotateFlags.Rotate90Counterclockwise,
                _ => (RotateFlags?)null
            };
            if (flag.HasValue)
            {
                var rotated = new Mat();
                Cv2.Rotate(working, rotated, flag.Value);
                working.Dispose();
                working = rotated;
            }
        }

        if (flipHorizontal || flipVertical)
        {
            var flipMode = (flipHorizontal, flipVertical) switch
            {
                (true, true) => FlipMode.XY,
                (true, false) => FlipMode.Y,
                (false, true) => FlipMode.X,
                _ => (FlipMode?)null
            };
            if (flipMode.HasValue)
            {
                var flipped = new Mat();
                Cv2.Flip(working, flipped, flipMode.Value);
                working.Dispose();
                working = flipped;
            }
        }

        var clampedWhiteBalance = AdjustmentGeometry.ClampTone(whiteBalance);
        if (clampedWhiteBalance != 0)
        {
            // Simple global per-channel gain: warm (+1) boosts red/cuts blue, cool (-1) the
            // reverse. Split/scale/merge rather than a matrix — clearer, and this is a 3-
            // channel BGR Mat throughout this pipeline, never single-channel here.
            Cv2.Split(working, out var wbChannels);
            var redGain = 1.0 + clampedWhiteBalance * 0.3;
            var blueGain = 1.0 - clampedWhiteBalance * 0.3;
            wbChannels[2].ConvertTo(wbChannels[2], -1, redGain, 0); // R channel (BGR index 2)
            wbChannels[0].ConvertTo(wbChannels[0], -1, blueGain, 0); // B channel (BGR index 0)
            using var wbMerged = new Mat();
            Cv2.Merge(wbChannels, wbMerged);
            foreach (var ch in wbChannels) ch.Dispose();
            working.Dispose();
            working = wbMerged.Clone();
        }

        var clampedBrightness = AdjustmentGeometry.ClampTone(brightness);
        var clampedContrast = AdjustmentGeometry.ClampTone(contrast);
        if (clampedBrightness != 0 || clampedContrast != 0)
        {
            // Contrast scales around the mid-gray pivot (128), not a naive multiply from zero
            // — output = (input - 128) * contrastFactor + 128 + brightnessOffset, expressed as
            // ConvertTo's alpha/beta affine form. contrastFactor ranges roughly 0.5x..2x across
            // the [-1, 1] slider range; brightnessOffset is scaled to typical byte range.
            var contrastFactor = 1.0 + clampedContrast;
            var brightnessOffset = clampedBrightness * 80.0;
            var beta = 128.0 * (1.0 - contrastFactor) + brightnessOffset;
            var toned = new Mat();
            working.ConvertTo(toned, -1, contrastFactor, beta);
            working.Dispose();
            working = toned;
        }

        var clampedSaturation = AdjustmentGeometry.ClampTone(saturation);
        if (clampedSaturation != 0)
        {
            // Hue-safe saturation: scale the S channel in HSV space rather than scaling RGB
            // channels independently, which shifts hue — the difference between amateur and
            // correct-feeling saturation for a slider like this.
            using var hsv = new Mat();
            Cv2.CvtColor(working, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.Split(hsv, out var hsvChannels);
            var satFactor = 1.0 + clampedSaturation; // -1 -> 0 (grayscale), +1 -> 2x
            hsvChannels[1].ConvertTo(hsvChannels[1], -1, satFactor, 0);
            using var hsvMerged = new Mat();
            Cv2.Merge(hsvChannels, hsvMerged);
            foreach (var ch in hsvChannels) ch.Dispose();
            var saturated = new Mat();
            Cv2.CvtColor(hsvMerged, saturated, ColorConversionCodes.HSV2BGR);
            working.Dispose();
            working = saturated;
        }

        var clampedSharpness = AdjustmentGeometry.ClampSharpness(sharpness);
        if (clampedSharpness > 0)
        {
            // Same unsharp-mask technique as the pipeline's own fixed-strength Sharpen(), here
            // exposed as a variable-strength user slider instead of a fixed constant.
            using var blurred = new Mat();
            Cv2.GaussianBlur(working, blurred, new Size(0, 0), 2.0);
            var sharpened = new Mat();
            Cv2.AddWeighted(working, 1 + clampedSharpness, blurred, -clampedSharpness, 0, sharpened);
            working.Dispose();
            working = sharpened;
        }

        return working;
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

    // ───────────── DPI RESAMPLE ─────────────

    /// <summary>Resamples pixel dimensions so the output actually measures <paramref name="targetDpi"/>
    /// against the rig's real, physically-measured resolution (<paramref name="measuredDpi"/> —
    /// see <see cref="MeasuredDpi"/>), instead of the old behavior of scaling against the
    /// arbitrary <see cref="BaselineDpi"/> label. scale = targetDpi / measuredDpi, applied with
    /// NO floor — deliberately allowed (and expected) to downsample when the target DPI is below
    /// the rig's measured resolution, matching traditional scanner-style behavior, per confirmed
    /// product decision. (The old "never downsample" guard was built around BaselineDpi being a
    /// safe stand-in floor for "native resolution," which stops being true once DPI is a real
    /// physical measurement — downsampling below a genuinely-measured native resolution is a
    /// legitimate, requested capability, not lost detail by accident.) Runs after every
    /// geometric/enhancement step, right before binarization, so all of this class's other
    /// pixel-footprint-tuned steps (Sharpen's sigma, CLAHE tile size, edge-detection bands, etc.)
    /// keep operating at the resolution they were actually tuned against.</summary>
    // Hard backstop independent of MeasuredDpi's own sanity check (see MinPlausibleDpi/
    // MaxPlausibleDpi above) — even a targetDpi/measuredDpi ratio that slips through for some
    // other reason should never be allowed to demand a >8x linear resample in either direction.
    // Cubic-interpolating an already multi-megapixel Mat at, say, 80x per axis (a real value seen
    // from a single fat-fingered calibration entry) can exhaust memory or hang the background
    // worker; no legitimate AvailableDpiOptions-vs-real-rig-resolution combination needs more
    // than this.
    private const double MaxResampleScale = 8.0;

    private static Mat ResizeForDpi(Mat src, int targetDpi, double measuredDpi)
    {
        var scale = Math.Clamp(targetDpi / measuredDpi, 1.0 / MaxResampleScale, MaxResampleScale);
        var newWidth = Math.Max(1, (int)Math.Round(src.Cols * scale));
        var newHeight = Math.Max(1, (int)Math.Round(src.Rows * scale));

        var resized = new Mat();
        Cv2.Resize(src, resized, new Size(newWidth, newHeight), 0, 0, InterpolationFlags.Cubic);
        src.Dispose();
        return resized;
    }

    // ───────────── BINARIZATION ─────────────

    /// <summary>Applies Sauvola binarization if enabled, and reports what happened via
    /// <paramref name="result"/> — same never-throws, degrade-to-unchanged shape as
    /// <see cref="TryApplyDewarp"/>. Runs last in the pipeline (after QC, see
    /// <see cref="ProcessSinglePage"/>) since blur/exposure scores are meaningless once
    /// thresholded to pure black-and-white.</summary>
    private Mat TryApplyBinarization(Mat src, ProcessingResult result, bool binarizeEnabled, int dpi, double measuredDpi)
    {
        if (!binarizeEnabled) return src;
        try
        {
            var binarized = ApplySauvolaBinarization(src, dpi, measuredDpi);
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
    private static Mat ApplySauvolaBinarization(Mat src, int dpi, double measuredDpi)
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
        return DespeckleBinary(beforeDespeckle, dpi, measuredDpi);
    }

    /// <summary>Removes isolated foreground specks from a bilevel (0/255) image via connected-
    /// component filtering — the literature-recommended approach over median/morphological
    /// filtering specifically because it preserves genuine thin strokes while dropping isolated
    /// noise pixels, since it filters by connected blob size rather than a fixed spatial
    /// footprint. The area floor scales with DPI (~4px² at 300 DPI) since "how many pixels make
    /// up a real stroke fragment" is a resolution-relative question, not an absolute one.
    /// Scaled against <paramref name="measuredDpi"/> (the rig's real physically-measured
    /// resolution — see <see cref="MeasuredDpi"/>) rather than a hardcoded 300 or the old
    /// BaselineDpi stand-in, since <paramref name="dpi"/>/<paramref name="measuredDpi"/> is
    /// exactly the resample factor ResizeForDpi already applied to reach this point — that ratio,
    /// not the raw tag value, is what tracks the bilevel image's actual pixel density.</summary>
    private static Mat DespeckleBinary(Mat bilevel, int dpi, double measuredDpi)
    {
        using var inverted = new Mat();
        Cv2.BitwiseNot(bilevel, inverted); // Our convention: ink=0/white=255 — ConnectedComponents treats non-zero as foreground.

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var labelCount = Cv2.ConnectedComponentsWithStats(inverted, labels, stats, centroids, PixelConnectivity.Connectivity8);

        var scale = dpi / measuredDpi;
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
