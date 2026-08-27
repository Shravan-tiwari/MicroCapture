using System;
using System.IO;
using MicroCapture.Core.Models;
using SkiaSharp;

namespace MicroCapture.Processing;

/// <summary>Feeds the watermark editor dialog's live debounced preview. Decodes a sample page
/// and composites the in-progress preset onto it via <see cref="WatermarkRenderer"/> — the same
/// drawing logic the real PDF export uses — so the editor preview and the actual export can
/// never visually diverge. Mirrors <see cref="CropPreviewRenderer"/>'s "OpenCV/Skia stays fully
/// contained in MicroCapture.Processing, UI only ever sees byte[]" boundary.</summary>
public static class WatermarkPreviewRenderer
{
    /// <summary>Returns PNG bytes of <paramref name="pageImagePath"/> with <paramref name="preset"/>
    /// drawn on top, or null if the page can't be decoded.</summary>
    /// <summary>Longest edge the preview is rendered at.
    ///
    /// <para>A real capture is around 6000x4000. Decoding that, compositing onto a surface of the
    /// same size and PNG-encoding the result took hundreds of milliseconds to seconds, and it ran
    /// again on every slider tick and keystroke — which is what made the watermark editor feel
    /// unresponsive. The watermark's geometry is stored as fractions of the page (see
    /// WatermarkRenderer), so it lands in the same proportional place at any scale: previewing at
    /// full capture resolution bought nothing a preview-sized render doesn't already show.</para></summary>
    private const int PreviewMaxEdge = 1400;

    private static readonly object SampleLock = new();
    private static string? _cachedSamplePath;
    private static SKBitmap? _cachedSample;

    /// <summary>The sample page, decoded and downscaled once and then reused. Decoding is the
    /// expensive half — a TIFF goes through OpenCV and a full PNG round-trip in
    /// ImageDecodeHelper — and the sample page doesn't change while the editor is open.</summary>
    private static SKBitmap? GetScaledSamplePage(string pageImagePath)
    {
        lock (SampleLock)
        {
            if (_cachedSample != null && string.Equals(_cachedSamplePath, pageImagePath, StringComparison.OrdinalIgnoreCase))
                return _cachedSample;

            var bytes = ImageDecodeHelper.GetDisplayBytes(pageImagePath);
            if (bytes == null) return null;
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded == null) return null;

            var longest = Math.Max(decoded.Width, decoded.Height);
            SKBitmap scaled;
            if (longest <= PreviewMaxEdge)
            {
                scaled = decoded.Copy();
            }
            else
            {
                var ratio = (double)PreviewMaxEdge / longest;
                var info = new SKImageInfo((int)(decoded.Width * ratio), (int)(decoded.Height * ratio));
                scaled = decoded.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                         ?? decoded.Copy();
            }

            _cachedSample?.Dispose();
            _cachedSample = scaled;
            _cachedSamplePath = pageImagePath;
            return _cachedSample;
        }
    }

    /// <summary>Drops the cached sample page. Called when the editor closes so a preview-sized
    /// copy of a page isn't held for the rest of the session.</summary>
    public static void ClearSampleCache()
    {
        lock (SampleLock)
        {
            _cachedSample?.Dispose();
            _cachedSample = null;
            _cachedSamplePath = null;
        }
    }

    public static byte[]? RenderPreview(string pageImagePath, WatermarkPreset preset)
    {
        var bitmap = GetScaledSamplePage(pageImagePath);
        if (bitmap == null) return null;

        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
        WatermarkRenderer.Draw(canvas, bitmap, preset);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data?.ToArray();
    }
}
