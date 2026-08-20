using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>Canonical boundary-detection/split/flatten pipeline, ported from the research
/// prototype at tools/phaseA-prototype/boundary_prototype.ipynb (Method 4 side-edge detection +
/// cubic-bow single-pass remap — the product owner's designated mandatory reference
/// implementation). This is now the ONLY automatic (non-manual-override) boundary/dewarp path
/// ImageProcessor.Process/ProcessFixedFrames run — there used to be a per-batch opt-in toggle
/// (Batch.UseAltBoundaryPipeline) selecting between this and the original contour/text-line-blob
/// pipeline in ImageProcessor.cs; that toggle is gone, this file's pipeline replaced it as the
/// default, and the old pipeline's TryAutoCrop/TryDeskew/TryApplyDewarp/TryApplyLineMesh chain now
/// only runs for the manual-crop-override path (where the operator's own drawn quad already IS
/// the boundary, so Method 4 has nothing to auto-detect).
///
/// Core idea, shared by every method in this file: instead of finding a document's boundary as
/// a single 4-corner quad from a closed contour (the original pipeline's approach), trace each
/// of the page's 4 edges independently as a continuity-constrained walk over Sobel gradient
/// evidence — a text line's high-contrast edge is the same order of magnitude in the gradient as
/// the real page edge, so a free per-column/per-row argmax has no reason to prefer the true edge;
/// constraining each step to a small window around the previous step's accepted position is what
/// keeps a text line (or gutter shadow) from hijacking the trace in a single step, while still
/// letting a real finger occlusion smoothly drag the trace off the true edge over a short run
/// (handled separately — see <see cref="AltRejectAndBridgeLowConfidenceRuns"/>).</summary>
public partial class ImageProcessor
{
    // --- Phase 1 tunables (ported from the notebook's own hardcoded constants; kept as
    // instance properties, matching this file's existing tunable-property convention, so they
    // can be adjusted without a rebuild the same way GutterConfidenceThreshold etc. can). ---

    /// <summary>Rolling-mean window (px) used by <see cref="AltFindSustainedTransitionRow"/> to
    /// find a background-to-page brightness transition that's sustained, not a brief text-line
    /// dip bouncing back. Ported as-is from the notebook (`window=80`).</summary>
    public int AltTransitionWindowPx { get; set; } = 80;

    /// <summary>Fraction of the transition threshold's own [bg_level, page_level] span that must
    /// be crossed for a rolling-mean sample to count as "on the page." Ported as-is from the
    /// notebook (`high_thresh_frac=0.6`).</summary>
    public double AltTransitionThresholdFrac { get; set; } = 0.6;

    /// <summary>Seed-region bounds (as a fraction of image width) the top/bottom edge trace
    /// picks its starting column from — a region assumed to sit on a flat, reliable part of the
    /// edge. Ported as-is from the notebook (`Wf*0.35`.. `Wf*0.95`); this is an inherited rig
    /// assumption from the prototype's own source photos, not yet independently re-verified
    /// against this app's real fixtures — see Phase 2's validation gate.</summary>
    public double AltSeedRegionMinFraction { get; set; } = 0.35;
    public double AltSeedRegionMaxFraction { get; set; } = 0.95;

    /// <summary>Max row (top/bottom trace) or column (side trace) change allowed between
    /// adjacent steps of the continuity walk. Ported as-is from the notebook (`max_step=6`) —
    /// this is what makes the walk immune to a text line's/gutter's full-height jump: a text
    /// line's own edge sits far more than this many pixels away from the true page edge in a
    /// single column step, so it's structurally unreachable, while a finger occlusion genuinely
    /// sitting on the edge only ever shifts it a few px per step.</summary>
    public int AltMaxStepPx { get; set; } = 6;

    /// <summary>Margin (px) added beyond a seed column's own estimated edge row when bounding the
    /// seed's local Sobel-peak refinement search. Ported as-is from the notebook (top-edge
    /// specific: `edge_row_estimate + 150`).</summary>
    public int AltSeedSearchMarginPx { get; set; } = 150;

    /// <summary>Margin (px) searched *before* a seed column's own estimated edge row, for the
    /// seed's local Sobel-peak refinement. Ported as-is from the notebook (`edge_row_estimate -
    /// 100`).</summary>
    public int AltSeedSearchBackMarginPx { get; set; } = 100;

    /// <summary>Fraction of a traced edge's own median Sobel-peak strength below which a column
    /// (or row, for the side trace) is flagged low-confidence — typically a finger occlusion or
    /// glare patch, not the real page edge. Ported as-is from the notebook (`* 0.4`).</summary>
    public double AltLowConfidencePeakFraction { get; set; } = 0.4;

    /// <summary>One point of a traced edge, in the source image's own pixel coordinates.</summary>
    public readonly record struct AltEdgePoint(int Column, double Row);

    /// <summary>Finds where a 1D column (or row) of grayscale values rolls from background level
    /// to page level and *stays* there for a sustained run — immune to a short text-line dip
    /// bouncing back within <see cref="AltTransitionWindowPx"/>. Port of the notebook's
    /// `find_sustained_transition_row`/`find_bg_page_transition` (the same function; the
    /// notebook duplicates it with an asymmetric background-sampling rule for the row-direction
    /// side-edge case — see <see cref="AltFindBgPageTransition"/> in Phase 2 for that variant).
    ///
    /// `pageLevel` = median of the middle 60% of <paramref name="values"/> (assumes the page
    /// fills most of the slice's interior — true for a seed column/row chosen from a known-page
    /// region). `bgLevel` = the darker of the slice's own outer 20 samples at each end (assumes
    /// background is darker than page — true for every real fixture the notebook validated
    /// against, a black-background copy stand). A rolling mean over `window` samples must cross
    /// `bgLevel + (pageLevel - bgLevel) * thresholdFrac` and *stay* above it for the returned
    /// index to be trustworthy; a lone text-line dip is narrower than `window` so the rolling
    /// mean never dips back below threshold because of it alone.</summary>
    public static int AltFindSustainedTransitionRow(float[] values, int window, double thresholdFrac, bool fromStart)
    {
        var n = values.Length;
        if (n == 0) return 0;

        var midLo = (int)(n * 0.2);
        var midHi = (int)(n * 0.8);
        var pageLevel = Median(values, midLo, midHi);

        var tailLen = Math.Min(20, n);
        var bgLevel = Math.Min(Min(values, 0, tailLen), Min(values, n - tailLen, n));

        var threshold = bgLevel + (pageLevel - bgLevel) * thresholdFrac;

        var rolling = RollingMean(values, window);

        if (fromStart)
        {
            for (var i = 0; i < n; i++)
                if (rolling[i] > threshold) return i;
            return 0;
        }
        for (var i = n - 1; i >= 0; i--)
            if (rolling[i] > threshold) return i;
        return n - 1;
    }

    private static double Median(float[] values, int lo, int hi)
    {
        if (hi <= lo) return values.Length > 0 ? values[0] : 0.0;
        var slice = new float[hi - lo];
        Array.Copy(values, lo, slice, 0, hi - lo);
        Array.Sort(slice);
        var mid = slice.Length / 2;
        return slice.Length % 2 == 0 ? (slice[mid - 1] + slice[mid]) / 2.0 : slice[mid];
    }

    private static float Min(float[] values, int lo, int hi)
    {
        var m = float.MaxValue;
        for (var i = lo; i < hi; i++) if (values[i] < m) m = values[i];
        return m;
    }

