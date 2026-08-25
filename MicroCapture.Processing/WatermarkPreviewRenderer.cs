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
    public static byte[]? RenderPreview(string pageImagePath, WatermarkPreset preset)
    {
        var bytes = ImageDecodeHelper.GetDisplayBytes(pageImagePath);
        if (bytes == null) return null;

        using var bitmap = SKBitmap.Decode(bytes);
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
