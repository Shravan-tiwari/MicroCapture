using System;
using System.IO;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>Renders fast, debounced live-preview frames of a crop-in-progress for the Crop
/// Review UI. Caches a downscaled copy of the source image once (so repeated warps during
/// interactive corner-dragging stay cheap, unlike re-reading and re-warping the full-resolution
/// original on every frame) and warps through the same logic <see cref="ImageProcessor"/> uses
/// for the real output, so the preview is representative of what Save will actually produce.
/// Keeps OpenCvSharp fully contained in this project — the UI only ever sees encoded bytes,
/// the same "byte[] frame -> Avalonia Bitmap" pattern already used for live camera view.
/// Dispose when Crop Review closes to release the cached native image.</summary>
public sealed class CropPreviewRenderer : IDisposable
{
    private readonly Mat _downscaled;
    private readonly double _scale; // downscaled-space pixels = original-space pixels * _scale

    public int SourceWidth { get; }
    public int SourceHeight { get; }

    /// <summary>downscaled-space pixels = original-space pixels * Scale — the Crop Review
    /// dewarp editor works in this renderer's downscaled pixel space (see
    /// <see cref="RenderPreviewWithDewarp"/>) and needs this to convert control points back to
    /// full-resolution coordinates before saving.</summary>
    public double Scale => _scale;

    private CropPreviewRenderer(Mat downscaled, double scale, int sourceWidth, int sourceHeight)
    {
        _downscaled = downscaled;
        _scale = scale;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    public static CropPreviewRenderer? Create(string imagePath, int maxDimension = 1200)
    {
        if (!File.Exists(imagePath)) return null;
        try
        {
            using var src = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (src.Empty()) return null;

            var sourceWidth = src.Width;
            var sourceHeight = src.Height;
            var longEdge = Math.Max(sourceWidth, sourceHeight);
            var scale = longEdge > maxDimension ? maxDimension / (double)longEdge : 1.0;

            var downscaled = new Mat();
            if (scale >= 1.0)
                src.CopyTo(downscaled);
            else
                Cv2.Resize(src, downscaled,
                    new Size(Math.Max(1, (int)(sourceWidth * scale)), Math.Max(1, (int)(sourceHeight * scale))),
                    0, 0, InterpolationFlags.Area);

            return new CropPreviewRenderer(downscaled, scale, sourceWidth, sourceHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Warps the given crop shape (corners in the *original* image's pixel
    /// coordinates) against the cached downscaled copy and returns a JPEG-encoded preview
    /// frame, or null if the corners are invalid or rendering failed. Rendering failures are
    /// advisory — the live preview is an editing aid, never something that should crash the
    /// review window.</summary>
    public byte[]? RenderPreview(CropPoint[] corners)
    {
        if (corners.Length != 4) return null;
        try
        {
            var scaled = new Point2f[4];
            for (var i = 0; i < 4; i++)
                scaled[i] = new Point2f((float)(corners[i].X * _scale), (float)(corners[i].Y * _scale));

            using var warped = ImageProcessor.WarpQuad(_downscaled, scaled);
            Cv2.ImEncode(".jpg", warped, out var bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Same as <see cref="RenderPreview"/>, but also applies book-curve dewarp using
    /// control points already in this renderer's own downscaled pixel space (see
    /// <see cref="Scale"/>) — used by the Crop Review curve editor so its live preview reflects
    /// both the crop and the in-progress curve edit.</summary>
    public byte[]? RenderPreviewWithDewarp(CropPoint[] corners, DewarpModel dewarpModel)
    {
        if (corners.Length != 4) return null;
        try
        {
            var scaled = new Point2f[4];
            for (var i = 0; i < 4; i++)
                scaled[i] = new Point2f((float)(corners[i].X * _scale), (float)(corners[i].Y * _scale));

            using var warped = ImageProcessor.WarpQuad(_downscaled, scaled);
            using var dewarped = ImageProcessor.ApplyDewarp(warped, dewarpModel);
            Cv2.ImEncode(".jpg", dewarped, out var bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Same as <see cref="RenderPreview"/>, but also applies the manual adjustment
    /// stack (rotate/flip/tone/color/sharpen) — used by Crop Review's Adjust mode so its live
    /// preview reflects both the crop and the in-progress adjustment edit, via the exact same
    /// <see cref="ImageProcessor.ApplyManualAdjustments"/> the real pipeline uses.</summary>
    public byte[]? RenderPreviewWithAdjustments(CropPoint[] corners, int rotationDegrees, bool flipHorizontal, bool flipVertical, double brightness, double contrast, double saturation, double sharpness, double whiteBalance)
    {
        if (corners.Length != 4) return null;
        try
        {
            var scaled = new Point2f[4];
            for (var i = 0; i < 4; i++)
                scaled[i] = new Point2f((float)(corners[i].X * _scale), (float)(corners[i].Y * _scale));

            using var warped = ImageProcessor.WarpQuad(_downscaled, scaled);
            using var adjusted = ImageProcessor.ApplyManualAdjustments(warped, rotationDegrees, flipHorizontal, flipVertical, brightness, contrast, saturation, sharpness, whiteBalance);
            Cv2.ImEncode(".jpg", adjusted, out var bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _downscaled.Dispose();
}
