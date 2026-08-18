namespace MicroCapture.Processing;

/// <summary>Named preset value bundle for the manual adjustment panel — brightness, contrast
/// and saturation only (rotation/flip are geometry, not tone/color, and are never touched by
/// a preset; sharpness and white balance are left at the operator's current value too, since
/// only Document/Photo lean on them meaningfully and both leave them at a neutral default).</summary>
public readonly record struct AdjustmentPreset(double Brightness, double Contrast, double Saturation);

/// <summary>Pure validation/normalization for manual post-capture adjustment values — no image
/// I/O, no OpenCvSharp dependency, so it's safe to call directly from the UI thread while a
/// slider is being dragged. Mirrors <see cref="CropGeometry"/>'s separation of concerns:
/// detection, geometry, and pixel processing stay independently testable.</summary>
public static class AdjustmentGeometry
{
    public const double ToneMin = -1.0;
    public const double ToneMax = 1.0;
    public const double SharpnessMin = 0.0;
    public const double SharpnessMax = 1.0;

    /// <summary>Clamps a brightness/contrast/saturation/white-balance value to [-1, 1].</summary>
    public static double ClampTone(double value) => Clamp(value, ToneMin, ToneMax);

    /// <summary>Clamps a sharpness value to [0, 1].</summary>
    public static double ClampSharpness(double value) => Clamp(value, SharpnessMin, SharpnessMax);

    /// <summary>Normalizes any integer degree value to one of the four supported rotation
    /// steps (0, 90, 180, 270), wrapping around in either direction.</summary>
    public static int NormalizeRotation(int degrees)
    {
        var normalized = degrees % 360;
        if (normalized < 0) normalized += 360;
        // Snap to the nearest supported quarter-turn rather than rejecting off-grid input —
        // callers only ever add/subtract 90 via RotateClockwise/RotateCounterclockwise, so
        // this is just defensive rounding, not expected to trigger in practice.
        return (int)(System.Math.Round(normalized / 90.0) % 4) * 90;
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    // Document: moderate contrast boost and a mild brightness lift for text legibility, with
    // saturation pulled back slightly so colored paper/ink doesn't distract from the text.
    public static readonly AdjustmentPreset Document = new(Brightness: 0.08, Contrast: 0.25, Saturation: -0.15);

    // Photo: near-neutral tone, saturation left untouched — preserves the captured color for
    // the app's photo-cradle use case rather than pushing a "document" look onto photographs.
    public static readonly AdjustmentPreset Photo = new(Brightness: 0.0, Contrast: 0.05, Saturation: 0.0);

    // Grayscale: full desaturation via the same hue-safe HSV path ApplyManualAdjustments uses
    // elsewhere, tone otherwise untouched.
    public static readonly AdjustmentPreset Grayscale = new(Brightness: 0.0, Contrast: 0.0, Saturation: -1.0);

    // B&W: a stronger contrast push than Document plus full desaturation — a print-ready high-
    // contrast look, distinct from Batch.BinarizeEnabled's separate 1-bit/CCITT pipeline step;
    // this preset stays in the same continuous 8-bit adjustment space as every other control.
    public static readonly AdjustmentPreset BlackAndWhite = new(Brightness: 0.05, Contrast: 0.5, Saturation: -1.0);
}