    /// <summary>Centered rolling mean (matches numpy's `np.convolve(..., mode='same')` used by
    /// the notebook — each output sample is the mean of a `window`-wide slice centered as evenly
    /// as possible on it, clipped at the array's own edges rather than zero-padded, since a
    /// zero-padded edge would pull the rolling mean down near the boundary and could produce a
    /// spurious transition there).</summary>
    private static float[] RollingMean(float[] values, int window)
    {
        var n = values.Length;
        var result = new float[n];
        var half = window / 2;
        // Prefix-sum for O(n) instead of O(n*window).
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + values[i];
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(n, i - half + window);
            result[i] = (float)((prefix[hi] - prefix[lo]) / (hi - lo));
        }
        return result;
    }

    /// <summary>Continuity-constrained trace of the page's top or bottom edge across the full
    /// width of <paramref name="gy"/> (a Sobel-Gy gradient magnitude map — signed for top vs.
    /// bottom is handled by the caller passing the already-appropriate sign/abs convention, see
    /// <see cref="AltTraceTopBottomEdge"/>'s own caller). Port of the notebook's Cell 4B/4C.
    ///
    /// Seeds from a column in [<see cref="AltSeedRegionMinFraction"/>, AltSeedRegionMaxFraction]
    /// of width — assumed flat/reliable — refines to the strongest local gradient peak near that
    /// column's own <see cref="AltFindSustainedTransitionRow"/> estimate, then walks left and
    /// right one column at a time. Each step is a local argmax over a window of only
    /// ± <see cref="AltMaxStepPx"/> rows around the *previous* column's accepted row — this hard
    /// locality is what keeps a text line's own (comparably strong) gradient from hijacking the
    /// trace: reaching a text line several rows away would require a jump bigger than one step
    /// allows.</summary>
    public AltEdgePoint[] AltTraceTopBottomEdge(Mat img, Mat gy, bool fromTop)
    {
        var w = gy.Cols;
        var h = gy.Rows;
        gy.GetArray(out float[] gyFlat);

        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        gray.GetArray(out byte[] grayFlat);

        return AltTraceTopBottomEdgeCore(grayFlat, gyFlat, w, h, fromTop);
    }

    private AltEdgePoint[] AltTraceTopBottomEdgeCore(byte[] grayFlat, float[] gyFlat, int w, int h, bool fromTop)
    {
        float At(int col, int row) => gyFlat[row * w + col];

        var seedLo = (int)(w * AltSeedRegionMinFraction);
        var seedHi = (int)(w * AltSeedRegionMaxFraction);
        var seedCol = (seedLo + seedHi) / 2;

        // Seed-row estimate: same two-step process as the notebook — first a coarse
        // brightness-based transition-row estimate (AltFindSustainedTransitionRow, immune to a
        // single text line's dip since it requires a *sustained* run), which only bounds a small
        // search window; THEN refine within that narrow window to the strongest local |Gy| peak.
        // Skipping the transition-row estimate and searching a wide window directly for the
        // strongest |Gy| peak (an earlier version of this method did that) finds whatever
        // high-contrast text/diagram content happens to be strongest anywhere in that wide
        // window, not the true page edge — confirmed as a real bug via the altboundary overlay
        // on real fixtures (top edge tracking a paragraph of body text instead of the page's
        // physical top edge).
        var seedColumn = new float[h];
        for (var r = 0; r < h; r++) seedColumn[r] = grayFlat[r * w + seedCol];
        var transitionRow = AltFindSustainedTransitionRow(seedColumn, AltTransitionWindowPx, AltTransitionThresholdFrac, fromTop);

        int searchTop, searchBot;
        if (fromTop)
        {
            searchBot = Math.Min(h, transitionRow + AltSeedSearchMarginPx);
            searchTop = Math.Max(0, transitionRow - AltSeedSearchBackMarginPx);
        }
        else
        {
            searchTop = Math.Max(0, transitionRow - AltSeedSearchMarginPx);
            searchBot = Math.Min(h, transitionRow + AltSeedSearchBackMarginPx);
        }

        var seedRow = ArgMaxAbsRow(At, seedCol, searchTop, searchBot);

        var edge = new double[w];
        edge[seedCol] = seedRow;

        var prev = seedRow;
        for (var x = seedCol + 1; x < w; x++)
        {
            prev = StepArgMax(At, x, prev, AltMaxStepPx, 0, h);
            edge[x] = prev;
        }
        prev = seedRow;
        for (var x = seedCol - 1; x >= 0; x--)
        {
            prev = StepArgMax(At, x, prev, AltMaxStepPx, 0, h);
            edge[x] = prev;
        }

        var points = new AltEdgePoint[w];
        for (var x = 0; x < w; x++) points[x] = new AltEdgePoint(x, edge[x]);
        return points;
    }

    private static int ArgMaxAbsRow(Func<int, int, float> at, int col, int rowLo, int rowHi)
    {
        var best = rowLo;
        var bestVal = float.MinValue;
        for (var r = rowLo; r < rowHi; r++)
        {
            var v = Math.Abs(at(col, r));
            if (v > bestVal) { bestVal = v; best = r; }
        }
        return best;
    }

    private static int StepArgMax(Func<int, int, float> at, int col, int prevRow, int maxStep, int bound0, int bound1)
    {
        var lo = Math.Max(bound0, prevRow - maxStep);
        var hi = Math.Min(bound1, prevRow + maxStep + 1);
        var best = lo;
        var bestVal = float.MinValue;
        for (var r = lo; r < hi; r++)
        {
            var v = Math.Abs(at(col, r));
            if (v > bestVal) { bestVal = v; best = r; }
        }
        return best;
    }

    /// <summary>Per-column (or per-row, for the side trace) peak-strength confidence score for
    /// an already-traced edge, plus local straight-line bridging of any contiguous low-confidence
    /// run. Port of the notebook's Cell 4C finger-rejection logic.
    ///
    /// Deliberately NOT a global smooth/fit: an earlier attempt at globally fitting/smoothing
    /// the whole curve to handle finger occlusion flattened the real gutter V-notch into a wrong
    /// straight diagonal (the notch is a genuine, correct feature of the curve, not noise). This
    /// version scores each point's own accepted gradient-peak strength against the trace's own
    /// median peak strength; a finger breaks the clean background-to-paper contrast, so
    /// finger-covered points show up as anomalously weak. Only the flagged contiguous run gets
    /// replaced with a straight-line interpolation between its two trustworthy neighbors —
    /// everywhere else keeps the real traced curve untouched.</summary>
    public double[] AltRejectAndBridgeLowConfidenceRuns(AltEdgePoint[] edge, Mat gy)
    {
        var w = gy.Cols;
        gy.GetArray(out float[] gyFlat);
        var h = gy.Rows;
        float At(int col, int row) => gyFlat[Math.Clamp(row, 0, h - 1) * w + col];

        var n = edge.Length;
        var peakStrength = new double[n];
        for (var i = 0; i < n; i++)
            peakStrength[i] = Math.Abs(At(edge[i].Column, (int)Math.Round(edge[i].Row)));

        var medianStrength = Median(Array.ConvertAll(peakStrength, v => (float)v), 0, n);
        var confidenceThreshold = medianStrength * AltLowConfidencePeakFraction;

        var final = new double[n];
        for (var i = 0; i < n; i++) final[i] = edge[i].Row;

        var x = 0;
        while (x < n)
        {
            if (peakStrength[x] > confidenceThreshold) { x++; continue; }
            var runStart = x;
            while (x < n && peakStrength[x] <= confidenceThreshold) x++;
            var runEnd = x; // exclusive

            var loVal = runStart > 0 ? edge[runStart - 1].Row : edge[Math.Min(runEnd, n - 1)].Row;
            var hiVal = runEnd < n ? edge[runEnd].Row : edge[Math.Max(runStart - 1, 0)].Row;
            var runLen = runEnd - runStart;
            for (var k = 0; k < runLen; k++)
            {
                var t = (k + 1.0) / (runLen + 1.0);
                final[runStart + k] = loVal + t * (hiVal - loVal);
            }
        }

        return final;
    }

    // --- Phase 2 tunables (ported from the notebook's own hardcoded constants). ---

    /// <summary>Half-width (px) of the window searched around the seeded gutter-x for the
    /// top/bottom curves' own notch extremum. Ported as-is from the notebook
    /// (`search_half_width = 150`).</summary>
    public int AltGutterNotchSearchHalfWidthPx { get; set; } = 150;

    /// <summary>HSV-saturation threshold below which local color is considered washed out by
    /// glare and not trustworthy for the side-edge trace's Cb-chroma tiebreaker — a different
    /// axis than <see cref="SkinCrLow"/>/<see cref="SkinCbLow"/> etc. (those bound a skin-color
    /// region; this bounds "is color present at all here"). Ported as-is from the notebook
    /// (`glare_sat_thresh=25`). Do not conflate this with the skin-detection constants even
    /// though both live in YCrCb/HSV color space — they answer different questions.</summary>
    public double AltGlareSaturationThreshold { get; set; } = 25.0;

    /// <summary>One point of the gutter spine, connecting the top and bottom curves' own notch
    /// points with a straight line (see <see cref="AltDetectGutterNotch"/>).</summary>
    public readonly record struct AltGutterPoint(double Row, double Column);

    /// <summary>Result of <see cref="AltDetectGutterNotch"/>: the two notch anchor points found
    /// in the already-traced top/bottom curves, plus the straight line connecting them.</summary>
    public readonly record struct AltGutterDetection(AltEdgePoint TopNotch, AltEdgePoint BottomNotch, AltGutterPoint[] Line);

    /// <summary>Finds the book gutter as two anchor points, NOT a per-row search — an earlier
    /// per-row Gx search wandered by ~200px chasing local text/shadow contrast near the spine
    /// instead of the one true crease, because the gutter itself isn't a strong continuous edge
    /// to trace directly. Port of the notebook's Cell 5A.
    ///
    /// The already-traced top/bottom curves (<see cref="AltTraceTopBottomEdge"/>, after
    /// <see cref="AltRejectAndBridgeLowConfidenceRuns"/>) already show a clear V-notch dip at
    /// the spine by construction — both curves bend toward each other there. This finds that
    /// notch directly as a local extremum within a window around a seeded gutter column (top
    /// curve dips DOWN toward the gutter, i.e. a local *max* of row; bottom curve dips UP, a
    /// local *min* of row) and connects the two notch points with a straight line. The notebook's
    /// own comment on this: "only slightly curved — if this isn't curved enough visually, next
    /// step is a gentle quadratic bow" — this ships the straight-line version; a quadratic bow is
    /// explicitly deferred, not silently added.</summary>
    public AltGutterDetection AltDetectGutterNotch(double[] topEdge, double[] bottomEdge, int gutterSeedColumn)
    {
        var w = topEdge.Length;
        var loX = Math.Max(0, gutterSeedColumn - AltGutterNotchSearchHalfWidthPx);
        var hiX = Math.Min(w, gutterSeedColumn + AltGutterNotchSearchHalfWidthPx);

        var topNotchX = loX;
        var topNotchVal = double.MinValue;
        for (var x = loX; x < hiX; x++)
            if (topEdge[x] > topNotchVal) { topNotchVal = topEdge[x]; topNotchX = x; }

        var botNotchX = loX;
        var botNotchVal = double.MaxValue;
        for (var x = loX; x < hiX; x++)
            if (bottomEdge[x] < botNotchVal) { botNotchVal = bottomEdge[x]; botNotchX = x; }

        var topNotch = new AltEdgePoint(topNotchX, topNotchVal);
        var botNotch = new AltEdgePoint(botNotchX, botNotchVal);

        var nPts = Math.Max(1, (int)Math.Round(Math.Abs(botNotchVal - topNotchVal)));
        var line = new AltGutterPoint[nPts];
        for (var i = 0; i < nPts; i++)
        {
            var t = nPts <= 1 ? 0.0 : (double)i / (nPts - 1);
            var row = topNotchVal + t * (botNotchVal - topNotchVal);
            var col = topNotchX + t * (botNotchX - topNotchX);
            line[i] = new AltGutterPoint(row, col);
        }

        return new AltGutterDetection(topNotch, botNotch, line);
    }

    /// <summary>Row-direction analogue of <see cref="AltFindSustainedTransitionRow"/>, used to
    /// seed the side-edge trace. Port of the notebook's `find_bg_page_transition`.
    ///
    /// Deliberately asymmetric vs. the column version: background level is sampled ONLY from
    /// the slice's OUTER end (near column 0 for a left-edge search, near the far end for a
    /// right-edge search) — NOT both ends. The slice's inner end sits near the gutter/text
    /// block, not real background; folding it into a min() the way the column version does
    /// would let dark ink text corrupt the background-level estimate.</summary>
    public static int AltFindBgPageTransition(float[] rowSlice, int window, double thresholdFrac, bool fromLeft)
    {
        var n = rowSlice.Length;
        if (n == 0) return 0;

        var midLo = (int)(n * 0.2);
        var midHi = (int)(n * 0.8);
        var pageLevel = Median(rowSlice, midLo, midHi);

        var tailLen = Math.Min(20, n);
        var bgLevel = fromLeft ? Min(rowSlice, 0, tailLen) : Min(rowSlice, n - tailLen, n);

        var threshold = bgLevel + (pageLevel - bgLevel) * thresholdFrac;
        var rolling = RollingMean(rowSlice, window);

        if (fromLeft)
        {
            for (var i = 0; i < n; i++)
                if (rolling[i] > threshold) return i;
            return 0;
        }
        for (var i = n - 1; i >= 0; i--)
            if (rolling[i] > threshold) return i;
        return n - 1;
    }

    /// <summary>Result of <see cref="AltTraceSideEdge"/>: the traced edge (one column-position
    /// per row across [rowLo, rowHi)), plus diagnostic counts for how many rows needed bridging
    /// and how many of those were recovered via the chroma tiebreaker vs. a plain straight-line
    /// bridge.</summary>
    public readonly record struct AltSideEdgeTrace(double[] Columns, int RowLo, int LowConfidenceRows, int GlareChromaRecovered);

    /// <summary>Continuity-constrained trace of the page's left or right edge — the SAME
    /// technique as <see cref="AltTraceTopBottomEdge"/> rotated 90°: walks row-by-row (not
    /// column-by-column) using Sobel Gx (not Gy), searching for the background<->page brightness
    /// transition in x. Port of the notebook's Cell 6A (`trace_side_edge`).
    ///
    /// This replaced two earlier failed approaches: a YCrCb Cb-valley detector (glare on glossy
    /// pages washes out chroma right at the physical edge, since angled rig lighting tends to
    /// hit the spine/edge hardest — exactly where a color-based estimator has nothing reliable
    /// left) and a plain ink-threshold approach (couldn't tell background from ink). The
    /// resulting design is a deliberate three-tier fallback for low-confidence (weak Gx peak)
    /// runs: (1) brightness/Gx gradient, primary — same continuity-walk mechanism as top/bottom;
    /// (2) a glare-gated Cb-chroma re-estimate, tried ONLY where local HSV saturation says color
    /// is still trustworthy there (<see cref="AltGlareSaturationThreshold"/>); (3) a plain
    /// straight-line bridge across the run if saturation has ALSO collapsed there — meaning
    /// glare washed out color too, so corrupted chroma must not be trusted, same principle as
    /// the top-edge's finger-bridging.</summary>
    public AltSideEdgeTrace AltTraceSideEdge(Mat img, Mat gx, int rowLo, int rowHi, int colLo, int colHi, bool fromLeft)
    {
        var w = img.Cols;
        var h = img.Rows;
        // Defensive clamp: a caller-provided row/col range (e.g. derived from a Sav-Gol-smoothed
        // curve, which can overshoot slightly right at a column's own edge) must never exceed
        // the actual image bounds this method indexes into.
        rowLo = Math.Clamp(rowLo, 0, h - 1);
        rowHi = Math.Clamp(rowHi, rowLo + 1, h);
        colLo = Math.Clamp(colLo, 0, w - 1);
        colHi = Math.Clamp(colHi, colLo + 1, w);

        gx.GetArray(out float[] gxFlat);

        using var ycrcb = new Mat();
        Cv2.CvtColor(img, ycrcb, ColorConversionCodes.BGR2YCrCb);
        using var cbMat = new Mat();
        Cv2.ExtractChannel(ycrcb, cbMat, 2);
        cbMat.GetArray(out byte[] cbFlat);

        using var hsv = new Mat();
        Cv2.CvtColor(img, hsv, ColorConversionCodes.BGR2HSV);
        using var satMat = new Mat();
        Cv2.ExtractChannel(hsv, satMat, 1);
        satMat.GetArray(out byte[] satFlat);

        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        gray.GetArray(out byte[] grayFlat);

        var n = rowHi - rowLo;
        var width = colHi - colLo;

        float GxAt(int row, int localCol) => gxFlat[row * w + (colLo + localCol)];
        byte GrayAt(int row, int localCol) => grayFlat[row * w + (colLo + localCol)];

        var seedRow = rowLo + n / 2;
        var rowSlice = new float[width];
        for (var c = 0; c < width; c++) rowSlice[c] = GrayAt(seedRow, c);
        var xEst = AltFindBgPageTransition(rowSlice, AltTransitionWindowPx, AltTransitionThresholdFrac, fromLeft);

        var sLo = Math.Max(0, xEst - 100);
        var sHi = Math.Min(width, xEst + 150);
        var seedCol = sLo;
        var seedBest = float.MinValue;
        for (var c = sLo; c < sHi; c++)
        {
            var v = Math.Abs(GxAt(seedRow, c));
            if (v > seedBest) { seedBest = v; seedCol = c; }
        }

        var edge = new int[n];
        edge[seedRow - rowLo] = seedCol;

        int StepCol(int row, int prevCol)
        {
            var lo = Math.Max(0, prevCol - AltMaxStepPx);
            var hi = Math.Min(width, prevCol + AltMaxStepPx + 1);
            var best = lo;
            var bestVal = float.MinValue;
            for (var c = lo; c < hi; c++)
            {
                var v = Math.Abs(GxAt(row, c));
                if (v > bestVal) { bestVal = v; best = c; }
            }
            return best;
        }

        var prev = seedCol;
        for (var row = seedRow + 1; row < rowHi; row++) { prev = StepCol(row, prev); edge[row - rowLo] = prev; }
        prev = seedCol;
        for (var row = seedRow - 1; row >= rowLo; row--) { prev = StepCol(row, prev); edge[row - rowLo] = prev; }

        var peakStrength = new double[n];
        for (var i = 0; i < n; i++) peakStrength[i] = Math.Abs(GxAt(rowLo + i, edge[i]));
        var medianStrength = Median(Array.ConvertAll(peakStrength, v => (float)v), 0, n);
        var good = new bool[n];
        for (var i = 0; i < n; i++) good[i] = peakStrength[i] > medianStrength * AltLowConfidencePeakFraction;

        var final = new double[n];
        for (var i = 0; i < n; i++) final[i] = edge[i];

        var lowConfidenceCount = 0;
        var glareRecovered = 0;
        var idx = 0;
        while (idx < n)
        {
            if (good[idx]) { idx++; continue; }
            var runStart = idx;
            while (idx < n && !good[idx]) idx++;
            var runEnd = idx;
            lowConfidenceCount += runEnd - runStart;

            var loVal = runStart > 0 ? edge[runStart - 1] : edge[Math.Min(runEnd, n - 1)];
            var hiVal = runEnd < n ? edge[runEnd] : edge[Math.Max(runStart - 1, 0)];
            var runLen = runEnd - runStart;

            for (var k = 0; k < runLen; k++)
            {
                var t = (k + 1.0) / (runLen + 1.0);
                var straight = loVal + t * (hiVal - loVal);
                final[runStart + k] = straight;

                var row = rowLo + runStart + k;
                var xGuess = Math.Clamp((int)Math.Round(straight), 0, width - 1);
                var satHere = satFlat[row * w + (colLo + xGuess)];
                if (satHere <= AltGlareSaturationThreshold) continue; // glare washed out color too — keep straight bridge

                var winLo = Math.Max(0, (int)Math.Round(straight) - AltMaxStepPx * 3);
                var winHi = Math.Min(width, (int)Math.Round(straight) + AltMaxStepPx * 3 + 1);
                double bgCb;
                if (fromLeft)
                {
                    if (winLo <= 0) continue;
                    bgCb = MedianByte(cbFlat, row * w + colLo, row * w + colLo + winLo);
                }
                else
                {
                    if (winHi >= width) continue;
                    bgCb = MedianByte(cbFlat, row * w + colLo + winHi, row * w + colHi);
                }

                var bestC = winLo;
                var bestDiff = double.MinValue;
                for (var c = winLo; c < winHi; c++)
                {
                    var diff = Math.Abs(cbFlat[row * w + colLo + c] - bgCb);
                    if (diff > bestDiff) { bestDiff = diff; bestC = c; }
                }
                final[runStart + k] = bestC;
                glareRecovered++;
            }
        }

        var columns = new double[n];
        for (var i = 0; i < n; i++) columns[i] = final[i] + colLo;
        return new AltSideEdgeTrace(columns, rowLo, lowConfidenceCount, glareRecovered);
    }

    private static double MedianByte(byte[] values, int lo, int hi)
    {
        if (hi <= lo) return 0;
        var slice = new byte[hi - lo];
        Array.Copy(values, lo, slice, 0, hi - lo);
        Array.Sort(slice);
        var mid = slice.Length / 2;
        return slice.Length % 2 == 0 ? (slice[mid - 1] + slice[mid]) / 2.0 : slice[mid];
    }

    // ============================================================================
    // METHOD 4: Gx sign-change count + gutter-anchored span + RANSAC-guided
    // continuity walk + finger bridging + strength/straightness widen-retry.
    //
    // Port of the notebook's Method 4 cells (search boundary_prototype.ipynb for
    // "Method 4: Gx sign-change count per column" and "signchange_m4"). This is the
    // notebook's own designated side-boundary detector for the real pipeline — Cell
    // 6A (AltTraceSideEdge/AltFindBgPageTransition above) is explicitly marked
    // "diagnostic/baseline only" in the notebook's own comments and is NOT used by
    // this method or by the flatten pipeline once Method 4 is wired in (see
    // AltDetectSpreadBoundary/AltFlattenSinglePage below, which now call
    // AltTraceSideEdgeMethod4 instead of AltTraceSideEdge).
    //
    // Method 4 replaces Cell 6A's brightness-Otsu book-span + plain-seeded walk with:
    //   1. A per-column count of how many times Gx changes sign within the page's
    //      already-known vertical extent (top/bottom trace) — text produces frequent
    //      sign alternation, flat background almost none. Purely structural, immune
    //      to absolute brightness/exposure (unlike Cell 6A's brightness-Otsu split).
    //   2. Otsu-split that count into a page/not-page mask, then find the book's
    //      x-span by walking OUTWARD FROM THE GUTTER (not "widest run wins" — a
    //      dense interior blob, e.g. a photo or dense text block, can out-mass the
    //      real span otherwise).
    //   3. A RANSAC-guided continuity walk (reusing collect_ransac_candidates/
    //      ransac_line_fit's C# ports below and the same trace_side_edge machinery
    //      AltTraceSideEdge implements) seeded from that span, with a diagram/
    //      line-art exclusion pass (Hough-detected near-vertical line segments are
    //      excluded from the RANSAC candidate set — a flowchart/box border is long,
    //      straight, and high-contrast, exactly what RANSAC treats as trustworthy,
    //      but sits inside the page, not at the true margin).
    //   4. The same finger-run bridging technique as the top/bottom trace, applied
    //      to the vertical walk.
    //   5. A widen-and-retry loop: each side must be BOTH straight (low residual
    //      std against its own best-fit line) AND strong (median |Gx| at its own
    //      accepted points, relative to whichever side is currently stronger) — a
    //      confidently-wrong trace (e.g. locked onto an interior illustration
    //      border) can be straight and symmetric while still not being the true
    //      page edge, which strength alone catches. A side that fails widens its
    //      OWN search margin (not both sides) and re-runs RANSAC+walk+bridge, up to
    //      the image edge, where it stops and flags rather than looping forever.
    // ============================================================================

    /// <summary>Robust straight-line fit x = m*y + b through (row, col) points via RANSAC —
    /// many random 2-point line hypotheses, keeping the one with the most inliers, then a
    /// least-squares refit through just those inliers. Port of the notebook's
    /// `ransac_line_fit`. Returns null slope/intercept if there are too few points or the best
    /// hypothesis has fewer than 2 inliers (mirrors the notebook's `(None, None)` return).
    /// Deterministic (fixed seed, matching the notebook's own `seed=0` default) so a page's
    /// detected boundary doesn't jitter between runs of the same photo.</summary>
    public readonly record struct RansacLineResult(double? Slope, double? Intercept);

    public static RansacLineResult RansacLineFit((double Row, double Col)[] points, int nIter = 300, double inlierThresh = 4.0, int seed = 0)
    {
        if (points.Length < 2) return new RansacLineResult(null, null);
        var rng = new Random(seed);

        bool[]? bestInliers = null;
        var bestCount = -1;

        for (var iter = 0; iter < nIter; iter++)
        {
            var i1 = rng.Next(points.Length);
            int i2;
            do { i2 = rng.Next(points.Length); } while (i2 == i1 && points.Length > 1);
            var (y1, x1) = points[i1];
            var (y2, x2) = points[i2];
            if (y1 == y2) continue;

            var m = (x2 - x1) / (y2 - y1);
            var b = x1 - m * y1;

            var inliers = new bool[points.Length];
            var count = 0;
            for (var i = 0; i < points.Length; i++)
            {
                var predX = m * points[i].Row + b;
                if (Math.Abs(points[i].Col - predX) < inlierThresh) { inliers[i] = true; count++; }
            }
            if (count > bestCount) { bestCount = count; bestInliers = inliers; }
        }

        if (bestInliers == null || bestCount < 2) return new RansacLineResult(null, null);

        // Least-squares refit through inliers only (matches np.polyfit(inlier_ys, inlier_xs, 1)).
        double sumY = 0, sumX = 0, sumYY = 0, sumYX = 0;
        var n = 0;
        for (var i = 0; i < points.Length; i++)
        {
            if (!bestInliers[i]) continue;
            var (y, x) = points[i];
            sumY += y; sumX += x; sumYY += y * y; sumYX += y * x;
            n++;
        }
        var denom = n * sumYY - sumY * sumY;
        if (Math.Abs(denom) < 1e-12) return new RansacLineResult(null, null);
        var slope = (n * sumYX - sumY * sumX) / denom;
        var intercept = (sumX - slope * sumY) / n;
        return new RansacLineResult(slope, intercept);
    }

    /// <summary>Strongest |Gx| peak per sampled row within [colLo, colHi), one candidate every
    /// <paramref name="step"/> rows. Port of the notebook's `collect_ransac_candidates`
    /// (`collect_ransac_candidates_excluding` when <paramref name="excludeMask"/> is supplied —
    /// same function, the notebook just adds one parameter, so this is that combined form; pass
    /// a null/all-false mask for the plain Cell 6A behavior).</summary>
    public static (double Row, double Col)[] CollectRansacCandidates(Mat gx, int rowLo, int rowHi, int colLo, int colHi, bool[]? excludeMask = null, int excludeMaskRowOffset = 0, int step = 3)
    {
        var w = gx.Cols;
        gx.GetArray(out float[] gxFlat);
        var points = new List<(double, double)>();
        for (var y = rowLo; y < rowHi; y += step)
        {
            if (excludeMask != null)
            {
                var idx = y - excludeMaskRowOffset;
                if (idx >= 0 && idx < excludeMask.Length && excludeMask[idx]) continue;
            }
            if (colHi <= colLo) continue;
            var best = colLo;
            var bestVal = float.MinValue;
            for (var c = colLo; c < colHi; c++)
            {
                var v = Math.Abs(gxFlat[y * w + c]);
                if (v > bestVal) { bestVal = v; best = c; }
            }
            points.Add((y, best));
        }
        return points.ToArray();
    }

    /// <summary>Marks rows (relative to <paramref name="rowLo"/>) that intersect a long,
    /// near-vertical straight line segment detected via Hough transform within
    /// [<paramref name="colLo"/>, <paramref name="colHi"/>) — i.e. rows likely to contain
    /// diagram/line-art edges (a flowchart box border, a printed rule) rather than the true page
    /// margin. Port of the notebook's `detect_diagram_line_mask`. A diagram's box edge is long,
    /// straight, and high-contrast — exactly what RANSAC treats as a trustworthy signal — but it
    /// sits well inside the page, not at the true outer edge; excluding these rows from the
    /// RANSAC candidate set (see <see cref="AltTraceSideEdgeMethod4"/>) keeps a mostly-blank
    /// page's sparse true-edge signal from being out-voted by a diagram's self-consistent
    /// interior line.</summary>
    public static bool[] DetectDiagramLineMask(Mat gray, int rowLo, int rowHi, int colLo, int colHi, double minLineLengthFrac = 0.05)
    {
        var n = rowHi - rowLo;
        var mask = new bool[Math.Max(0, n)];
        if (n <= 0 || colHi <= colLo) return mask;

        using var band = new Mat(gray, new Rect(colLo, rowLo, colHi - colLo, rowHi - rowLo));
        using var edges = new Mat();
        Cv2.Canny(band, edges, 50, 150);
        var minLen = Math.Max(15, (int)(n * minLineLengthFrac));

        var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, threshold: 40, minLineLength: minLen, maxLineGap: 5);
        foreach (var line in lines)
        {
            var x1 = line.P1.X; var y1 = line.P1.Y; var x2 = line.P2.X; var y2 = line.P2.Y;
            var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            if (length < minLen) continue;
            if (Math.Abs(x2 - x1) > Math.Abs(y2 - y1) * 0.3) continue; // not near-vertical enough
            var yLo = Math.Max(0, Math.Min(y1, y2));
            var yHi = Math.Min(n, Math.Max(y1, y2) + 1);
            for (var y = yLo; y < yHi; y++) mask[y] = true;
        }
        return mask;
    }

    /// <summary>Per-column count of Gx sign changes within the page's already-known vertical
    /// extent (the top/bottom trace, trimmed 10% top/bottom to avoid fingers/page-edge
    /// artifacts) — text produces frequent dark&lt;-&gt;light alternation, flat background
    /// almost none. Port of the notebook's Method 4 profile computation (search
    /// "signchange_m4"). Purely structural (counts sign flips, not gradient magnitude), so it's
    /// immune to absolute brightness/exposure the way Cell 6A's median-brightness approach is
    /// not — confirmed in the notebook's own comments as the reason Method 4 replaces Cell 6A's
    /// approach (a dark-toned photo on the page reads as "background" to a pure brightness
    /// split, but its sign-change count still reads as "page" here).</summary>
    public readonly record struct SignChangeProfile(float[] SignChanges, bool[] Valid);

    public static SignChangeProfile ComputeSignChangeProfile(Mat gx, double[] topY, double[] botY)
    {
        var w = gx.Cols;
        var h = gx.Rows;
        gx.GetArray(out float[] gxFlat);

        var signChanges = new float[w];
        var valid = new bool[w];

        for (var x = 0; x < w; x++)
        {
            var top = Math.Clamp((int)Math.Round(topY[x]), 0, h - 1);
            var bot = Math.Clamp((int)Math.Round(botY[x]), 0, h);
            if (bot <= top + 10) continue;
            var hgt = bot - top;
            var trim = Math.Max(3, (int)(hgt * 0.10));
            var y1 = top + trim;
            var y2 = bot - trim;
            if (y2 <= y1) continue;

            var count = 0;
            var haveSign = false;
            var prevSign = 0;
            for (var y = y1; y < y2; y++)
            {
                var v = gxFlat[y * w + x];
                var sign = v > 0 ? 1 : v < 0 ? -1 : 0;
                if (sign == 0) continue; // np.sign(...)==0 samples are dropped before np.diff
                if (haveSign && sign != prevSign) count++;
                prevSign = sign;
                haveSign = true;
            }
            // Notebook requires >= 2 nonzero-sign samples to compute np.diff at all.
            var nonzeroCount = 0;
            for (var y = y1; y < y2; y++) if (gxFlat[y * w + x] != 0) nonzeroCount++;
            if (nonzeroCount < 2) continue;

            signChanges[x] = count;
            valid[x] = hgt > 20;
        }

        return new SignChangeProfile(signChanges, valid);
    }

    /// <summary>Otsu threshold on the valid portion of an arbitrary scalar per-column profile —
    /// works on any profile, not just brightness (Method 4 applies this to the sign-change
    /// count). Port of the notebook's `otsu_split`: scales the valid values into 0-255,
    /// computes cv2's Otsu threshold on that, then maps it back to the profile's own scale.</summary>
    public static double OtsuSplit(float[] profile, bool[] validMask)
    {
        var valid = new List<float>();
        for (var i = 0; i < profile.Length; i++) if (validMask[i]) valid.Add(profile[i]);
        if (valid.Count < 20) throw new InvalidOperationException("Not enough valid columns for Otsu split.");

        var lo = valid.Min();
        var hi = valid.Max();
        if (hi <= lo) throw new InvalidOperationException("Profile has no dynamic range to split.");

        using var scaledMat = new Mat(1, valid.Count, MatType.CV_8UC1);
        var scaledBytes = new byte[valid.Count];
        for (var i = 0; i < valid.Count; i++)
            scaledBytes[i] = (byte)Math.Clamp((valid[i] - lo) / (hi - lo) * 255.0, 0, 255);
        scaledMat.SetArray(scaledBytes);

        using var dst = new Mat();
        var tU8 = Cv2.Threshold(scaledMat, dst, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        return lo + (tU8 / 255.0) * (hi - lo);
    }

    /// <summary>Rolling-majority cleanup of a 1D boolean mask (same technique as Cell 6A step 7
    /// / <see cref="OtsuSplit"/>'s caller uses for the brightness mask) — a sample survives only
    /// if at least half of a <c>max(5, w*0.01)</c>-wide (forced odd) window centered on it is
    /// also set. Port of the notebook's `clean_mask_1d`.</summary>
    public static bool[] CleanMask1D(bool[] mask, int w)
    {
        var kernelWidth = Math.Max(5, (int)(w * 0.01));
        if (kernelWidth % 2 == 0) kernelWidth++;
        var pad = kernelWidth / 2;

        int At(int i) => mask[Math.Clamp(i, 0, mask.Length - 1)] ? 1 : 0;

        var result = new bool[w];
        for (var i = 0; i < w; i++)
        {
            var sum = 0;
            for (var k = -pad; k <= pad; k++) sum += At(i + k);
            result[i] = sum >= kernelWidth * 0.5;
        }
        return result;
    }

    /// <summary>Finds the book's x-span [lo, hi) from a cleaned page/not-page mask by walking
    /// OUTWARD FROM THE GUTTER rather than picking whichever contiguous run is widest. Port of
    /// the notebook's `find_book_span`. A "widest run wins" rule can land on a high-activity
    /// interior blob (e.g. a photo or dense text block) that has nothing to do with either outer
    /// edge — the notebook's own comment documents this as a confirmed failure mode on Method
    /// 4's own sign-change profile. Since the gutter physically has to sit strictly between the
    /// two true outer edges, anchoring the search there and reading off each side's run's OUTER
    /// end is guaranteed to reach the real edges as long as the mask is correct near the gutter.
    /// Returns null if no substantial run exists (mirrors the notebook's own None-return case);
    /// falls back to widest-run if the gutter isn't bracketed by a run on both sides (mask
    /// failure near the spine) or if gutterX is null.</summary>
    public static (int Lo, int Hi)? FindBookSpan(bool[] mask, int w, int? gutterX)
    {
        var runs = new List<(int A, int B)>();
        var inside = false;
        var start = 0;
        for (var x = 0; x < w; x++)
        {
            if (mask[x] && !inside) { start = x; inside = true; }
            else if (!mask[x] && inside) { runs.Add((start, x)); inside = false; }
        }
        if (inside) runs.Add((start, w));

        var minRunWidth = Math.Max(20, (int)(w * 0.05));
        runs = runs.Where(r => r.B - r.A >= minRunWidth).ToList();
        if (runs.Count == 0) return null;

        runs = runs.OrderBy(r => r.A).ToList();
        var mergeGap = Math.Max(10, (int)(w * 0.03));
        var merged = new List<(int A, int B)>();
        foreach (var r in runs)
        {
            if (merged.Count == 0) { merged.Add(r); continue; }
            var (prevA, prevB) = merged[^1];
            if (r.A - prevB <= mergeGap) merged[^1] = (prevA, Math.Max(prevB, r.B));
            else merged.Add(r);
        }

        if (gutterX == null) return merged.OrderByDescending(r => r.B - r.A).First();

        var gx = Math.Clamp(gutterX.Value, 0, w - 1);
        var leftRuns = merged.Where(r => r.A <= gx).ToList();
        var rightRuns = merged.Where(r => r.B > gx).ToList();
        if (leftRuns.Count == 0 || rightRuns.Count == 0)
            return merged.OrderByDescending(r => r.B - r.A).First();

        var leftRun = leftRuns.OrderByDescending(r => r.B).First();
        var rightRun = rightRuns.OrderBy(r => r.A).First();
        return (leftRun.A, rightRun.B);
    }

    /// <summary>Result of <see cref="AltTraceSideEdgeMethod4"/>: the final (bridged) trace,
    /// diagnostics for how many rows were bridged, and whether the widen-retry loop ever
    /// declared this side unreliable (never found a strong/straight fit within the searchable
    /// window) — surfaced so a caller/diagnostic can flag it, mirroring the notebook's own
    /// WARNING print.</summary>
    public readonly record struct Method4SideTrace(double[] Columns, int RowLo, int BridgedCount, bool Unreliable, double FinalMarginPx);

    /// <summary>Method 4's own vertical-trace finger-bridging — identical technique to
    /// <see cref="AltRejectAndBridgeLowConfidenceRuns"/> (Cell 4D) but applied to a per-row
    /// column trace instead of a per-column row trace. Kept as a separate small helper (rather
    /// than reusing the top/bottom version) because indexing is transposed and the notebook
    /// itself keeps `bridge_finger_runs` as its own local function for exactly this reason.</summary>
    private static (int[] Bridged, int BridgedCount, bool[] Good) BridgeFingerRunsVertical(int[] edgeX, Mat gx, int rowLo, double lowConfidenceFraction)
    {
        var w = gx.Cols;
        gx.GetArray(out float[] gxFlat);
        var n = edgeX.Length;

        var peakStrength = new double[n];
        for (var i = 0; i < n; i++) peakStrength[i] = Math.Abs(gxFlat[(rowLo + i) * w + edgeX[i]]);
        var medianStrength = Median(Array.ConvertAll(peakStrength, v => (float)v), 0, n);
        var confidenceThreshold = medianStrength * lowConfidenceFraction;
        var good = new bool[n];
        for (var i = 0; i < n; i++) good[i] = peakStrength[i] > confidenceThreshold;

        var bridged = (int[])edgeX.Clone();
        var bridgedCount = 0;
        var idx = 0;
        while (idx < n)
        {
            if (good[idx]) { idx++; continue; }
            var runStart = idx;
            while (idx < n && !good[idx]) idx++;
            var runEnd = idx;
            bridgedCount += runEnd - runStart;

            var loVal = runStart > 0 ? edgeX[runStart - 1] : edgeX[Math.Min(runEnd, n - 1)];
            var hiVal = runEnd < n ? edgeX[runEnd] : edgeX[Math.Max(runStart - 1, 0)];
            var runLen = runEnd - runStart;
            for (var k = 0; k < runLen; k++)
            {
                var t = (k + 1.0) / (runLen + 1.0);
                bridged[runStart + k] = (int)Math.Round(loVal + t * (hiVal - loVal));
            }
        }
        return (bridged, bridgedCount, good);
    }

    /// <summary>Std-dev of a trace's deviation from its own best-fit line — low = straight/clean
    /// trace, high = jittery/wandering. Port of the notebook's
    /// `edge_straightness_residual_std`.</summary>
    private static double EdgeStraightnessResidualStd(double[] edgeX, int rowLo)
    {
        var n = edgeX.Length;
        var pts = new (double Row, double Col)[n];
        for (var i = 0; i < n; i++) pts[i] = (rowLo + i, edgeX[i]);
        var fit = RansacLineFitExact(pts);
        var sumSq = 0.0;
        for (var i = 0; i < n; i++)
        {
            var pred = fit.Slope * (rowLo + i) + fit.Intercept;
            var residual = edgeX[i] - pred;
            sumSq += residual * residual;
        }
        return Math.Sqrt(sumSq / n);
    }

    /// <summary>Plain (non-robust) least-squares line fit — port of `np.polyfit(ys, xs, 1)`,
    /// used only by <see cref="EdgeStraightnessResidualStd"/> to compute a trace's OWN
    /// best-fit line (not a robust/outlier-rejecting fit — the notebook doesn't use RANSAC
    /// here either, since this measures the trace's own internal straightness, not fitting
    /// against noisy per-row candidates).</summary>
    private static (double Slope, double Intercept) RansacLineFitExact((double Row, double Col)[] points)
    {
        double sumY = 0, sumX = 0, sumYY = 0, sumYX = 0;
        var n = points.Length;
        foreach (var (y, x) in points) { sumY += y; sumX += x; sumYY += y * y; sumYX += y * x; }
        var denom = n * sumYY - sumY * sumY;
        if (Math.Abs(denom) < 1e-12) return (0, points.Length > 0 ? points[0].Col : 0);
        var slope = (n * sumYX - sumY * sumX) / denom;
        var intercept = (sumX - slope * sumY) / n;
        return (slope, intercept);
    }

    private static double GutterDistance(double[] edgeX, double gutterX)
    {
        var vals = new double[edgeX.Length];
        for (var i = 0; i < edgeX.Length; i++) vals[i] = Math.Abs(edgeX[i] - gutterX);
        Array.Sort(vals);
        var mid = vals.Length / 2;
        return vals.Length % 2 == 0 ? (vals[mid - 1] + vals[mid]) / 2.0 : vals[mid];
    }

    private static double EdgeStrengthScore(double[] edgeX, int rowLo, Mat gx)
    {
        var w = gx.Cols;
        gx.GetArray(out float[] gxFlat);
        var vals = new double[edgeX.Length];
        for (var i = 0; i < edgeX.Length; i++)
            vals[i] = Math.Abs(gxFlat[(rowLo + i) * w + Math.Clamp((int)Math.Round(edgeX[i]), 0, w - 1)]);
        Array.Sort(vals);
        var mid = vals.Length / 2;
        return vals.Length % 2 == 0 ? (vals[mid - 1] + vals[mid]) / 2.0 : vals[mid];
    }

    /// <summary>Symmetry ratio threshold — sides whose gutter distance differs by more than this
    /// factor are suspect. Ported as-is from the notebook (`SYMMETRY_RATIO_THRESHOLD = 1.3`).</summary>
    public double Method4SymmetryRatioThreshold { get; set; } = 1.3;
    /// <summary>Straightness residual std (px) above which a trace is considered jittery/
    /// wandering. Ported as-is from the notebook (`JITTER_THRESHOLD_PX = 3.0`).</summary>
    public double Method4JitterThresholdPx { get; set; } = 3.0;
    /// <summary>Per-retry search-margin growth factor. Ported as-is from the notebook
    /// (`RETRY_MARGIN_STEP_MULT = 2`).</summary>
    public double Method4RetryMarginStepMult { get; set; } = 2.0;
    /// <summary>Minimum fraction of the stronger side's edge strength a side must reach to be
    /// considered "strong." Ported as-is from the notebook (`STRENGTH_RATIO_THRESHOLD = 0.5`).</summary>
    public double Method4StrengthRatioThreshold { get; set; } = 0.5;

    /// <summary>One RANSAC-guided walk + finger-bridge for one side of one page, at a given
    /// search margin around <paramref name="bookX"/> — the unit of work the widen-retry loop
    /// repeats with a larger margin. Port of the notebook's `rerun_side_with_margin` (which
    /// itself duplicates the LEFT/RIGHT block's own first-pass logic — this helper is used for
    /// BOTH the first pass and every retry, unlike the notebook which inlines the first pass
    /// separately; the math is identical, so sharing one implementation here is a faithful
    /// simplification, not a behavior change).</summary>
    private (int[] Raw, double? GuideM, double? GuideB) RunSideWalk(Mat gray, Mat gx, int rowTop, int rowBot, int bookX, int margin, int w)
    {
        var ransacRowLo = rowTop + (int)((rowBot - rowTop) * 0.15);
        var ransacRowHi = rowTop + (int)((rowBot - rowTop) * 0.85);
        var colLo = Math.Max(0, bookX - margin);
        var colHi = Math.Min(w, bookX + margin);

        var diagramMask = DetectDiagramLineMask(gray, rowTop, rowBot, colLo, colHi);
        // diagramMask is indexed relative to rowTop (length rowBot-rowTop); the RANSAC sampling
        // range [ransacRowLo, ransacRowHi) is a sub-range of that — slice with the same offset
        // convention CollectRansacCandidates expects (excludeMaskRowOffset = rowTop).
        var pts = CollectRansacCandidates(gx, ransacRowLo, ransacRowHi, colLo, colHi, diagramMask, rowTop);
        var guide = RansacLineFit(pts, nIter: 1000, inlierThresh: 2.0);

        var seedX = bookX;
        if (guide.Slope is { } gm && guide.Intercept is { } gb)
        {
            var seedRow = (rowTop + rowBot) / 2;
            seedX = (int)Math.Round(gm * seedRow + gb);
            seedX = Math.Clamp(seedX, bookX - margin, bookX + margin - 1);
        }

        var raw = TraceSideEdgeGx(gx, rowTop, rowBot, seedX, colLo, colHi, guide.Slope, guide.Intercept);
        return (raw, guide.Slope, guide.Intercept);
    }

    /// <summary>The continuity-constrained |Gx| walk itself (notebook's `trace_side_edge`),
    /// shared by every Method 4 pass (first pass and every widen-retry). Distinct from
    /// <see cref="AltTraceSideEdge"/> (Cell 6A's own walk): this version takes an explicit RANSAC
    /// guide line and pulls the search window back toward it when the previous step's accepted
    /// column drifts more than <paramref name="guideTolerance"/> px away — Cell 6A's walk has no
    /// guide-line parameter at all. Returns one column per row across [rowTop, rowBot).</summary>
    private static int[] TraceSideEdgeGx(Mat gx, int rowTop, int rowBot, int seedX, int colLo, int colHi, double? guideM, double? guideB, int maxStep = 6, double guideTolerance = 15)
    {
        var w = gx.Cols;
        gx.GetArray(out float[] gxFlat);
        var n = rowBot - rowTop;
        var edgeX = new int[n];
        var seedRow = rowTop + n / 2;
        seedX = Math.Clamp(seedX, colLo, colHi - 1);
        edgeX[seedRow - rowTop] = seedX;

        int StepCenter(int prev, int row)
        {
            var center = (double)prev;
            if (guideM is { } gm && guideB is { } gb)
            {
                var guideX = gm * row + gb;
                if (Math.Abs(center - guideX) > guideTolerance)
                    center = guideX + Math.Clamp(center - guideX, -guideTolerance, guideTolerance);
            }
            return (int)Math.Round(center);
        }

        int ArgMaxWindow(int row, int center)
        {
            var lo = Math.Max(colLo, center - maxStep);
            var hi = Math.Min(colHi, center + maxStep + 1);
            if (hi <= lo) return center;
            var best = lo;
            var bestVal = float.MinValue;
            for (var c = lo; c < hi; c++)
            {
                var v = Math.Abs(gxFlat[row * w + c]);
                if (v > bestVal) { bestVal = v; best = c; }
            }
            return best;
        }

        var prev = seedX;
        for (var row = seedRow + 1; row < rowBot; row++)
        {
            prev = ArgMaxWindow(row, StepCenter(prev, row));
            edgeX[row - rowTop] = prev;
        }
        prev = seedX;
        for (var row = seedRow - 1; row >= rowTop; row--)
        {
            prev = ArgMaxWindow(row, StepCenter(prev, row));
            edgeX[row - rowTop] = prev;
        }
        return edgeX;
    }

    /// <summary>Method 4's full side-edge detector for ONE side (left or right) of one page:
    /// RANSAC-guided walk + finger bridging, then the symmetry+straightness+strength widen-retry
    /// loop against the OTHER side. Since both sides need each other's strength/distance to
    /// evaluate symmetry, this is called as a pair from <see cref="AltTraceSideEdgeMethod4Pair"/>
    /// rather than independently — mirrors the notebook's single shared cell computing both
    /// sides together rather than two independent function calls.</summary>
    private (Method4SideTrace Left, Method4SideTrace Right) TraceMethod4Pair(
        Mat img, Mat gray, Mat gx,
        int rowTopLeft, int rowBotLeft, int bookXLo,
        int rowTopRight, int rowBotRight, int bookXHi,
        int margin, double gutterCol, int w)
    {
        var (leftRaw, leftGm, leftGb) = RunSideWalk(gray, gx, rowTopLeft, rowBotLeft, bookXLo, margin, w);
        var (rightRaw, rightGm, rightGb) = RunSideWalk(gray, gx, rowTopRight, rowBotRight, bookXHi, margin, w);

        var (leftBridged, leftBridgedCount, _) = BridgeFingerRunsVertical(leftRaw, gx, rowTopLeft, AltLowConfidencePeakFraction);
        var (rightBridged, rightBridgedCount, _) = BridgeFingerRunsVertical(rightRaw, gx, rowTopRight, AltLowConfidencePeakFraction);

        var leftX = Array.ConvertAll(leftBridged, v => (double)v);
        var rightX = Array.ConvertAll(rightBridged, v => (double)v);

        var leftDist = GutterDistance(leftX, gutterCol);
        var rightDist = GutterDistance(rightX, gutterCol);
        var leftJitter = EdgeStraightnessResidualStd(leftX, rowTopLeft);
        var rightJitter = EdgeStraightnessResidualStd(rightX, rowTopRight);
        var leftStrength = EdgeStrengthScore(leftX, rowTopLeft, gx);
        var rightStrength = EdgeStrengthScore(rightX, rowTopRight, gx);

        bool SideIsGood(double jitter, double strength, double referenceStrength) =>
            jitter <= Method4JitterThresholdPx && strength >= referenceStrength * Method4StrengthRatioThreshold;

        var leftMargin = (double)margin;
        var rightMargin = (double)margin;
        var leftMaxed = false;
        var rightMaxed = false;

        while (true)
        {
            var referenceStrength = Math.Max(leftStrength, rightStrength);
            var leftOk = SideIsGood(leftJitter, leftStrength, referenceStrength);
            var rightOk = SideIsGood(rightJitter, rightStrength, referenceStrength);
            var symmetric = Math.Max(leftDist, rightDist) / Math.Max(Math.Min(leftDist, rightDist), 1e-6) <= Method4SymmetryRatioThreshold;

            var leftNeedsRetry = !leftOk || (!symmetric && leftDist <= rightDist);
            var rightNeedsRetry = !rightOk || (!symmetric && rightDist < leftDist);

            if ((!leftNeedsRetry && !rightNeedsRetry) || (leftMaxed && rightMaxed)) break;

            var retriedSomething = false;

            if (leftNeedsRetry && !leftMaxed)
            {
                retriedSomething = true;
                leftMargin = Math.Min(bookXLo, leftMargin * Method4RetryMarginStepMult);
                leftMaxed = (bookXLo - leftMargin) <= 0;
                var (raw, gm, gb) = RunSideWalk(gray, gx, rowTopLeft, rowBotLeft, bookXLo, (int)leftMargin, w);
                leftGm = gm; leftGb = gb;
                var (bridged, bridgedCount, _) = BridgeFingerRunsVertical(raw, gx, rowTopLeft, AltLowConfidencePeakFraction);
                leftBridgedCount = bridgedCount;
                leftX = Array.ConvertAll(bridged, v => (double)v);
                leftDist = GutterDistance(leftX, gutterCol);
                leftJitter = EdgeStraightnessResidualStd(leftX, rowTopLeft);
                leftStrength = EdgeStrengthScore(leftX, rowTopLeft, gx);
            }

            if (rightNeedsRetry && !rightMaxed)
            {
                retriedSomething = true;
                rightMargin = Math.Min(w - bookXHi, rightMargin * Method4RetryMarginStepMult);
                rightMaxed = (bookXHi + rightMargin) >= w;
                var (raw, gm, gb) = RunSideWalk(gray, gx, rowTopRight, rowBotRight, bookXHi, (int)rightMargin, w);
                rightGm = gm; rightGb = gb;
                var (bridged, bridgedCount, _) = BridgeFingerRunsVertical(raw, gx, rowTopRight, AltLowConfidencePeakFraction);
                rightBridgedCount = bridgedCount;
                rightX = Array.ConvertAll(bridged, v => (double)v);
                rightDist = GutterDistance(rightX, gutterCol);
                rightJitter = EdgeStraightnessResidualStd(rightX, rowTopRight);
                rightStrength = EdgeStrengthScore(rightX, rowTopRight, gx);
            }

            if (!retriedSomething) break;
        }

        var finalReference = Math.Max(leftStrength, rightStrength);
        var leftUnreliable = !SideIsGood(leftJitter, leftStrength, finalReference);
        var rightUnreliable = !SideIsGood(rightJitter, rightStrength, finalReference);

        return (
            new Method4SideTrace(leftX, rowTopLeft, leftBridgedCount, leftUnreliable, leftMargin),
            new Method4SideTrace(rightX, rowTopRight, rightBridgedCount, rightUnreliable, rightMargin)
        );
    }

    /// <summary>Method 4's book-span detector: the sign-change-count profile, Otsu-split into a
    /// page mask, gutter-anchored span. Port of the notebook's Method 4 span-detection cell.
    /// <paramref name="spanGutterX"/> is the best available REAL gutter-x estimate to anchor
    /// <see cref="FindBookSpan"/>'s outward-from-the-gutter walk — pass null when there is no
    /// real gutter (a genuine single page, not a spread) to fall back to
    /// <see cref="FindBookSpan"/>'s own widest-merged-run behavior instead. Anchoring a fake
    /// "gutter" (e.g. the image's own horizontal center) on a true single page is unsound: unlike
    /// a real spine, which the sign-change mask reliably breaks around because the two pages'
    /// text blocks are physically separated by a gap/shadow, a single page's own mask can have a
    /// merged run that happens to straddle the image center without the run's ends being
    /// anywhere near the true left/right page edges — confirmed as a real bug on a real fixture
    /// (Trapezoid_Image003: a spread with the left page mostly out of frame, misclassified as a
    /// single page by the upstream spine-shadow gutter check — DetectGutter's own doc comment
    /// notes it scores this exact photo's family as a non-confident single page — where anchoring
    /// at the image center collapsed the detected span to a ~378px band deep inside the visible
    /// page's own text block, producing a near-zero-width flattened output). The notebook itself
    /// only ever runs on already-split single-page images with `gutter_x_global` as a crude
    /// `Wf // 2` fallback and never hit this failure mode in its own test photos; this null-gutter
    /// path is this port's own hardening for a case the notebook's fixtures didn't exercise.</summary>
    public static (int Lo, int Hi)? DetectMethod4Span(Mat gx, double[] topY, double[] botY, int? spanGutterX, int w)
    {
        var profile = ComputeSignChangeProfile(gx, topY, botY);
        double threshold;
        try { threshold = OtsuSplit(profile.SignChanges, profile.Valid); }
        catch (InvalidOperationException) { return null; }

        var mask = new bool[w];
        for (var x = 0; x < w; x++) mask[x] = profile.Valid[x] && profile.SignChanges[x] >= threshold;
        mask = CleanMask1D(mask, w);

        return FindBookSpan(mask, w, spanGutterX);
    }

    /// <summary>Full Method 4 side-boundary detection for a spread OR a genuine single page:
    /// span detection + RANSAC-guided walk + bridging + widen-retry for both sides, returning
    /// the same <see cref="AltSideEdgeTrace"/> shape <see cref="AltTraceSideEdge"/> (Cell 6A)
    /// produces so it drops directly into <see cref="AltDetectSpreadBoundary"/>/
    /// <see cref="AltFlattenSinglePage"/> in its place.
    /// <paramref name="gutterSeedColumn"/> is always used for the symmetry check's own
    /// gutter-distance reference (a left/right-vs-center comparison is a reasonable symmetry
    /// gate for either a real spread or a single page — the notebook uses the same
    /// `gutter_x_global - left_cut` for both). <paramref name="hasRealGutter"/> controls ONLY
    /// whether <see cref="DetectMethod4Span"/>'s span search itself is anchored there (true — a
    /// real spread, where a text-block gap/shadow really does separate the two pages at that
    /// column) or falls back to widest-run (false — a genuine single page, where anchoring a fake
    /// center "gutter" risks locking onto an interior text blob instead of the true page
    /// extent — see <see cref="DetectMethod4Span"/>'s own doc comment for the confirmed failure
    /// this avoids).</summary>
    public readonly record struct Method4Result(AltSideEdgeTrace Left, AltSideEdgeTrace Right, int BookXLo, int BookXHi, bool SpanFound);

    public Method4Result AltTraceSideEdgeMethod4Pair(Mat img, Mat gray, Mat gx, double[] topY, double[] botY, int gutterSeedColumn, bool hasRealGutter = true)
    {
        var w = img.Cols;
        var span = DetectMethod4Span(gx, topY, botY, hasRealGutter ? gutterSeedColumn : null, w);
        if (span is not { } s)
        {
            // No span detected — mirror the notebook's own "skip edge trace" fallback by
            // returning empty/degenerate traces; callers already handle a zero-width side
            // trace the same way AltTraceSideEdge's own defensive clamps do.
            var empty = new AltSideEdgeTrace(Array.Empty<double>(), 0, 0, 0);
            return new Method4Result(empty, empty, 0, w, false);
        }

        var (bookXLo, bookXHi) = s;

        int RowMedian(double[] arr, int lo, int hi)
        {
            lo = Math.Max(0, lo); hi = Math.Min(arr.Length, hi);
            if (hi <= lo) return 0;
            var slice = arr.Skip(lo).Take(hi - lo).Select(v => (float)v).ToArray();
            return (int)Median(slice, 0, slice.Length);
        }

        var rowTopLeft = Math.Clamp(RowMedian(topY, bookXLo, bookXLo + 10), 0, img.Rows - 1);
        var rowBotLeft = Math.Clamp(RowMedian(botY, bookXLo, bookXLo + 10), rowTopLeft + 1, img.Rows);
        var rowTopRight = Math.Clamp(RowMedian(topY, bookXHi - 10, bookXHi), 0, img.Rows - 1);
        var rowBotRight = Math.Clamp(RowMedian(botY, bookXHi - 10, bookXHi), rowTopRight + 1, img.Rows);

        var margin = Math.Max(20, (int)(w * 0.04));

        var (left, right) = TraceMethod4Pair(img, gray, gx, rowTopLeft, rowBotLeft, bookXLo, rowTopRight, rowBotRight, bookXHi, margin, gutterSeedColumn, w);

        var leftTrace = new AltSideEdgeTrace(left.Columns, left.RowLo, left.BridgedCount, 0);
        var rightTrace = new AltSideEdgeTrace(right.Columns, right.RowLo, right.BridgedCount, 0);

        return new Method4Result(leftTrace, rightTrace, bookXLo, bookXHi, true);
    }

    // --- Phase 2 orchestration + diagnostic entry point ---

    /// <summary>Full result of tracing a whole spread's boundary (Phases 1-3 combined): raw
    /// top/bottom traces, their finger-bridged versions, their Sav-Gol-smoothed final versions,
    /// the gutter notch/line, and the left/right side-edge traces. Everything here is in the
    /// source image's own pixel coordinates.
    ///
    /// Left/right are NOT smoothed (see <see cref="AltDetectSpreadBoundary"/>'s own remarks) —
    /// <see cref="Left"/>/<see cref="Right"/> are the finger/glare-bridged trace directly, same
    /// as what shipped in Phase 2, so there is no separate raw/smoothed pair for them the way
    /// there is for top/bottom.</summary>
    public readonly record struct AltSpreadBoundary(
        AltEdgePoint[] TopRaw, double[] TopBridged, double[] TopFinal,
        AltEdgePoint[] BottomRaw, double[] BottomBridged, double[] BottomFinal,
        AltGutterDetection Gutter,
        AltSideEdgeTrace Left, AltSideEdgeTrace Right,
        int TopBridgedCount, int BottomBridgedCount);

    /// <summary>Runs the Phase 1-3 trace pipeline (top/bottom edge trace + finger bridging +
    /// Sav-Gol smoothing, gutter notch, left/right side-edge trace) against a full spread image.
    /// Pure detection — does not crop, split, or flatten anything (that's Phase 4). Shared by
    /// <see cref="DebugAltBoundaryDetection"/> (text + overlay diagnostic) and, once wired in
    /// (Phase 5), the real pipeline entry point.
    ///
    /// Left/right come from Method 4 (Gx sign-change count + gutter-anchored span + RANSAC-
    /// guided continuity walk + finger bridging + strength/straightness widen-retry — see
    /// <see cref="AltTraceSideEdgeMethod4Pair"/>), NOT smoothed here either: Method 4's own
    /// bridging already removes finger-occlusion jitter the same way Cell 6A's did, and a
    /// side's genuine tilt/bow (real physical curl, or the deliberate tilt in
    /// <c>Trapezoid_Image001</c>/`002`) must survive downstream exactly as the notebook's own
    /// Method 4 trace does. Top/bottom get Sav-Gol because the notebook applies it there
    /// unconditionally and because that curve is expected to be smooth by physical construction
    /// (a page edge doesn't have sharp corners along its own length, unlike text-line noise).</summary>
    public AltSpreadBoundary AltDetectSpreadBoundary(Mat img)
    {
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        using var gy = new Mat();
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, ksize: 3);
        using var gx = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, ksize: 3);

        var topRaw = AltTraceTopBottomEdge(img, gy, fromTop: true);
        var bottomRaw = AltTraceTopBottomEdge(img, gy, fromTop: false);

        var topBridged = AltRejectAndBridgeLowConfidenceRuns(topRaw, gy);
        var bottomBridged = AltRejectAndBridgeLowConfidenceRuns(bottomRaw, gy);

        var topBridgedCount = CountBridged(topRaw, topBridged);
        var bottomBridgedCount = CountBridged(bottomRaw, bottomBridged);

        var topSmooth = AltSavGolFilter(topBridged, AltSavGolWindow, AltSavGolPolyDegree);
        var bottomSmooth = AltSavGolFilter(bottomBridged, AltSavGolWindow, AltSavGolPolyDegree);

        // Gutter seed: prefer the already-validated spine-shadow detector (ImageProcessor.
        // DetectGutter — real per-batch signal, confirmed against real fixtures per its own doc
        // comment) over the notebook's own crude `Wf // 2` fallback, which is only ever a last
        // resort when no better measurement exists (the notebook's own comment on
        // gutter_x_global says exactly this: "crude center seed -- no Cb-valley gutter estimate
        // to anchor to"). Falls back to center when DetectGutter isn't confident, matching the
        // notebook's own fallback behavior.
        var gutterDetection = DetectGutter(img, GutterMinFlankMarginFraction);
        var gutterSeedColumn = gutterDetection.Confidence >= GutterConfidenceThreshold
            ? Math.Clamp((int)Math.Round(img.Cols * gutterDetection.Fraction), 0, img.Cols - 1)
            : img.Cols / 2;
        var gutter = AltDetectGutterNotch(topSmooth, bottomSmooth, gutterSeedColumn);

        var gutterMidCol = Math.Clamp((int)Math.Round((gutter.TopNotch.Column + gutter.BottomNotch.Column) / 2.0), 0, img.Cols - 1);

        var method4 = AltTraceSideEdgeMethod4Pair(img, gray, gx, topSmooth, bottomSmooth, gutterMidCol);
        var left = method4.Left;
        var right = method4.Right;

        return new AltSpreadBoundary(topRaw, topBridged, topSmooth, bottomRaw, bottomBridged, bottomSmooth, gutter, left, right, topBridgedCount, bottomBridgedCount);
    }

    private static int CountBridged(AltEdgePoint[] raw, double[] final)
    {
        var count = 0;
        for (var i = 0; i < raw.Length; i++)
            if (Math.Abs(raw[i].Row - final[i]) > 0.01) count++;
        return count;
    }

    /// <summary>Single-page counterpart of <see cref="AltSpreadBoundary"/> — same top/bottom/
    /// left/right trace fields, minus the gutter (a single page has no spine to notch). Everything
    /// here is in the source image's own pixel coordinates.</summary>
    public readonly record struct AltSinglePageBoundary(
        AltEdgePoint[] TopRaw, double[] TopBridged, double[] TopFinal,
        AltEdgePoint[] BottomRaw, double[] BottomBridged, double[] BottomFinal,
        AltSideEdgeTrace Left, AltSideEdgeTrace Right,
        int TopBridgedCount, int BottomBridgedCount);

    /// <summary>Pure detection for a single (non-spread) page — same Phase 1-3 trace pipeline as
    /// <see cref="AltDetectSpreadBoundary"/> minus the gutter notch step, matching
    /// <see cref="AltFlattenSinglePage"/>'s own inline detection exactly (including its
    /// hasRealGutter:false / image-center anchor for Method 4's span search — see that method's
    /// own remarks). Does not crop or flatten anything.</summary>
    public AltSinglePageBoundary AltDetectSinglePageBoundary(Mat img)
    {
        using var gray = new Mat();
        Cv2.CvtColor(img, gray, ColorConversionCodes.BGR2GRAY);
        using var gy = new Mat();
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, ksize: 3);
        using var gx = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, ksize: 3);

        var topRaw = AltTraceTopBottomEdge(img, gy, fromTop: true);
        var bottomRaw = AltTraceTopBottomEdge(img, gy, fromTop: false);

        var topBridged = AltRejectAndBridgeLowConfidenceRuns(topRaw, gy);
        var bottomBridged = AltRejectAndBridgeLowConfidenceRuns(bottomRaw, gy);

        var topBridgedCount = CountBridged(topRaw, topBridged);
        var bottomBridgedCount = CountBridged(bottomRaw, bottomBridged);

        var topSmooth = AltSavGolFilter(topBridged, AltSavGolWindow, AltSavGolPolyDegree);
        var bottomSmooth = AltSavGolFilter(bottomBridged, AltSavGolWindow, AltSavGolPolyDegree);

        var method4 = AltTraceSideEdgeMethod4Pair(img, gray, gx, topSmooth, bottomSmooth, img.Cols / 2, hasRealGutter: false);

        return new AltSinglePageBoundary(topRaw, topBridged, topSmooth, bottomRaw, bottomBridged, bottomSmooth, method4.Left, method4.Right, topBridgedCount, bottomBridgedCount);
    }

    // --- Phase 3: Sav-Gol smoothing ---

    /// <summary>Smoothing window (samples) for <see cref="AltSavGolFilter"/>, applied to the
    /// top/bottom curves after finger-bridging. Ported as-is from the notebook (`window=151`).
    /// Odd-window-length guarded by the filter itself.</summary>
    public int AltSavGolWindow { get; set; } = 151;

    /// <summary>Polynomial degree for <see cref="AltSavGolFilter"/>. Ported as-is from the
    /// notebook (`poly=2`).</summary>
    public int AltSavGolPolyDegree { get; set; } = 2;

    /// <summary>Hand-rolled Savitzky-Golay filter (no general-purpose math/signal-processing
    /// NuGet exists in this project — see MicroCapture.Processing.csproj — consistent with this
    /// codebase's existing preference for small, well-specified hand-rolled numerical code, e.g.
    /// <see cref="WeightedPolyFit"/>/<see cref="SolveLinearSystem"/>). Matches
    /// `scipy.signal.savgol_filter`'s behavior for a fixed polynomial degree: at each sample,
    /// fits a degree-<see cref="AltSavGolPolyDegree"/> polynomial by least squares to the
    /// window of samples centered on it (clamped/shrunk near the array's own edges, since a
    /// zero-padded edge would pull the fit down at the boundary) and evaluates that polynomial
    /// at the center. Ported as-is from the notebook's Cell 6D (`window=151, poly=2`).</summary>
    public double[] AltSavGolFilter(double[] values, int window, int polyDegree)
    {
        var n = values.Length;
        if (n == 0) return values;
        var w = Math.Min(window, n - (1 - n % 2));
        if (w < polyDegree + 2) return (double[])values.Clone();
        if (w % 2 == 0) w--;

        var half = w / 2;
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(n, i + half + 1);
            var points = new List<(double X, double Y, double W)>(hi - lo);
            for (var j = lo; j < hi; j++) points.Add((j - i, values[j], 1.0));

            var localDegree = Math.Min(polyDegree, hi - lo - 1);
            var coeffs = WeightedPolyFit(points, localDegree);
            result[i] = coeffs == null ? values[i] : EvalPoly(coeffs, 0.0);
        }
        return result;
    }

    /// <summary>Diagnostic-only entry point for <c>DewarpDiagnostic altboundary</c>: runs
    /// <see cref="AltDetectSpreadBoundary"/> and returns a text report. Never mutates or writes
    /// files itself — pairs with <see cref="AltBoundaryOverlay"/> for the visual half (a trace
    /// can only be meaningfully validated by eye, per this project's own established practice —
    /// see tools/SmokeTest/Fixtures/real-photos/README.md).</summary>
    public string DebugAltBoundaryDetection(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (mat.Empty()) return "decode failed";

        var boundary = AltDetectSpreadBoundary(mat);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");
        sb.AppendLine($"Top edge: {boundary.TopBridgedCount}/{mat.Cols} columns bridged (low-confidence)");
        sb.AppendLine($"Bottom edge: {boundary.BottomBridgedCount}/{mat.Cols} columns bridged (low-confidence)");
        sb.AppendLine($"Gutter notch: top=({boundary.Gutter.TopNotch.Column},{boundary.Gutter.TopNotch.Row:F1}) bottom=({boundary.Gutter.BottomNotch.Column},{boundary.Gutter.BottomNotch.Row:F1})");
        sb.AppendLine($"Left edge: {boundary.Left.LowConfidenceRows} rows bridged ({boundary.Left.GlareChromaRecovered} recovered via glare-gated Cb tiebreaker), x range {boundary.Left.Columns.Min():F0}-{boundary.Left.Columns.Max():F0}");
        sb.AppendLine($"Right edge: {boundary.Right.LowConfidenceRows} rows bridged ({boundary.Right.GlareChromaRecovered} recovered via glare-gated Cb tiebreaker), x range {boundary.Right.Columns.Min():F0}-{boundary.Right.Columns.Max():F0}");
        return sb.ToString();
    }

    /// <summary>Draws <see cref="AltDetectSpreadBoundary"/>'s traces on the source image: final
    /// top=red, final bottom=green (both bright/full-saturation — the Sav-Gol-smoothed curve
    /// actually used), pre-smoothing bridged top/bottom=dim red/dim orange-green (for visually
    /// comparing smoothed vs. raw, per this phase's own validation gate), left/right=cyan, gutter
    /// notch points=magenta circles, gutter line=yellow. Returns it PNG-encoded, for
    /// <c>DewarpDiagnostic altboundary</c> to write to disk — text metrics alone can't validate a
    /// visual trace.</summary>
    public byte[] AltBoundaryOverlay(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        var boundary = AltDetectSpreadBoundary(mat);

        for (var x = 0; x < boundary.TopBridged.Length; x++)
        {
            DrawDot(mat, x, boundary.TopBridged[x], new Scalar(0, 0, 120));
            DrawDot(mat, x, boundary.BottomBridged[x], new Scalar(0, 120, 120));
        }
        for (var x = 0; x < boundary.TopFinal.Length; x++)
        {
            DrawDot(mat, x, boundary.TopFinal[x], new Scalar(0, 0, 255));
            DrawDot(mat, x, boundary.BottomFinal[x], new Scalar(0, 255, 0));
        }
        for (var i = 0; i < boundary.Left.Columns.Length; i++)
            DrawDot(mat, boundary.Left.Columns[i], boundary.Left.RowLo + i, new Scalar(255, 200, 0));
        for (var i = 0; i < boundary.Right.Columns.Length; i++)
            DrawDot(mat, boundary.Right.Columns[i], boundary.Right.RowLo + i, new Scalar(255, 200, 0));
        foreach (var p in boundary.Gutter.Line)
            DrawDot(mat, p.Column, p.Row, new Scalar(0, 255, 255));
        Cv2.Circle(mat, boundary.Gutter.TopNotch.Column, (int)Math.Round(boundary.Gutter.TopNotch.Row), 8, new Scalar(255, 0, 255), -1);
        Cv2.Circle(mat, boundary.Gutter.BottomNotch.Column, (int)Math.Round(boundary.Gutter.BottomNotch.Row), 8, new Scalar(255, 0, 255), -1);

        Cv2.ImEncode(".png", mat, out var bytes);
        return bytes;
    }

    private static void DrawDot(Mat mat, double x, double y, Scalar color)
    {
        var xi = Math.Clamp((int)Math.Round(x), 0, mat.Cols - 1);
        var yi = Math.Clamp((int)Math.Round(y), 0, mat.Rows - 1);
        var y0 = Math.Max(0, yi - 1);
        var y1 = Math.Min(mat.Rows - 1, yi + 1);
        var x0 = Math.Max(0, xi - 1);
        var x1 = Math.Min(mat.Cols - 1, xi + 1);
        Cv2.Rectangle(mat, new Point(x0, y0), new Point(x1, y1), color, -1);
    }

    // --- Phase 4: polygon split + arc-length flatten ---

    /// <summary>Sum of Euclidean segment lengths along a sampled open curve/polyline. Port of
    /// the notebook's `curve_length`. Unlike <c>Cv2.ArcLength</c> (closed-contour-only, used
    /// elsewhere in this file for a different purpose — see <see cref="BuildDetection"/>), this
    /// works on an open, arbitrarily-sampled point sequence. Shared by both the WIDTH and HEIGHT
    /// computation in <see cref="AltFlattenPage"/> — not duplicated inline, since both need
    /// exactly this operation.</summary>
    public static double AltArcLength(double[] xs, double[] ys)
    {
        var total = 0.0;
        for (var i = 1; i < xs.Length; i++)
        {
            var dx = xs[i] - xs[i - 1];
            var dy = ys[i] - ys[i - 1];
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    /// <summary>Per-column arc-length "physical x" axis for a page's top/bottom curves, per the
    /// notebook's Cell 9A/9B (Method 1): at each step, the arc-length increment is the AVERAGE of
    /// the top and bottom curves' own hypot(1, dy) step (each individually guaranteed >= 1, the
    /// straight-line step) — so the cumulative physical-x axis is guaranteed >= the raw pixel
    /// column count by construction. This is the invariant the notebook's two rejected
    /// width-correction approaches (closed-form circular-arc radius fit — numerically unstable;
    /// direct H0/h(x) integration — could produce an output NARROWER than the raw crop, which
    /// is physically impossible for a page that only bends) both violated. Returns a
    /// same-length array, index 0 = 0 (the starting edge).
    ///
    /// NOT currently called by <see cref="AltFlattenPage"/> — the notebook itself never picks a
    /// winner between this (Method 1) and the cubic-bow fit (Method 2, <see cref="FitWidthCubic"/>)
    /// and instead ends on a side-by-side comparison cell (Cell 9C). The product owner's own
    /// framing ("Method 4 + its cubic-bow remap" as the mandatory reference) is what selects
    /// Method 2 as the shipped default; this Method 1 primitive is kept, unused, as a documented,
    /// faithful port of the notebook's other validated option, not dead/orphaned code.</summary>
    private static double[] AltArcLengthPhysicalX(double[] topY, double[] botY)
    {
        var n = topY.Length;
        var physX = new double[n];
        for (var i = 1; i < n; i++)
        {
            var dyTop = topY[i] - topY[i - 1];
            var dyBot = botY[i] - botY[i - 1];
            var ds = (Math.Sqrt(1.0 + dyTop * dyTop) + Math.Sqrt(1.0 + dyBot * dyBot)) / 2.0;
            physX[i] = physX[i - 1] + ds;
        }
        return physX;
    }

    /// <summary>Samples f(t) for t in [0,1] (n points) given edge slopes alpha/beta, for the
    /// cubic "vertical sheet" cross-section f(0)=0, f(1)=0, f'(0)=alpha, f'(1)=beta — page_dewarp.py's
    /// model, adapted here to boundary curves (see <see cref="FitWidthCubic"/>). Port of the
    /// notebook's `cubic_bow`, which solves this system symbolically via sympy at import time;
    /// this closed-form solve is the same system worked out algebraically (d=0 from f(0)=0,
    /// c=alpha from f'(0)=alpha, then f(1)=0 and f'(1)=beta give a=alpha+beta, b=-2*alpha-beta —
    /// verified by substitution: a+b+c = (alpha+beta)+(-2alpha-beta)+alpha = 0, and
    /// 3a+2b+c = 3(alpha+beta)+2(-2alpha-beta)+alpha = beta).</summary>
    public static double[] CubicBow(double alpha, double beta, int n)
    {
        var a = alpha + beta;
        var b = -2 * alpha - beta;
        var c = alpha;
        var result = new double[n];
        for (var i = 0; i < n; i++)
        {
            var t = n <= 1 ? 0.0 : (double)i / (n - 1);
            result[i] = a * t * t * t + b * t * t + c * t;
        }
        return result;
    }

    /// <summary>Fits a single-parameter cubic bow (beta = -alpha, i.e. symmetric spine-to-outer-
    /// edge droop) to the observed column-height profile h(x) = bot_y - top_y, then uses the
    /// fitted curve's own arc length as the physical width estimate. Port of the notebook's
    /// `fit_width_cubic` — this is the "mandatory reference" width-correction method (per the
    /// product owner: "Method 4 + its cubic-bow remap"), distinct from the plain arc-length
    /// measurement of the raw traced curve (<see cref="AltArcLengthPhysicalX"/>, kept in this
    /// file only as the underlying arc-length primitive <see cref="AltArcLength"/> the cubic fit
    /// itself still needs — not used for the page-width estimate anymore). The cubic model asks
    /// "what flat page, viewed under perspective by a camera at this pose, would produce this
    /// exact curve" rather than treating the raw traced curve's own pixel noise as ground truth
    /// — same guaranteed->=raw-span property as the arc-length approach (each ds step is
    /// hypot(1, dy) >= 1 by construction), but the per-column stretch comes from a physically-
    /// motivated bow model fit to the observed shrinkage instead of the noisy trace directly.
    /// scipy's `minimize_scalar(bounds=(-3,3), method='bounded')` is a golden-section search;
    /// ported here as a plain grid+refine search over the same bounds since this project has no
    /// general-purpose optimizer dependency (consistent with this file's existing hand-rolled
    /// numerical code, e.g. <see cref="RansacLineFit"/>/<see cref="WeightedPolyFit"/>) — a
    /// smooth, unimodal 1D objective over a bounded range converges to the same optimum via
    /// either method.</summary>
    public static (double[] PhysX, double Alpha) FitWidthCubic(double[] topY, double[] botY, double widthGain = 1.0)
    {
        var n = topY.Length;
        var h = new double[n];
        for (var i = 0; i < n; i++) h[i] = botY[i] - topY[i];
        var hMin = h.Min();
        var hMax = h.Max();
        var hNorm = new double[n];
        for (var i = 0; i < n; i++) hNorm[i] = hMax > hMin ? (h[i] - hMin) / (hMax - hMin) : (h[i] - hMin);

        double Objective(double alpha)
        {
            var bow = CubicBow(alpha, -alpha, n);
            var bowMin = bow.Min();
            var bowMax = bow.Max();
            var sumSq = 0.0;
            for (var i = 0; i < n; i++)
            {
                var bowNorm = bowMax > bowMin ? (bow[i] - bowMin) / (bowMax - bowMin) : (bow[i] - bowMin);
                var diff = bowNorm - hNorm[i];
                sumSq += diff * diff;
            }
            return sumSq;
        }

        // Golden-section search over [-3, 3], mirroring scipy's bounded minimize_scalar — a
        // derivative-free bracket search appropriate for this smooth, unimodal, single-parameter
        // objective (matches the notebook's own choice of `method='bounded'`).
        var lo = -3.0; var hi = 3.0;
        const double gr = 0.6180339887498949; // 1/golden ratio
        var c1 = hi - gr * (hi - lo);
        var c2 = lo + gr * (hi - lo);
        var f1 = Objective(c1);
        var f2 = Objective(c2);
        for (var iter = 0; iter < 100 && hi - lo > 1e-6; iter++)
        {
            if (f1 < f2)
            {
                hi = c2; c2 = c1; f2 = f1;
                c1 = hi - gr * (hi - lo);
                f1 = Objective(c1);
            }
            else
            {
                lo = c1; c1 = c2; f1 = f2;
                c2 = lo + gr * (hi - lo);
                f2 = Objective(c2);
            }
        }
        var alphaFit = (lo + hi) / 2.0;

        var bowFit = CubicBow(alphaFit, -alphaFit, n);
        var bowRange = hMax > hMin ? hMax - hMin : 1.0;
        var physX = new double[n];
        var cum = 0.0;
        physX[0] = 0.0;
        for (var i = 1; i < n; i++)
        {
            var dy = (bowFit[i] - bowFit[i - 1]) * bowRange;
            var ds = Math.Sqrt(1.0 + dy * dy) * widthGain;
            cum += ds;
            physX[i] = cum;
        }
        return (physX, alphaFit);
    }

    /// <summary>Result of <see cref="AltFlattenPage"/>: the flattened page image plus the
    /// output dimensions actually used (cubic-bow-derived, not the raw polygon crop's pixel
    /// span) — exposed so callers/diagnostics can verify the width-correction invariant
    /// (flattened width must never be smaller than the raw apparent crop width).
    /// <paramref name="FoundRealEdges"/> is false only when the input had too little
    /// gradient/texture evidence anywhere for Method 4 to find real page edges at all (a
    /// featureless/blank capture) — Flattened is still a real, non-degenerate image in that
    /// case (the defensive fallbacks in this method guarantee that), just an unrefined
    /// pass-through of the input rather than a genuine geometric correction. Callers that have
    /// their own trusted fallback boundary (e.g. ProcessFixedFrames' own calibrated rectangle)
    /// should prefer that over trusting this pass-through result's exact dimensions.</summary>
    public readonly record struct AltFlattenResult(Mat Flattened, int OutWidth, int OutHeight, double FittedAlpha, bool FoundRealEdges = true);

    /// <summary>Flattens one page (left or right half of a spread, or a whole single page — see
    /// <see cref="AltFlattenSinglePage"/>) via the notebook's arc-length remap (Cell 9A/9B,
    /// `remap_curved_page`). Unlike <see cref="RectifyWithBoundaryCurves"/> (this file's closest
    /// existing analog — a Coons patch blending 4 independently-fit edges), this uses only the
    /// TWO long edges (top/bottom, or for a single page with no gutter, still top/bottom) plus
    /// the two SHORT edges (outer + inner/gutter, each a row-indexed x-position, not necessarily
    /// a constant column) to drive the remap.
    ///
    /// WIDTH = cubic-bow-fit physical x-axis (<see cref="FitWidthCubic"/>, the notebook's Method
    /// 2 / Cell 9B-alt — the product owner's designated mandatory reference over the plain
    /// arc-length sum) — guaranteed >= the raw pixel span. HEIGHT = the outer edge's own traced
    /// arc length (the least-foreshortened available measurement of true page height).
    ///
    /// TILT FIX (an explicit, previously-shipped-then-fixed bug in the notebook itself — the
    /// single easiest regression to reintroduce when porting this, called out prominently in the
    /// port plan): naively sampling every output ROW of a given output COLUMN from the same
    /// source column is only correct if the outer page edge is a perfectly vertical line. Since
    /// the outer edge here is a genuinely TILTED line (<see cref="AltTraceSideEdge"/>'s own
    /// per-row trace, not a constant x), the source column at the outer boundary depends on ROW,
    /// not just which output column is being sampled. Fixed the same way the notebook fixes it:
    /// <paramref name="outerXAtRow"/>/<paramref name="innerXAtRow"/> are row-indexed (one x per
    /// output row, looked up at that PIXEL's own mapped source row after curve-straightening —
    /// not once per column), and every output pixel (row AND column) is blended independently
    /// using the same fractional 0-to-1 (outer-to-inner) position.</summary>
    public AltFlattenResult AltFlattenPage(
        Mat img,
        double[] topY, double[] botY, // indexed by local column, ascending from outer edge (index 0) to inner/gutter edge (index n-1)
        int[] sourceColumns,          // the actual source-image column for each local index (ascending outer->inner)
        double[] outerXAtRow, int outerRowStart, // row-indexed outer-edge x, index 0 = outerRowStart
        double[] innerXAtRow, int innerRowStart) // row-indexed inner (gutter or far-side) edge x, index 0 = innerRowStart
    {
        var n = topY.Length;

        // Defensive fallback for a genuinely featureless/low-signal crop (e.g. a fixed-frame
        // search region with too little edge content for Method 4 to find anything —
        // AltTraceSideEdgeMethod4Pair's own doc comment says "callers already handle a
        // zero-width side trace" but nothing downstream actually did, which crashed
        // ProcessFixedFrames outright with Math.Clamp(x, 0, -1) on any blank/low-texture
        // capture). No row-indexed edge trace means no tilt data to correct with — fall back to
        // a single constant column (the crop's own left/right bound) at every row, which
        // degrades to exactly the untilted, no-correction case rather than crashing.
        var foundRealEdges = outerXAtRow.Length > 0 && innerXAtRow.Length > 0;
        if (outerXAtRow.Length == 0)
            outerXAtRow = new[] { (double)(sourceColumns.Length > 0 ? sourceColumns[0] : 0) };
        if (innerXAtRow.Length == 0)
            innerXAtRow = new[] { (double)(sourceColumns.Length > 0 ? sourceColumns[^1] : img.Cols - 1) };
        // WIDTH: cubic-bow fit (notebook's Cell 9B-alt / Method 2), the product owner's
        // designated mandatory reference width correction ("Method 4 + its cubic-bow remap") —
        // see FitWidthCubic's own doc comment for why this supersedes the plain arc-length sum
        // of the raw traced curve (AltArcLengthPhysicalX, still used by AltArcLength/HEIGHT
        // below, just no longer for the page-width estimate).
        var (physX, fittedAlpha) = FitWidthCubic(topY, botY, widthGain: 1.0);
        var outW = Math.Max(1, (int)Math.Round(physX[^1]));
        // Sanity bound: the cubic-bow width fit is only meaningful with real per-column
        // height-variation evidence. On a span too narrow/degenerate for Method 4 to have found
        // real content (e.g. a synthetic/low-texture image where sign-change detection locks
        // onto a near-zero-width sliver instead of the true page span), the fit can still run
        // but produces a physically nonsensical output (confirmed: a 2-column span produced a
        // literal 2px-wide output on a 3840px-wide source). Rather than emit that, fall back to
        // the raw source-column span — still not "correct" if the span itself is wrong, but at
        // least a recognizable, non-degenerate crop instead of an unusable sliver.
        if (outW < sourceColumns.Length / 4) outW = Math.Max(1, sourceColumns.Length);

        // HEIGHT: arc length of the outer edge — but measured against a SMOOTHED copy of
        // outerXAtRow, not the raw trace used for the actual per-pixel mapX lookup below. The
        // raw side-edge trace (AltTraceSideEdge) is deliberately left unsmoothed (see Phase 3's
        // AltDetectSpreadBoundary remarks — smoothing risked flattening genuine tilt/curl for
        // the crop/overlay use case), but that same small per-row jitter is fatal to an
        // arc-length SUM: confirmed on a real fixture (IMG_0021 right page) where the raw
        // trace's sum-of-|delta| was 1498px over 2696 rows against a net displacement of only
        // 4px — i.e. almost entirely jitter, not real path length — inflating outH to 3836
        // against a true ~2700px content span and producing visible ghosting/doubled text in
        // the flattened output (each output row advancing far less than one real source pixel).
        // Smoothing ONLY for this length measurement fixes it without reopening Phase 3's
        // decision to keep the raw trace itself unsmoothed for cropping.
        // Fallback height evidence, independent of the row-indexed side trace: the page's own
        // top/bottom curve span (topY/botY, always populated — even when the span/side-edge
        // detection above collapsed to the single-element constant-column fallback, e.g. on a
        // real page with a deliberate edge gap where the side trace itself found nothing but
        // the top/bottom trace correctly measured the true ~1000px page height). Used both as
        // the "no real row-indexed trace" replacement for outerXAtRow.Length<=1 and as a lower
        // sanity floor even when a trace exists.
        var topBotHeight = n > 0 ? Math.Max(1, (int)Math.Round(botY.Zip(topY, (b, t) => b - t).Max())) : 1;

        var outerXSmooth = AltSavGolFilter(outerXAtRow, AltSavGolWindow, AltSavGolPolyDegree);
        var outerYs = new double[outerXAtRow.Length];
        for (var i = 0; i < outerYs.Length; i++) outerYs[i] = outerRowStart + i;
        var outerArcLen = AltArcLength(outerXSmooth, outerYs);
        var outH = Math.Max(1, (int)Math.Round(outerArcLen));
        // Same sanity bound as outW above, mirrored for height: even Sav-Gol-smoothed, a
        // per-row trace that's still jittery relative to its own net vertical span (a real
        // failure mode this method's own comment above already documents — "sum-of-|delta| was
        // 1498px ... against a net displacement of only 4px") can inflate arc length far past
        // the row count it was actually measured over. Bound against BOTH the raw row count the
        // arc length was measured over AND the independently-derived topBotHeight (the page's
        // own top/bottom curve span) — confirmed a single bound isn't tight enough on its own:
        // a generous 3x-rawRowSpan cap alone still let a real bug through (a synthetic
        // mock-capture image's side trace inflated arc length to 3656 against a true ~1400px
        // page height, comfortably under a 3x cap but still a physically wrong ~2.6x result).
        // A real page's traced arc length legitimately exceeds its straight-line height for
        // genuine curvature/tilt, but not by more than roughly 50% on anything this codebase has
        // seen — anything beyond that is jitter inflation, not real path length, so floor/ceiling
        // against topBotHeight's own independent measurement rather than trust arc length alone.
        var rawRowSpan = Math.Max(1, outerXAtRow.Length);
        if (outH > rawRowSpan * 3) outH = rawRowSpan;
        if (outerXAtRow.Length <= 1 || outH > topBotHeight * 3 / 2 || outH < topBotHeight / 4) outH = topBotHeight;

        var idxAxis = new double[n];
        for (var i = 0; i < n; i++) idxAxis[i] = i;

        var outPos = new double[outW];
        for (var i = 0; i < outW; i++) outPos[i] = i;
        var fracIdx = Interp(outPos, physX, idxAxis);
        var frac01 = new double[outW];
        var lastIdx = Math.Max(idxAxis[^1], 1e-9);
        for (var i = 0; i < outW; i++) frac01[i] = fracIdx[i] / lastIdx;

        var topAt = Interp(fracIdx, idxAxis, topY);
        var botAt = Interp(fracIdx, idxAxis, botY);

        var mapXData = new float[outH * outW];
        var mapYData = new float[outH * outW];
        for (var row = 0; row < outH; row++)
        {
            var t = outH <= 1 ? 0.0 : (double)row / (outH - 1);
            var rowOffset = row * outW;
            for (var col = 0; col < outW; col++)
            {
                var srcY = topAt[col] + t * (botAt[col] - topAt[col]);
                mapYData[rowOffset + col] = (float)srcY;

                var rowLookup = Math.Clamp(srcY - outerRowStart, 0, outerXAtRow.Length - 1);
                var outerX = InterpScalar(rowLookup, outerXAtRow);
                var innerRowLookup = Math.Clamp(srcY - innerRowStart, 0, innerXAtRow.Length - 1);
                var innerX = InterpScalar(innerRowLookup, innerXAtRow);

                mapXData[rowOffset + col] = (float)(outerX + frac01[col] * (innerX - outerX));
            }
        }

        using var mapXMat = new Mat(outH, outW, MatType.CV_32FC1);
        using var mapYMat = new Mat(outH, outW, MatType.CV_32FC1);
        mapXMat.SetArray(mapXData);
        mapYMat.SetArray(mapYData);
        var flattened = new Mat();
        Cv2.Remap(img, flattened, mapXMat, mapYMat, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.White);

        return new AltFlattenResult(flattened, outW, outH, fittedAlpha, foundRealEdges);
    }

    /// <summary>Linear interpolation of <paramref name="fp"/> (values) at each of
    /// <paramref name="x"/>, given ascending sample sites <paramref name="xp"/> — matches
    /// numpy's `np.interp` (clamps outside the sample range to the nearest endpoint, rather than
    /// extrapolating). Used to resample the top/bottom curves from the discrete per-column index
    /// axis onto the continuous arc-length output axis.</summary>
    private static double[] Interp(double[] x, double[] xp, double[] fp)
    {
        var result = new double[x.Length];
        for (var i = 0; i < x.Length; i++) result[i] = InterpScalar(x[i], xp, fp);
        return result;
    }

    private static double InterpScalar(double x, double[] xp, double[] fp)
    {
        if (x <= xp[0]) return fp[0];
        if (x >= xp[^1]) return fp[^1];
        var lo = 0;
        var hi = xp.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (xp[mid] <= x) lo = mid; else hi = mid;
        }
        var t = (x - xp[lo]) / (xp[hi] - xp[lo]);
        return fp[lo] + t * (fp[hi] - fp[lo]);
    }

    /// <summary>Same as <see cref="InterpScalar(double, double[], double[])"/> but for a
    /// row-indexed array where the "x" axis is implicitly 0..length-1 (used for the
    /// outer/inner-edge row lookups in <see cref="AltFlattenPage"/>).</summary>
    private static double InterpScalar(double index, double[] values)
    {
        if (index <= 0) return values[0];
        if (index >= values.Length - 1) return values[^1];
        var lo = (int)Math.Floor(index);
        var hi = Math.Min(values.Length - 1, lo + 1);
        var t = index - lo;
        return values[lo] + t * (values[hi] - values[lo]);
    }

    /// <summary>Result of flattening a full spread: both pages, the boundary detection that
    /// drove them, and each page's raw (pre-flatten) apparent pixel width — the straight-line
    /// column span the traced curves cover before arc-length correction, for verifying the
    /// arc-length invariant (flattened width must never be smaller than this raw span).</summary>
    public readonly record struct AltSpreadFlattenResult(AltFlattenResult Left, AltFlattenResult Right, AltSpreadBoundary Boundary, int LeftRawWidth, int RightRawWidth);

    /// <summary>Splits a detected spread (<see cref="AltDetectSpreadBoundary"/>) into left/right
    /// pages and flattens each via <see cref="AltFlattenPage"/>. Port of the notebook's Cell 7B
    /// (split) + Cell 9B (flatten), combined. The gutter line/notches ARE the split boundary —
    /// no separate polygon-mask-fill crop step is needed before flattening, since the arc-length
    /// remap already samples directly from the original image using the traced curves as its
    /// source geometry (the notebook's Cell 7B polygon-crop preview is a visualization aid, not
    /// a required intermediate step for the actual flatten in Cell 9B).</summary>
    public AltSpreadFlattenResult AltFlattenSpread(Mat img)
    {
        var boundary = AltDetectSpreadBoundary(img);
        var gutter = boundary.Gutter;

        var topNotchCol = gutter.TopNotch.Column;
        var botNotchCol = gutter.BottomNotch.Column;
        var w = img.Cols;

        // Row-indexed gutter x, reindexed onto each page's own full outer-edge row range —
        // rows outside the gutter's own [topNotch, botNotch) span are clamped to its nearest
        // endpoint (the gutter notch is always inside the page's row range, never wider than
        // it, so this only ever extends an already-near-straight segment, never extrapolates
        // something curved). Port of the notebook's `gutter_x_over_rows`.
        double[] GutterXOverRows(int rowStart, int rowEnd)
        {
            var gutterRows = new double[gutter.Line.Length];
            var gutterXs = new double[gutter.Line.Length];
            for (var i = 0; i < gutter.Line.Length; i++) { gutterRows[i] = gutter.Line[i].Row; gutterXs[i] = gutter.Line[i].Column; }
            var n = rowEnd - rowStart;
            var result = new double[n];
            for (var i = 0; i < n; i++) result[i] = InterpScalar(rowStart + i, gutterRows, gutterXs);
            return result;
        }

        // Each page's top/bottom trace must be bounded at its own OUTER edge, not the full
        // image width. AltTraceTopBottomEdge walks the entire image [0, w) unconditionally
        // (it has no notion of "the page's own boundary" — that's what the side-edge trace is
        // for), so beyond the real page edge it just keeps following whatever gradient is
        // nearby, typically background noise/clutter. Confirmed as a real bug on a real fixture
        // (Trapezoid_Image001's right page): the bottom curve visibly drooped across the black
        // desk background past the book's true right edge, producing wavy/diagonal text in the
        // flattened output because botY for columns beyond the real edge no longer tracked the
        // page at all. Fix: clamp each page's column range to its own AltTraceSideEdge outer
        // trace's own column extent.
        //
        // Use the MEDIAN of the side trace's columns, not Max()/Min(): confirmed via a second
        // real fixture (Trapezoid_Image002/003's left pages) that Max() is not robust — a
        // single outlier row (the trace briefly drifting into the background before recovering)
        // drags the bound far past the true edge, re-admitting a wide strip of background into
        // the page and producing a jagged/rippled artifact in the flattened output. The median
        // reflects where the trace sits for MOST rows, which is what "the page's true outer
        // edge" means when a small minority of rows are themselves wrong.
        var leftOuterColBound = (int)Math.Round(Median(Array.ConvertAll(boundary.Left.Columns, v => (float)v), 0, boundary.Left.Columns.Length));
        var rightOuterColBound = (int)Math.Round(Median(Array.ConvertAll(boundary.Right.Columns, v => (float)v), 0, boundary.Right.Columns.Length));

        // --- LEFT PAGE: local columns leftOuterColBound..min(topNotch,botNotch), ascending
        // (outer->gutter) — clamped to [0, gutter) in case the outer bound is noisy. ---
        var leftLo = Math.Clamp(leftOuterColBound, 0, Math.Min(topNotchCol, botNotchCol));
        var leftHi = Math.Min(topNotchCol, botNotchCol);
        var leftN = leftHi - leftLo + 1;
        var leftTopY = new double[leftN];
        var leftBotY = new double[leftN];
        var leftCols = new int[leftN];
        for (var i = 0; i < leftN; i++)
        {
            leftCols[i] = leftLo + i;
            leftTopY[i] = boundary.TopFinal[leftLo + i];
            leftBotY[i] = boundary.BottomFinal[leftLo + i];
        }
        var leftOuterX = boundary.Left.Columns;
        var leftGutterX = GutterXOverRows(boundary.Left.RowLo, boundary.Left.RowLo + leftOuterX.Length);
        var left = AltFlattenPage(img, leftTopY, leftBotY, leftCols, leftOuterX, boundary.Left.RowLo, leftGutterX, boundary.Left.RowLo);

        // --- RIGHT PAGE: local columns descending from rightOuterColBound down to
        // max(topNotch,botNotch), so index 0 = outer edge (right side), index n-1 = gutter —
        // mirrors the notebook's descending local_cols_r construction for the right page,
        // clamped to [gutter, w) in case the outer bound is noisy. ---
        var rightLo = Math.Max(topNotchCol, botNotchCol);
        var rightHi = Math.Clamp(rightOuterColBound, rightLo, w - 1);
        var rightN = rightHi - rightLo + 1;
        var rightTopY = new double[rightN];
        var rightBotY = new double[rightN];
        var rightCols = new int[rightN];
        for (var i = 0; i < rightN; i++)
        {
            rightCols[i] = rightHi - i;
            rightTopY[i] = boundary.TopFinal[rightHi - i];
            rightBotY[i] = boundary.BottomFinal[rightHi - i];
        }
        var rightOuterX = boundary.Right.Columns;
        var rightGutterX = GutterXOverRows(boundary.Right.RowLo, boundary.Right.RowLo + rightOuterX.Length);
        var rightFlat = AltFlattenPage(img, rightTopY, rightBotY, rightCols, rightOuterX, boundary.Right.RowLo, rightGutterX, boundary.Right.RowLo);
        // Horizontal flip — restores gutter-left/outer-right orientation (port of the
        // notebook's cv2.flip(..., 1)). OpenCvSharp's FlipMode.Y is the horizontal flip
        // (flip around the Y axis); FlipMode.X flips vertically and was confirmed wrong here
        // by an actual upside-down/mirrored right-page output on a real fixture.
        Cv2.Flip(rightFlat.Flattened, rightFlat.Flattened, FlipMode.Y);

        var leftRawWidth = leftN;
        var rightRawWidth = rightN;

        return new AltSpreadFlattenResult(left, rightFlat, boundary, leftRawWidth, rightRawWidth);
    }

    /// <summary>Diagnostic entry point for <c>DewarpDiagnostic altflatten</c>: runs
    /// <see cref="AltFlattenSpread"/> and returns both flattened pages PNG-encoded, plus a text
    /// report checking the arc-length invariant (flattened width must never be smaller than the
    /// raw polygon crop's own apparent width) — the report is generated by the CLI caller from
    /// these values, this method only computes them.</summary>
    public (byte[] LeftPng, byte[] RightPng, string Report) DebugAltFlatten(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        var result = AltFlattenSpread(mat);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Page size: {mat.Cols}x{mat.Rows}");

        var leftOk = result.Left.OutWidth >= result.LeftRawWidth;
        var rightOk = result.Right.OutWidth >= result.RightRawWidth;
        sb.AppendLine($"LEFT  -> out_w={result.Left.OutWidth} out_h={result.Left.OutHeight}  (raw apparent width {result.LeftRawWidth}, arc-length invariant {(leftOk ? "OK" : "VIOLATED")})");
        sb.AppendLine($"RIGHT -> out_w={result.Right.OutWidth} out_h={result.Right.OutHeight}  (raw apparent width {result.RightRawWidth}, arc-length invariant {(rightOk ? "OK" : "VIOLATED")})");

        Cv2.ImEncode(".png", result.Left.Flattened, out var leftPng);
        Cv2.ImEncode(".png", result.Right.Flattened, out var rightPng);
        result.Left.Flattened.Dispose();
        result.Right.Flattened.Dispose();

        return (leftPng, rightPng, sb.ToString());
    }

    // --- Phase 4.5: single-page boundary/flatten (fixed-frame calibration) ---

    /// <summary>Single-page variant of <see cref="AltFlattenSpread"/>, for fixed-frame captures
    /// (always exactly one page — no gutter, no split). Reuses the same top/bottom trace +
    /// left/right side-edge trace + arc-length flatten primitives built for the two-page-spread
    /// path; the only structural difference is there's no gutter notch, so both side edges are
    /// real page boundaries (left = "outer", right = "inner" by arbitrary convention — the
    /// flatten math itself is symmetric, it just needs two edges to blend between).</summary>
    public AltFlattenResult AltFlattenSinglePage(Mat img)
    {
        var boundary = AltDetectSinglePageBoundary(img);
        var topSmooth = boundary.TopFinal;
        var bottomSmooth = boundary.BottomFinal;
        var left = boundary.Left;
        var right = boundary.Right;

        // Same median-based robust outer-bound clamp used for spread pages (see AltFlattenSpread
        // — Max()/Min() were confirmed non-robust to a jittery trace on real fixtures). A
        // featureless/low-signal capture (nothing for Sobel to find at all — confirmed on a
        // flat solid-color synthetic test image) leaves left.Columns/right.Columns empty
        // (Method4Result's own "no span found" fallback), which collapsed the whole-image width
        // down to a degenerate few-pixel span here before any of it reached AltFlattenPage's own
        // guards. Fall back to the image's own full width in that case — the same "nothing to
        // detect, don't invent a tiny crop" spirit as the legacy pipeline's confidence gate,
        // just triggered by "no edge evidence at all" instead of a numeric confidence score.
        int leftBound, rightBound;
        if (left.Columns.Length == 0 || right.Columns.Length == 0)
        {
            leftBound = 0;
            rightBound = img.Cols - 1;
        }
        else
        {
            leftBound = (int)Math.Round(Median(Array.ConvertAll(left.Columns, v => (float)v), 0, left.Columns.Length));
            rightBound = (int)Math.Round(Median(Array.ConvertAll(right.Columns, v => (float)v), 0, right.Columns.Length));
            leftBound = Math.Clamp(leftBound, 0, img.Cols - 2);
            rightBound = Math.Clamp(rightBound, leftBound + 1, img.Cols - 1);
            // Both sides found SOME trace, but Method 4's span detection itself can still lock
            // onto an implausibly narrow sliver on a real image with too little interior
            // texture/gradient content for the sign-change signal to separate page from
            // background (confirmed: a synthetic mock-capture image — solid background, thin
            // stroked-outline "page", sparse text — produced a 2-column span here, which
            // propagated into AltFlattenPage as a 1px-wide/thousands-of-px-tall degenerate
            // output despite that method's own guards, since a "found something, just tiny"
            // span isn't the same case as "found literally nothing" above). Same "don't invent
            // an unusable sliver crop" spirit as the empty-trace branch — fall back to the full
            // image width when the detected span is too narrow to be a real page.
            if (rightBound - leftBound + 1 < img.Cols / 4)
            {
                leftBound = 0;
                rightBound = img.Cols - 1;
            }
        }

        var n = rightBound - leftBound + 1;
        var topY = new double[n];
        var botY = new double[n];
        var cols = new int[n];
        for (var i = 0; i < n; i++)
        {
            cols[i] = leftBound + i;
            topY[i] = topSmooth[leftBound + i];
            botY[i] = bottomSmooth[leftBound + i];
        }

        return AltFlattenPage(img, topY, botY, cols, left.Columns, left.RowLo, right.Columns, right.RowLo);
    }

    /// <summary>Diagnostic entry point for a fixed-frame single-page flatten, mirroring
    /// <see cref="DebugAltFlatten"/>'s shape.</summary>
    public byte[] DebugAltFlattenSinglePage(byte[] encodedImage)
    {
        using var mat = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        var result = AltFlattenSinglePage(mat);
        Cv2.ImEncode(".png", result.Flattened, out var png);
        result.Flattened.Dispose();
        return png;
    }
}
