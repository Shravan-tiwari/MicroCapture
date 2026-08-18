using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>Alternative boundary-detection/split/flatten pipeline, ported from the
/// diagnostic-only Python/Jupyter prototype at tools/phaseA-prototype/boundary_prototype.ipynb
/// (never wired into production there). Selected per-batch via a toggle (see
/// Batch.UseAltBoundaryPipeline) alongside the original pipeline in ImageProcessor.cs — not a
/// replacement, so behavior for batches that don't opt in is unaffected.
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
    /// Left/right are deliberately NOT smoothed: the notebook's own Cell 10 comment claims they
    /// are "already a straight line by construction" (so Sav-Gol would add nothing), but reading
    /// the notebook's actual Cell 9 code shows the side-edge trace is a per-row point-by-point
    /// continuity walk with local bridging — not a fitted line — contradicting that comment. This
    /// was flagged in the port plan as a real, confirmed discrepancy, to be resolved empirically
    /// rather than by trusting the stale comment. Resolved here by inspecting this phase's own
    /// overlay (see <see cref="AltBoundaryOverlay"/>) on all 8 real fixtures: the side-edge
    /// traces already come out visually clean (the confidence-gated bridging in
    /// <see cref="AltTraceSideEdge"/> already removes the jitter Sav-Gol would otherwise smooth),
    /// and the side edges are frequently genuinely non-straight in real photos (e.g. a page's
    /// true physical curl, or the deliberate tilt in <c>Trapezoid_Image001</c>/`002` — see the
    /// fixtures) — smoothing them risks flattening real shape the same way over-aggressive
    /// smoothing was already shown to risk flattening the gutter V-notch. Top/bottom get Sav-Gol
    /// because the notebook applies it there unconditionally and because that curve is expected
    /// to be smooth by physical construction (a page edge doesn't have sharp corners along its
    /// own length, unlike text-line noise).</summary>
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

        var gutterSeedColumn = img.Cols / 2; // crude center seed — no prior gutter estimate to anchor to yet
        var gutter = AltDetectGutterNotch(topSmooth, bottomSmooth, gutterSeedColumn);

        // Clamp to valid image rows: Sav-Gol's least-squares fit can overshoot slightly beyond
        // the raw traced range right at a column's own edge (0 or w-1), where the smoothing
        // window is asymmetric/shrunk — confirmed via a real crash on Trapezoid_Image001 before
        // this clamp was added (AltTraceSideEdge indexing gxFlat with a row >= h).
        var rowTopLeft = Math.Clamp((int)Math.Round(topSmooth[0]), 0, img.Rows - 1);
        var rowBotLeft = Math.Clamp((int)Math.Round(bottomSmooth[0]), 0, img.Rows - 1);
        var rowTopRight = Math.Clamp((int)Math.Round(topSmooth[^1]), 0, img.Rows - 1);
        var rowBotRight = Math.Clamp((int)Math.Round(bottomSmooth[^1]), 0, img.Rows - 1);
        var gutterMidCol = Math.Clamp((int)Math.Round((gutter.TopNotch.Column + gutter.BottomNotch.Column) / 2.0), 0, img.Cols - 1);

        var left = AltTraceSideEdge(img, gx, rowTopLeft, rowBotLeft, 0, gutterMidCol, fromLeft: true);
        var right = AltTraceSideEdge(img, gx, rowTopRight, rowBotRight, gutterMidCol, img.Cols, fromLeft: false);

        return new AltSpreadBoundary(topRaw, topBridged, topSmooth, bottomRaw, bottomBridged, bottomSmooth, gutter, left, right, topBridgedCount, bottomBridgedCount);
    }

    private static int CountBridged(AltEdgePoint[] raw, double[] final)
    {
        var count = 0;
        for (var i = 0; i < raw.Length; i++)
            if (Math.Abs(raw[i].Row - final[i]) > 0.01) count++;
        return count;
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
    /// notebook's Cell 9A/9B: at each step, the arc-length increment is the AVERAGE of the
    /// top and bottom curves' own hypot(1, dy) step (each individually guaranteed >= 1, the
    /// straight-line step) — so the cumulative physical-x axis is guaranteed >= the raw pixel
    /// column count by construction. This is the invariant the notebook's two rejected
    /// width-correction approaches (closed-form circular-arc radius fit — numerically unstable;
    /// direct H0/h(x) integration — could produce an output NARROWER than the raw crop, which
    /// is physically impossible for a page that only bends) both violated. Returns a
    /// same-length array, index 0 = 0 (the starting edge).</summary>
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

    /// <summary>Result of <see cref="AltFlattenPage"/>: the flattened page image plus the
    /// output dimensions actually used (arc-length-derived, not the raw polygon crop's pixel
    /// span) — exposed so callers/diagnostics can verify the arc-length invariant (flattened
    /// width must never be smaller than the raw apparent crop width).</summary>
    public readonly record struct AltFlattenResult(Mat Flattened, int OutWidth, int OutHeight);

    /// <summary>Flattens one page (left or right half of a spread, or a whole single page — see
    /// <see cref="AltFlattenSinglePage"/>) via the notebook's arc-length remap (Cell 9A/9B,
    /// `remap_curved_page`). Unlike <see cref="RectifyWithBoundaryCurves"/> (this file's closest
    /// existing analog — a Coons patch blending 4 independently-fit edges), this uses only the
    /// TWO long edges (top/bottom, or for a single page with no gutter, still top/bottom) plus
    /// the two SHORT edges (outer + inner/gutter, each a row-indexed x-position, not necessarily
    /// a constant column) to drive the remap.
    ///
    /// WIDTH = arc length of the top/bottom curves, averaged (<see cref="AltArcLengthPhysicalX"/>)
    /// — guaranteed >= the raw pixel span. HEIGHT = the outer edge's own traced arc length (the
    /// least-foreshortened available measurement of true page height).
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
        var physX = AltArcLengthPhysicalX(topY, botY);
        var outW = Math.Max(1, (int)Math.Round(physX[^1]));

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
        var outerXSmooth = AltSavGolFilter(outerXAtRow, AltSavGolWindow, AltSavGolPolyDegree);
        var outerYs = new double[outerXAtRow.Length];
        for (var i = 0; i < outerYs.Length; i++) outerYs[i] = outerRowStart + i;
        var outerArcLen = AltArcLength(outerXSmooth, outerYs);
        var outH = Math.Max(1, (int)Math.Round(outerArcLen));

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

        return new AltFlattenResult(flattened, outW, outH);
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
        var topSmooth = AltSavGolFilter(topBridged, AltSavGolWindow, AltSavGolPolyDegree);
        var bottomSmooth = AltSavGolFilter(bottomBridged, AltSavGolWindow, AltSavGolPolyDegree);

        var rowTopLeft = Math.Clamp((int)Math.Round(topSmooth[0]), 0, img.Rows - 1);
        var rowBotLeft = Math.Clamp((int)Math.Round(bottomSmooth[0]), 0, img.Rows - 1);
        var rowTopRight = Math.Clamp((int)Math.Round(topSmooth[^1]), 0, img.Rows - 1);
        var rowBotRight = Math.Clamp((int)Math.Round(bottomSmooth[^1]), 0, img.Rows - 1);

        var left = AltTraceSideEdge(img, gx, rowTopLeft, rowBotLeft, 0, img.Cols / 2, fromLeft: true);
        var right = AltTraceSideEdge(img, gx, rowTopRight, rowBotRight, img.Cols / 2, img.Cols, fromLeft: false);

        // Same median-based robust outer-bound clamp used for spread pages (see AltFlattenSpread
        // — Max()/Min() were confirmed non-robust to a jittery trace on real fixtures).
        var leftBound = (int)Math.Round(Median(Array.ConvertAll(left.Columns, v => (float)v), 0, left.Columns.Length));
        var rightBound = (int)Math.Round(Median(Array.ConvertAll(right.Columns, v => (float)v), 0, right.Columns.Length));
        leftBound = Math.Clamp(leftBound, 0, img.Cols - 2);
        rightBound = Math.Clamp(rightBound, leftBound + 1, img.Cols - 1);

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
