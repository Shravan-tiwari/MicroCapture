using OpenCvSharp;
using CvAruco = OpenCvSharp.Aruco.CvAruco;
using ArucoDictionary = OpenCvSharp.Aruco.Dictionary;
using ArucoDetectorParameters = OpenCvSharp.Aruco.DetectorParameters;
using ArucoDictionaryName = OpenCvSharp.Aruco.PredefinedDictionaryName;
using ArucoCornerRefineMethod = OpenCvSharp.Aruco.CornerRefineMethod;

namespace MicroCapture.Processing;

/// <summary>Computes camera lens intrinsics/distortion from photos of a ChArUco calibration
/// board (a checkerboard with embedded ArUco fiducial markers on alternating squares).
///
/// The OpenCvSharp4 build this project references binds plain ArUco marker detection
/// (<see cref="CvAruco.DetectMarkers"/>) but not OpenCV's own CharucoBoard/
/// InterpolateCornersCharuco/CalibrateCameraCharuco helpers, so this reimplements the piece
/// those would otherwise provide: inferring each marker's position on the physical board from
/// its observed neighbors in one reference photo (<see cref="BuildMarkerGrid"/>), then
/// calibrating from marker centers directly via the standard (non-ChArUco)
/// <see cref="Cv2.CalibrateCamera(System.Collections.Generic.IEnumerable{System.Collections.Generic.IEnumerable{Point3f}}, System.Collections.Generic.IEnumerable{System.Collections.Generic.IEnumerable{Point2f}}, Size, double[,], double[], out Vec3d[], out Vec3d[], CalibrationFlags, TermCriteria?)"/>.
///
/// The inferred marker grid is a consistent, orthogonal, planar coordinate system for the
/// markers' true relative positions — each grid step is one hop to a real nearest-neighbor
/// marker, in whichever two directions the board's own physical marker layout actually places
/// them (empirically determined per photo by <see cref="EstimateLocalBasis"/>, not assumed to
/// be axis-aligned or diagonal). It is arbitrary-scale and not aligned to any absolute
/// real-world axis or origin. Neither of those matter for Phase 1: only the camera matrix and
/// distortion coefficients are used (via <see cref="ImageProcessor.Undistort"/>), and both are
/// invariant to the object-point coordinate system's absolute scale/origin/rotation as long as
/// it's a consistent rigid planar system, which this always produces.</summary>
public static class LensCalibrationService
{
    /// <summary><see cref="Calibration"/> is null on failure — check it (or <see cref="Success"/>)
    /// rather than relying on an exception or a nullable return, so <see cref="Warnings"/>
    /// (which explains *why* on failure, not just *that* it failed) is never discarded.</summary>
    public readonly record struct CalibrationOutcome(LensCalibration? Calibration, double ReprojectionErrorPx, int ImagesUsed, int ImagesTotal, IReadOnlyList<string> Warnings)
    {
        public bool Success => Calibration != null;
    }

    // Enough distinct grid neighbors to fit a trustworthy 2-axis basis (EstimateLocalBasis) and
    // to give CalibrateCamera a reasonably well-conditioned per-image pose. Public so the
    // calibration-capture UI can apply the same per-shot bar for immediate accept/reject
    // feedback (see CountDetectableMarkers) instead of only finding out after the fact.
    public const int MinMarkersPerImage = 6;

    private static readonly ArucoDictionaryName[] CandidateDictionaries =
    {
        ArucoDictionaryName.Dict4X4_50, ArucoDictionaryName.Dict4X4_100, ArucoDictionaryName.Dict4X4_250, ArucoDictionaryName.Dict4X4_1000,
        ArucoDictionaryName.Dict5X5_50, ArucoDictionaryName.Dict5X5_100, ArucoDictionaryName.Dict5X5_250, ArucoDictionaryName.Dict5X5_1000,
        ArucoDictionaryName.Dict6X6_50, ArucoDictionaryName.Dict6X6_100, ArucoDictionaryName.Dict6X6_250, ArucoDictionaryName.Dict6X6_1000,
        ArucoDictionaryName.Dict7X7_50, ArucoDictionaryName.Dict7X7_100, ArucoDictionaryName.Dict7X7_250, ArucoDictionaryName.Dict7X7_1000,
        ArucoDictionaryName.DictArucoOriginal,
    };

