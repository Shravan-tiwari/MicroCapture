using System;
using Avalonia;
using MicroCapture.Processing;

namespace MicroCapture.UI.Controls;

/// <summary>Which part of a fixed-frame rectangle a drag is resizing, or <see cref="Move"/> to
/// translate the whole rectangle. Frames are always axis-aligned (no perspective correction
/// needed for a stationary, straight-down copy-stand shot), so resizing is plain
/// min/max/clamp arithmetic — no convexity checks like CropReviewViewModel's quads need.</summary>
public enum FrameHandleKind { Move, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }

/// <summary>Pure geometry for editing fixed-frame rectangles — resize, translate, and handle
/// placement — kept free of any view or view-model state so the overlay editor can run it
/// directly during a drag without a round-trip through the view model on every pointer move.
///
/// <para>All operations work in the image's own pixel space and take the image dimensions
/// explicitly, so the same math serves a live-view-sized frame and a full-resolution one.</para></summary>
public static class FrameGeometry
{
    /// <summary>Smallest allowed frame edge, as a fraction of the image's shorter side.
    /// Deliberately fractional rather than an absolute pixel count: frames used to be authored
    /// only against a ~6000px calibration still, where a 20px floor was 0.3%, but they are now
    /// drawn on a ~960px live feed where that same 20px would be a floor 10x coarser. A fraction
    /// keeps the minimum consistent regardless of what the frames were authored against.</summary>
    public const double MinFrameSizeFraction = 0.02;

    public static readonly FrameHandleKind[] AllHandles =
    {
        FrameHandleKind.TopLeft, FrameHandleKind.Top, FrameHandleKind.TopRight,
        FrameHandleKind.Left, FrameHandleKind.Right,
        FrameHandleKind.BottomLeft, FrameHandleKind.Bottom, FrameHandleKind.BottomRight
    };

    /// <summary>Minimum frame edge in pixels for an image of the given size.</summary>
    public static double MinSize(double imageWidth, double imageHeight) =>
        Math.Max(1.0, Math.Min(imageWidth, imageHeight) * MinFrameSizeFraction);

    /// <summary>Where a given handle sits on a rectangle, in image space.</summary>
    public static Point HandlePoint(FixedFrameRect rect, FrameHandleKind handle) => handle switch
    {
        FrameHandleKind.TopLeft => new Point(rect.X, rect.Y),
        FrameHandleKind.Top => new Point(rect.X + rect.Width / 2, rect.Y),
        FrameHandleKind.TopRight => new Point(rect.X + rect.Width, rect.Y),
        FrameHandleKind.Left => new Point(rect.X, rect.Y + rect.Height / 2),
        FrameHandleKind.Right => new Point(rect.X + rect.Width, rect.Y + rect.Height / 2),
        FrameHandleKind.BottomLeft => new Point(rect.X, rect.Y + rect.Height),
        FrameHandleKind.Bottom => new Point(rect.X + rect.Width / 2, rect.Y + rect.Height),
        FrameHandleKind.BottomRight => new Point(rect.X + rect.Width, rect.Y + rect.Height),
        _ => default
    };

    /// <summary>Resizes <paramref name="current"/> from the given handle to the current pointer
    /// position (absolute, not delta-based — matches CropReviewWindow's own drag model). Clamps
    /// to image bounds and a minimum size so a frame can never collapse to zero or escape the
    /// image entirely.</summary>
    public static FixedFrameRect ResolveResize(FixedFrameRect current, FrameHandleKind handle, double pointerX, double pointerY, double imageWidth, double imageHeight)
    {
        var min = MinSize(imageWidth, imageHeight);

        double x = current.X, y = current.Y;
        double right = current.X + current.Width, bottom = current.Y + current.Height;

        switch (handle)
        {
            case FrameHandleKind.TopLeft:
                x = Math.Min(pointerX, right - min);
                y = Math.Min(pointerY, bottom - min);
                break;
            case FrameHandleKind.Top:
                y = Math.Min(pointerY, bottom - min);
                break;
            case FrameHandleKind.TopRight:
                right = Math.Max(pointerX, x + min);
                y = Math.Min(pointerY, bottom - min);
                break;
            case FrameHandleKind.Left:
                x = Math.Min(pointerX, right - min);
                break;
            case FrameHandleKind.Right:
                right = Math.Max(pointerX, x + min);
                break;
            case FrameHandleKind.BottomLeft:
                x = Math.Min(pointerX, right - min);
                bottom = Math.Max(pointerY, y + min);
                break;
            case FrameHandleKind.Bottom:
                bottom = Math.Max(pointerY, y + min);
                break;
            case FrameHandleKind.BottomRight:
                right = Math.Max(pointerX, x + min);
                bottom = Math.Max(pointerY, y + min);
                break;
        }

        x = Math.Clamp(x, 0, Math.Max(0, imageWidth - min));
        y = Math.Clamp(y, 0, Math.Max(0, imageHeight - min));
        right = Math.Clamp(right, x + min, imageWidth);
        bottom = Math.Clamp(bottom, y + min, imageHeight);

        return new FixedFrameRect(x, y, right - x, bottom - y);
    }

    /// <summary>Translates <paramref name="current"/> by the given image-space delta, clamped
    /// so the frame stays fully within the image.</summary>
    public static FixedFrameRect Move(FixedFrameRect current, double deltaX, double deltaY, double imageWidth, double imageHeight)
    {
        var x = Math.Clamp(current.X + deltaX, 0, Math.Max(0, imageWidth - current.Width));
        var y = Math.Clamp(current.Y + deltaY, 0, Math.Max(0, imageHeight - current.Height));
        return new FixedFrameRect(x, y, current.Width, current.Height);
    }

    /// <summary>Builds a rectangle from two opposite corners of a rubber-band drag, normalized
    /// so either drag direction works, then clamped to the image.</summary>
    public static FixedFrameRect FromDragCorners(Point origin, Point current, double imageWidth, double imageHeight)
    {
        var x = Math.Clamp(Math.Min(origin.X, current.X), 0, imageWidth);
        var y = Math.Clamp(Math.Min(origin.Y, current.Y), 0, imageHeight);
        var right = Math.Clamp(Math.Max(origin.X, current.X), 0, imageWidth);
        var bottom = Math.Clamp(Math.Max(origin.Y, current.Y), 0, imageHeight);
        return new FixedFrameRect(x, y, right - x, bottom - y);
    }

    /// <summary>Whether a rubber-band drag covered enough ground to become a real frame. A bare
    /// click yields a 0x0 rect; without this check every stray click on the live view would add
    /// an invisible frame and silently flip the batch into fixed-frame mode.</summary>
    public static bool IsCommittableSize(FixedFrameRect rect, double imageWidth, double imageHeight)
    {
        var min = MinSize(imageWidth, imageHeight);
        return rect.Width >= min && rect.Height >= min;
    }

    /// <summary>A default frame for the "Add Frame" button, staggered by <paramref name="index"/>
    /// so repeated clicks don't stack exactly on top of each other and become unreachable.</summary>
    public static FixedFrameRect DefaultFrame(double imageWidth, double imageHeight, int index)
    {
        var w = imageWidth * 0.3;
        var h = imageHeight * 0.3;
        var offset = (index % 5) * 0.025;
        var x = Math.Clamp(imageWidth * (0.08 + offset), 0, Math.Max(0, imageWidth - w));
        var y = Math.Clamp(imageHeight * (0.08 + offset), 0, Math.Max(0, imageHeight - h));
        return new FixedFrameRect(x, y, w, h);
    }
}
