using System;
using Avalonia;

namespace MicroCapture.UI.Controls;

/// <summary>Which part of a watermark box a drag is manipulating. Unlike
/// <see cref="FrameGeometry"/>'s fixed frames (always axis-aligned), a watermark can be rotated,
/// so a dedicated <see cref="Rotate"/> handle exists alongside the 8 ordinary resize handles.</summary>
public enum WatermarkHandleKind { Move, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight, Rotate }

/// <summary>A watermark's position/size/rotation, in normalized 0..1 fraction-of-page space —
/// the same space <see cref="MicroCapture.Core.Models.WatermarkPreset"/>'s X/Y/Width/Height/
/// RotationDegrees columns are stored in, so no conversion is needed between what the editor
/// manipulates and what gets persisted. X/Y is the box's top-left corner before rotation;
/// rotation pivots around the box's own center.</summary>
public readonly record struct WatermarkTransform(double X, double Y, double Width, double Height, double RotationDegrees);

/// <summary>Pure geometry for editing a watermark's position/size/rotation — kept free of any
/// view or view-model state, mirroring <see cref="FrameGeometry"/>'s separation, but extended
/// with rotation-aware math that fixed frames never needed.
///
/// <para><b>Clamp simplification.</b> Fully exact container-bounds clamping of a *rotated*
/// rectangle is nontrivial (rotated corners can extend outside the container even when the
/// box's unrotated footprint is inside it, or vice versa). Resize/Move clamp against the box's
/// own UNROTATED bounding rect only — an aggressively rotated, near-edge watermark can visually
/// extend slightly past the page edge while editing. This is a deliberate, documented
/// simplification (matching how lightweight sticker-style editors typically behave), not a bug
/// — do not "fix" it into full rotated-rect clamping without a real need.</para></summary>
public static class WatermarkGeometry
{
    /// <summary>Smallest allowed box edge, as a fraction of the page — same rationale/shape as
    /// <see cref="FrameGeometry.MinFrameSizeFraction"/>.</summary>
    public const double MinSizeFraction = 0.02;

    public static readonly WatermarkHandleKind[] AllResizeHandles =
    {
        WatermarkHandleKind.TopLeft, WatermarkHandleKind.Top, WatermarkHandleKind.TopRight,
        WatermarkHandleKind.Left, WatermarkHandleKind.Right,
        WatermarkHandleKind.BottomLeft, WatermarkHandleKind.Bottom, WatermarkHandleKind.BottomRight
    };

    public static double MinSize() => MinSizeFraction;

    public static Point Center(WatermarkTransform t) => new(t.X + t.Width / 2, t.Y + t.Height / 2);

    private static Point UnrotatedHandlePoint(WatermarkTransform t, WatermarkHandleKind handle) => handle switch
    {
        WatermarkHandleKind.TopLeft => new Point(t.X, t.Y),
        WatermarkHandleKind.Top => new Point(t.X + t.Width / 2, t.Y),
        WatermarkHandleKind.TopRight => new Point(t.X + t.Width, t.Y),
        WatermarkHandleKind.Left => new Point(t.X, t.Y + t.Height / 2),
        WatermarkHandleKind.Right => new Point(t.X + t.Width, t.Y + t.Height / 2),
        WatermarkHandleKind.BottomLeft => new Point(t.X, t.Y + t.Height),
        WatermarkHandleKind.Bottom => new Point(t.X + t.Width / 2, t.Y + t.Height),
        WatermarkHandleKind.BottomRight => new Point(t.X + t.Width, t.Y + t.Height),
        _ => Center(t)
    };

    private static Point RotateAround(Point p, Point pivot, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = p.X - pivot.X;
        var dy = p.Y - pivot.Y;
        return new Point(pivot.X + dx * cos - dy * sin, pivot.Y + dx * sin + dy * cos);
    }

    /// <summary>Where a resize handle sits, in the CONTAINER's (normalized) coordinate space,
    /// accounting for rotation — the unrotated handle position relative to Center, rotated by
    /// RotationDegrees, then added back to Center.</summary>
    public static Point HandlePoint(WatermarkTransform t, WatermarkHandleKind handle) =>
        RotateAround(UnrotatedHandlePoint(t, handle), Center(t), t.RotationDegrees);