    /// <summary>Runs the full calibration pipeline against a set of ChArUco board photos: picks
    /// the ArUco dictionary the physical board actually uses, infers the board's marker layout
    /// from whichever single image detects the most markers, then calibrates from every image
    /// that has enough detected markers matching that layout. <see cref="CalibrationOutcome.Success"/>
    /// is false (with <see cref="CalibrationOutcome.Warnings"/> explaining why) when there isn't
    /// enough usable data to trust a result.</summary>
    public static CalibrationOutcome Calibrate(IReadOnlyList<string> imagePaths)
    {
        var warnings = new List<string>();
        CalibrationOutcome Fail() => new(null, 0, 0, imagePaths.Count, warnings);

        if (imagePaths.Count < 3)
        {
            warnings.Add($"Need at least 3 calibration images, got {imagePaths.Count}.");
            return Fail();
        }

        var dictionaryName = DetectDictionary(imagePaths, warnings);
        if (dictionaryName is not { } dictName)
        {
            warnings.Add("Could not identify a consistent ArUco dictionary from any candidate — is this actually a ChArUco/ArUco board?");
            return Fail();
        }

        using var dictionary = CvAruco.GetPredefinedDictionary(dictName);
        var parameters = new ArucoDetectorParameters { CornerRefinementMethod = ArucoCornerRefineMethod.Subpix };

        var perImage = new List<(string Path, int[] Ids, Point2f[] Centers, Size ImageSize)>();
        foreach (var path in imagePaths)
        {
            using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (gray.Empty()) { warnings.Add($"Could not decode '{path}'."); continue; }
            var (ids, centers) = DetectMarkerCenters(gray, dictionary, parameters);
            perImage.Add((path, ids, centers, gray.Size()));
        }

        if (perImage.Count == 0)
        {
            warnings.Add("No calibration images could be decoded.");
            return Fail();
        }

        var reference = perImage.OrderByDescending(p => p.Ids.Length).First();
        if (reference.Ids.Length < MinMarkersPerImage)
        {
            warnings.Add($"Best image only detected {reference.Ids.Length} markers (need {MinMarkersPerImage}+) — no image detected enough markers to infer the board layout.");
            return Fail();
        }

        var layout = BuildMarkerGrid(reference.Ids, reference.Centers);
        if (layout == null || layout.Count < MinMarkersPerImage)
        {
            warnings.Add($"Could not infer a consistent marker grid from '{Path.GetFileName(reference.Path)}' ({reference.Ids.Length} markers detected, {layout?.Count ?? 0} placed on a consistent grid).");
            return Fail();
        }
        warnings.Add($"Inferred board layout from '{Path.GetFileName(reference.Path)}': {layout.Count} of {reference.Ids.Length} detected markers placed on a consistent grid.");

        var objectPointsPerImage = new List<IEnumerable<Point3f>>();
        var imagePointsPerImage = new List<IEnumerable<Point2f>>();
        var imageSize = reference.ImageSize;
        foreach (var (path, ids, centers, size) in perImage)
        {
            if (size != imageSize)
            {
                warnings.Add($"Skipped '{Path.GetFileName(path)}': image size {size} doesn't match the reference image's {imageSize} — all calibration photos must be the same resolution.");
                continue;
            }

            var objPts = new List<Point3f>();
            var imgPts = new List<Point2f>();
            for (var i = 0; i < ids.Length; i++)
            {
                if (!layout.TryGetValue(ids[i], out var gridPos)) continue;
                objPts.Add(new Point3f(gridPos.I, gridPos.J, 0));
                imgPts.Add(centers[i]);
            }

            if (objPts.Count < MinMarkersPerImage)
            {
                warnings.Add($"Skipped '{Path.GetFileName(path)}': only {objPts.Count} detected markers matched the inferred board layout (need {MinMarkersPerImage}+).");
                continue;
            }

            objectPointsPerImage.Add(objPts);
            imagePointsPerImage.Add(imgPts);
        }

        if (objectPointsPerImage.Count < 3)
        {
            warnings.Add($"Only {objectPointsPerImage.Count} images had enough usable markers — need at least 3 for a calibration.");
            return Fail();
        }

        var cameraMatrix = new double[3, 3];
        var distCoeffs = new double[5];
        double rms;
        try
        {
            rms = Cv2.CalibrateCamera(objectPointsPerImage, imagePointsPerImage, imageSize, cameraMatrix, distCoeffs, out _, out _);
        }
        catch (Exception ex)
        {
            warnings.Add($"Cv2.CalibrateCamera failed: {ex.Message}");
            return Fail();
        }

        var calibration = new LensCalibration(cameraMatrix[0, 0], cameraMatrix[1, 1], cameraMatrix[0, 2], cameraMatrix[1, 2], distCoeffs, imageSize.Width, imageSize.Height);
        return new CalibrationOutcome(calibration, rms, objectPointsPerImage.Count, imagePaths.Count, warnings);
    }

