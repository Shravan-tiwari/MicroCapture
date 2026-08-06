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
}