    /// <summary>The rotation handle's position: a fixed offset above the Top handle's rotated
    /// position, along the box's own rotated "up" vector. <paramref name="offset"/> is in the
    /// same normalized space as the transform (the caller converts a fixed pixel offset into
    /// this space using its own display scale).</summary>
    public static Point RotationHandlePoint(WatermarkTransform t, double offset)
    {
        var top = UnrotatedHandlePoint(t, WatermarkHandleKind.Top);
        var unrotatedAbove = new Point(top.X, top.Y - offset);
        return RotateAround(unrotatedAbove, Center(t), t.RotationDegrees);
    }

    /// <summary>Resizes from a handle to an absolute pointer position (container/normalized
    /// space). The pointer position is first rotated INTO the box's local (unrotated) frame
    /// around Center, so ordinary axis-aligned min/max/clamp math applies, then the result keeps
    /// the same RotationDegrees.</summary>
    public static WatermarkTransform ResolveResize(WatermarkTransform current, WatermarkHandleKind handle, Point pointerPos)
    {
        var center = Center(current);
        var local = RotateAround(pointerPos, center, -current.RotationDegrees);

        var min = MinSize();
        double x = current.X, y = current.Y;
        double right = current.X + current.Width, bottom = current.Y + current.Height;

        switch (handle)
        {
            case WatermarkHandleKind.TopLeft:
                x = Math.Min(local.X, right - min);
                y = Math.Min(local.Y, bottom - min);
                break;
            case WatermarkHandleKind.Top:
                y = Math.Min(local.Y, bottom - min);
                break;
            case WatermarkHandleKind.TopRight:
                right = Math.Max(local.X, x + min);
                y = Math.Min(local.Y, bottom - min);
                break;
            case WatermarkHandleKind.Left:
                x = Math.Min(local.X, right - min);
                break;
            case WatermarkHandleKind.Right:
                right = Math.Max(local.X, x + min);
                break;
            case WatermarkHandleKind.BottomLeft:
                x = Math.Min(local.X, right - min);
                bottom = Math.Max(local.Y, y + min);
                break;
            case WatermarkHandleKind.Bottom:
                bottom = Math.Max(local.Y, y + min);
                break;
            case WatermarkHandleKind.BottomRight:
                right = Math.Max(local.X, x + min);
                bottom = Math.Max(local.Y, y + min);
                break;
        }

        x = Math.Clamp(x, 0, Math.Max(0, 1 - min));
        y = Math.Clamp(y, 0, Math.Max(0, 1 - min));
        right = Math.Clamp(right, x + min, 1);
        bottom = Math.Clamp(bottom, y + min, 1);

        return current with { X = x, Y = y, Width = right - x, Height = bottom - y };
    }

    /// <summary>Translates by a container/normalized-space delta, clamped to keep the box's
    /// UNROTATED bounding rect within the page (same simplification as <see cref="ResolveResize"/>).</summary>
    public static WatermarkTransform Move(WatermarkTransform current, double deltaX, double deltaY)
    {
        var x = Math.Clamp(current.X + deltaX, 0, Math.Max(0, 1 - current.Width));
        var y = Math.Clamp(current.Y + deltaY, 0, Math.Max(0, 1 - current.Height));
        return current with { X = x, Y = y };
    }

    /// <summary>Computes the new RotationDegrees from the pointer's current angle relative to
    /// Center. When <paramref name="snapToAngles"/> is true, snaps to the nearest 15° within a
    /// small tolerance — common design-tool convention for a modifier-held "free rotate" vs.
    /// unmodified "snap" (the modifier check itself lives in the control, which has access to
    /// PointerEventArgs.KeyModifiers; this method only does the raw angle math).</summary>
    public static double ResolveRotate(WatermarkTransform current, Point pointerPos, bool snapToAngles)
    {
        var center = Center(current);
        var dx = pointerPos.X - center.X;
        var dy = pointerPos.Y - center.Y;
        // Angle of the "up" vector (0deg = pointing up, matching how the rotation handle sits
        // above the box at RotationDegrees == 0) — atan2 measures from the positive X axis, so
        // add 90 degrees to align 0 with "up" and negate Y since normalized Y grows downward.
        var degrees = Math.Atan2(dx, -dy) * 180.0 / Math.PI;

        if (snapToAngles)
        {
            const double step = 15.0;
            degrees = Math.Round(degrees / step) * step;
        }

        return degrees;
    }
}