    /// <summary>Quick per-capture feedback for the calibration UI: the most ArUco markers (of
    /// any candidate dictionary) detectable in a single photo — a low/zero count means
    /// "reposition and retry" before wasting the shot as one of the images later sent to
    /// <see cref="Calibrate"/>. Deliberately tries every candidate dictionary rather than
    /// caching whichever one <see cref="Calibrate"/> last picked, since this can run before any
    /// full calibration attempt has determined the board's actual dictionary.</summary>
    public static int CountDetectableMarkers(string imagePath)
    {
        using var gray = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
        if (gray.Empty()) return 0;
        var parameters = new ArucoDetectorParameters { CornerRefinementMethod = ArucoCornerRefineMethod.Subpix };
        var best = 0;
        foreach (var name in CandidateDictionaries)
        {
            using var dict = CvAruco.GetPredefinedDictionary(name);
            CvAruco.DetectMarkers(gray, dict, out _, out var ids, parameters, out _);
            if (ids.Length > best) best = ids.Length;
        }
        return best;
    }

    /// <summary>Diagnostic-only entry point (not used by <see cref="Calibrate"/>): dumps the
    /// basis-estimation and grid-inference intermediate values for one image, for
    /// troubleshooting a board that isn't calibrating.</summary>
    public static string DebugGridInference(string imagePath, ArucoDictionaryName dictName)
    {
        using var gray = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
        using var dictionary = CvAruco.GetPredefinedDictionary(dictName);
        var parameters = new ArucoDetectorParameters { CornerRefinementMethod = ArucoCornerRefineMethod.Subpix };
        var (ids, centers) = DetectMarkerCenters(gray, dictionary, parameters);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Markers detected: {ids.Length}");
        var spacing = MedianNearestNeighborDistance(centers);
        sb.AppendLine($"Median NN spacing: {spacing:F2}");

        var centroid = new Point2f(centers.Average(c => c.X), centers.Average(c => c.Y));
        var seedIdx = Enumerable.Range(0, ids.Length).OrderBy(i => Distance(centers[i], centroid)).First();
        var seedCenter = centers[seedIdx];

        var basis = EstimateLocalBasis(seedCenter, centers, spacing);
        if (basis == null) { sb.AppendLine("EstimateLocalBasis returned null at seed"); return sb.ToString(); }
        var (u, v) = basis.Value;
        sb.AppendLine($"u=({u.X:F2},{u.Y:F2}) len={Length(u):F2}");
        sb.AppendLine($"v=({v.X:F2},{v.Y:F2}) len={Length(v):F2}");
        var dot = u.X * v.X + u.Y * v.Y;
        var angleBetween = Math.Acos(Math.Clamp(dot / (Length(u) * Length(v)), -1, 1)) * 180 / Math.PI;
        sb.AppendLine($"angle between u,v = {angleBetween:F1} deg");

        var grid = BuildMarkerGrid(ids, centers);
        sb.AppendLine($"Grid placed: {grid?.Count ?? 0} / {ids.Length}");

        // Dump the seed's own neighbor decode so a near-miss threshold issue is visible.
        var det = u.X * v.Y - u.Y * v.X;
        sb.AppendLine($"Seed id={ids[seedIdx]} at ({seedCenter.X:F1},{seedCenter.Y:F1})");
        var neighborDump = Enumerable.Range(0, ids.Length)
            .Where(i => i != seedIdx)
            .Select(i =>
            {
                var dx = centers[i].X - seedCenter.X;
                var dy = centers[i].Y - seedCenter.Y;
                var a = (dx * v.Y - dy * v.X) / det;
                var b = (u.X * dy - u.Y * dx) / det;
                return (Id: ids[i], Dist: Distance(centers[i], seedCenter), A: a, B: b);
            })
            .OrderBy(n => n.Dist)
            .Take(10);
        foreach (var n in neighborDump)
            sb.AppendLine($"  id={n.Id} dist={n.Dist:F1} a={n.A:F3} b={n.B:F3}");

        return sb.ToString();
    }

