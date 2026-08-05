using System;

namespace MicroCapture.Processing;

/// <summary>Pure polygon geometry for crop-shape editing — no image I/O, no OpenCvSharp
/// dependency, so it's safe to call directly from the UI's live corner-drag handling as well
/// as anywhere else in the pipeline that needs the same rules. Kept separate from
/// <see cref="ImageProcessor"/> so detection, geometry, and pixel processing stay independently
/// testable and don't duplicate the "what makes a crop shape valid" rule in more than one place.</summary>
public static class CropGeometry
{
    /// <summary>True when the 4 points form a simple (non-self-intersecting) convex
    /// quadrilateral — every consecutive turn goes the same rotational direction. A rectangle
    /// or any reasonable perspective-skewed page outline satisfies this; a shape where a
    /// corner has been dragged past an adjacent edge does not.</summary>
    public static bool IsConvex(CropPoint[] corners)
    {
        if (corners.Length != 4) return false;

        int? sign = null;
        for (int i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var c = corners[(i + 2) % 4];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-6) continue; // near-collinear — ambiguous, don't fail on it
            var currentSign = Math.Sign(cross);
            if (sign == null) sign = currentSign;
            else if (sign != currentSign) return false;
        }
        return true;
    }

    /// <summary>Clamps a corner drag so the quad can never become concave or self-intersecting.
    /// Assumes <paramref name="corners"/> is currently valid (true by construction — crop
    /// shapes always start from a detected or full-frame rect/quad). Returns
    /// <paramref name="candidate"/> unchanged if it keeps the quad convex; otherwise returns
    /// the furthest point along the attempted movement that still does.</summary>
    public static CropPoint ClampCornerToConvex(CropPoint[] corners, int cornerIndex, CropPoint candidate)
    {
        if (corners.Length != 4 || cornerIndex < 0 || cornerIndex > 3) return candidate;

        var trial = (CropPoint[])corners.Clone();
        trial[cornerIndex] = candidate;
        if (IsConvex(trial)) return candidate;

        var original = corners[cornerIndex];
        var lo = 0.0; // original position: known convex
        var hi = 1.0; // candidate position: known non-convex, per the check above
        for (var i = 0; i < 20; i++)
        {
            var mid = (lo + hi) / 2.0;
            trial[cornerIndex] = Lerp(original, candidate, mid);
            if (IsConvex(trial)) lo = mid; else hi = mid;
        }

        return Lerp(original, candidate, lo);
    }

    private static CropPoint Lerp(CropPoint from, CropPoint to, double t) =>
        new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
}
