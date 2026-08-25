using System.IO;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>Produces display-ready encoded bytes for any file the processing pipeline writes.
/// OpenCV writes real TIFFs for processed derivatives, which neither SkiaSharp's own decoder
/// nor Avalonia's Skia-backed <c>Bitmap</c> decoder can read (confirmed: both reliably return
/// null/throw for a plain OpenCV-written TIFF). Every caller that wants to display or
/// re-encode a processed derivative — batch export and the UI thumbnail strip alike — needs
/// this same bridge, so it lives in exactly one place instead of being reimplemented per
/// caller.</summary>
public static class ImageDecodeHelper
{
    /// <summary>Returns PNG bytes for a TIFF (bridged through OpenCV's own decoder, which does
    /// read these files correctly), or the file's own bytes unchanged for any other format.
    /// Returns null if the file is missing or OpenCV fails to decode it.</summary>
    public static byte[]? GetDisplayBytes(string path)
    {
        if (!File.Exists(path)) return null;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".tif" or ".tiff"))
            return File.ReadAllBytes(path);

        using var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat.Empty()) return null;
        Cv2.ImEncode(".png", mat, out var pngBytes);
        return pngBytes;
    }

    /// <summary>Same as <see cref="GetDisplayBytes"/>, but returns only the given pixel
    /// rectangle — used to build a fixed frame's own thumbnail from the shared source capture,
    /// so the operator sees exactly that frame's region rather than the whole spread every
    /// sibling frame's job also points at. The rect is clamped to the decoded image's own
    /// bounds, so a stale/out-of-range rect degrades to the nearest valid crop instead of
    /// throwing. Returns null if the file is missing, fails to decode, or the clamped rect is
    /// empty.</summary>
    public static byte[]? GetCroppedDisplayBytes(string path, int x, int y, int width, int height)
    {
        if (!File.Exists(path)) return null;

        using var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat.Empty()) return null;

        var clampedX = System.Math.Clamp(x, 0, mat.Cols - 1);
        var clampedY = System.Math.Clamp(y, 0, mat.Rows - 1);
        var clampedW = System.Math.Clamp(width, 0, mat.Cols - clampedX);
        var clampedH = System.Math.Clamp(height, 0, mat.Rows - clampedY);
        if (clampedW <= 0 || clampedH <= 0) return null;

        using var cropped = new Mat(mat, new Rect(clampedX, clampedY, clampedW, clampedH));
        Cv2.ImEncode(".png", cropped, out var pngBytes);
        return pngBytes;
    }

    /// <summary>Exact pixel dimensions of a captured file, for callers that need the rig's true
    /// capture resolution rather than the image itself — a frame-size readout, or checking that
    /// the live feed and the capture really do share an aspect ratio. Decodes through OpenCV so
    /// it works for the TIFFs Skia can't read; callers should treat it as a background-thread
    /// operation. Returns null if the file can't be read.</summary>
    public static (int Width, int Height)? GetPixelSize(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var mat = Cv2.ImRead(path, ImreadModes.Color);
            return mat.Empty() ? null : (mat.Cols, mat.Rows);
        }
        catch
        {
            return null;
        }
    }
}