    private static (int[] Ids, Point2f[] Centers) DetectMarkerCenters(Mat gray, ArucoDictionary dictionary, ArucoDetectorParameters parameters)
    {
        CvAruco.DetectMarkers(gray, dictionary, out var corners, out var ids, parameters, out _);
        var centers = corners.Select(c => new Point2f(c.Average(p => p.X), c.Average(p => p.Y))).ToArray();
        return (ids, centers);
    }

    /// <summary>Tries each standard predefined ArUco dictionary against a handful of sample
    /// images and keeps whichever detects the most total markers — the physical board's actual
    /// dictionary isn't recorded anywhere, so this determines it empirically instead of
    /// guessing.</summary>
    private static ArucoDictionaryName? DetectDictionary(IReadOnlyList<string> imagePaths, List<string> warnings)
    {
        var samples = imagePaths.Take(5).Select(p => Cv2.ImRead(p, ImreadModes.Grayscale)).Where(m => !m.Empty()).ToList();
        try
        {
            if (samples.Count == 0) return null;

            ArucoDictionaryName? best = null;
            var bestCount = 0;
            var parameters = new ArucoDetectorParameters();
            foreach (var name in CandidateDictionaries)
            {
                using var dict = CvAruco.GetPredefinedDictionary(name);
                var total = 0;
                foreach (var sample in samples)
                {
                    CvAruco.DetectMarkers(sample, dict, out _, out var ids, parameters, out _);
                    total += ids.Length;
                }
                if (total > bestCount) { bestCount = total; best = name; }
            }

            if (best is { } found)
                warnings.Add($"Detected ArUco dictionary {found} ({bestCount} markers across {samples.Count} sample image(s)).");
            return bestCount >= MinMarkersPerImage ? best : null;
        }
        finally
        {
            foreach (var s in samples) s.Dispose();
        }
    }

    private readonly record struct GridPos(int I, int J);

    /// <summary>Infers each marker's integer position on the board's own diagonal-neighbor
    /// lattice (see class remarks) from a single image's detected marker centers, via a
    /// breadth-first flood fill outward from the most centrally-located marker.
    ///
    /// The local basis (the pixel-space vectors representing "one lattice step") is
    /// re-estimated at *every* node from that node's own nearest neighbors
    /// (<see cref="EstimateLocalBasis"/>), rather than computed once globally and reused
    /// everywhere. A tilted calibration photo is a genuine projective transform, not an affine
    /// one, so the apparent pixel spacing/direction of "one board square" drifts gradually
    /// across the image — a single global basis (originally tried here, and observed to fail:
    /// on a moderately tilted real photo, the closest true neighbors decoded to non-integer,
    /// unrecognizable lattice coordinates because the global average was itself contaminated by
    /// that same drift) can't represent that, while a basis that updates as the flood fill
    /// walks across the image tracks it naturally.</summary>
    private static Dictionary<int, GridPos>? BuildMarkerGrid(int[] ids, Point2f[] centers)
    {
        if (ids.Length < MinMarkersPerImage) return null;

        var spacing = MedianNearestNeighborDistance(centers);
        if (spacing <= 0) return null;

        var centerOf = new Dictionary<int, Point2f>();
        for (var i = 0; i < ids.Length; i++) centerOf[ids[i]] = centers[i];

        var centroid = new Point2f(centers.Average(c => c.X), centers.Average(c => c.Y));
        var seedId = ids[Enumerable.Range(0, ids.Length).OrderBy(i => Distance(centers[i], centroid)).First()];

        var seedBasis = EstimateLocalBasis(centerOf[seedId], centers, spacing);
        if (seedBasis == null) return null;

        var grid = new Dictionary<int, GridPos> { [seedId] = new GridPos(0, 0) };
        var basisOf = new Dictionary<int, (Point2f U, Point2f V)> { [seedId] = seedBasis.Value };
        var queue = new Queue<int>();
        queue.Enqueue(seedId);

        while (queue.Count > 0)
        {
            var curId = queue.Dequeue();
            var curGrid = grid[curId];
            var curCenter = centerOf[curId];
            var (u, v) = basisOf[curId];
            var det = u.X * v.Y - u.Y * v.X;
            if (Math.Abs(det) < 1e-6) continue;

            foreach (var candId in ids)
            {
                if (grid.ContainsKey(candId)) continue;
                var candCenter = centerOf[candId];
                var dx = candCenter.X - curCenter.X;
                var dy = candCenter.Y - curCenter.Y;
                if (Math.Sqrt(dx * dx + dy * dy) > spacing * 1.8) continue; // cheap prefilter

                var a = (dx * v.Y - dy * v.X) / det;
                var b = (u.X * dy - u.Y * dx) / det;
                var ra = (int)Math.Round(a);
                var rb = (int)Math.Round(b);
                // Only trust a direct one-hop neighbor step — rejecting anything that doesn't
                // land close to an integer lattice point filters out non-neighbors and
                // detection noise. Accepts any of the 8 immediate neighbors (axis-aligned or
                // diagonal): which of those are actually *populated* with markers depends on
                // the physical board's own marker layout (dense grid vs. alternating
                // checkerboard squares), which this deliberately doesn't assume either way —
                // EstimateLocalBasis empirically finds whichever two directions the real
                // nearest neighbors actually sit in.
                if (Math.Abs(a - ra) > 0.3 || Math.Abs(b - rb) > 0.3) continue;
                if (Math.Abs(ra) > 1 || Math.Abs(rb) > 1 || (ra == 0 && rb == 0)) continue;

                grid[candId] = new GridPos(curGrid.I + ra, curGrid.J + rb);
                // Refine the newly placed node's own local basis — tracking perspective drift
                // when it's later dequeued to expand further — but *aligned to the parent's u/v*
                // (RefineLocalBasis), not independently re-picked. An independent re-pick (tried
                // first, and observed to fail on real data) has no memory of which physical axis
                // is "u" vs "v": nothing stops it from swapping their identity from node to
                // node, which silently corrupts the accumulated (I,J) integers into a shape
                // that doesn't correspond to any rigid grid at all — confirmed by the
                // real-photo trial run this replaced, which "succeeded" (every image used) but
                // produced a nonsensical calibration (fx/fy differing by 2x+, >700px
                // reprojection error) from exactly this kind of corrupted object-point data.
                basisOf[candId] = RefineLocalBasis(candCenter, centers, spacing, u, v) ?? (u, v);
                queue.Enqueue(candId);
            }
        }

        return grid;
    }

    /// <summary>Estimates "one lattice step" as a pixel-space (u, v) basis local to
    /// <paramref name="center"/>: u is the offset to its single closest neighbor within a
    /// plausible spacing range, and v is whichever other nearby neighbor's offset is most
    /// nearly perpendicular to u — directly from real observed neighbor positions, not an
    /// average over the whole image, so it isn't contaminated by perspective drift elsewhere on
    /// a tilted board.</summary>
    private static (Point2f U, Point2f V)? EstimateLocalBasis(Point2f center, Point2f[] centers, double spacing)
    {
        var candidates = centers
            .Where(c => Distance(c, center) > 1e-3)
            .Select(c => (Pt: c, Dist: Distance(center, c)))
            .Where(c => c.Dist >= spacing * 0.5 && c.Dist <= spacing * 1.8)
            .OrderBy(c => c.Dist)
            .Take(6)
            .ToList();
        if (candidates.Count < 2) return null;

        var u = new Point2f(candidates[0].Pt.X - center.X, candidates[0].Pt.Y - center.Y);
        Point2f? best = null;
        var bestAbsCos = double.MaxValue;
        for (var i = 1; i < candidates.Count; i++)
        {
            var cand = new Point2f(candidates[i].Pt.X - center.X, candidates[i].Pt.Y - center.Y);
            var absCos = Math.Abs((u.X * cand.X + u.Y * cand.Y) / (Length(u) * Length(cand)));
            if (absCos < bestAbsCos) { bestAbsCos = absCos; best = cand; }
        }
        return best.HasValue ? (u, best.Value) : null;
    }

    /// <summary>Like <see cref="EstimateLocalBasis"/>, but keeps axis identity locked to
    /// <paramref name="priorU"/>/<paramref name="priorV"/> — for each, picks whichever real
    /// nearby-neighbor direction at <paramref name="center"/> is most closely aligned in angle
    /// (allowing a sign flip, since the same physical axis can be observed pointing either way
    /// depending on which neighbor happens to exist), rather than re-deriving u/v from scratch.
    /// This is what makes the flood fill in <see cref="BuildMarkerGrid"/> track gradual
    /// perspective drift across a tilted photo without ever confusing "the row axis" for "the
    /// column axis" partway across the image.</summary>
    private static (Point2f U, Point2f V)? RefineLocalBasis(Point2f center, Point2f[] centers, double spacing, Point2f priorU, Point2f priorV)
    {
        var candidates = centers
            .Where(c => Distance(c, center) > 1e-3)
            .Select(c => new Point2f(c.X - center.X, c.Y - center.Y))
            .Where(v => Length(v) is var d && d >= spacing * 0.5 && d <= spacing * 1.8)
            .ToList();
        if (candidates.Count == 0) return null;

        var refinedU = ClosestByAngle(candidates, priorU) ?? priorU;
        var refinedV = ClosestByAngle(candidates, priorV) ?? priorV;
        return (refinedU, refinedV);
    }

    /// <summary>Among <paramref name="candidates"/>, the one whose direction is closest to
    /// <paramref name="target"/>'s (by absolute cosine similarity, so a candidate pointing
    /// exactly opposite counts as a perfect match and is sign-flipped to match
    /// <paramref name="target"/>'s own direction). Returns null if the best match still isn't
    /// within ~40° — rather than lock onto a plausible-but-wrong neighbor when the real one is
    /// missing (occluded, undetected, or off the edge of the frame).</summary>
    private static Point2f? ClosestByAngle(List<Point2f> candidates, Point2f target)
    {
        Point2f? best = null;
        var bestAbsCos = double.MinValue;
        foreach (var c in candidates)
        {
            var cos = (c.X * target.X + c.Y * target.Y) / (Length(c) * Length(target));
            var absCos = Math.Abs(cos);
            if (absCos > bestAbsCos)
            {
                bestAbsCos = absCos;
                best = cos >= 0 ? c : new Point2f(-c.X, -c.Y);
            }
        }
        return bestAbsCos >= Math.Cos(40 * Math.PI / 180) ? best : null;
    }

    private static double MedianNearestNeighborDistance(Point2f[] centers)
    {
        var nnDistances = new List<double>();
        foreach (var c in centers)
        {
            var best = double.MaxValue;
            foreach (var o in centers)
            {
                if (o.X == c.X && o.Y == c.Y) continue;
                var d = Distance(c, o);
                if (d < best) best = d;
            }
            if (best < double.MaxValue) nnDistances.Add(best);
        }
        if (nnDistances.Count == 0) return 0;
        nnDistances.Sort();
        return nnDistances[nnDistances.Count / 2];
    }

    private static double Length(Point2f p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);

    private static double Distance(Point2f a, Point2f b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
