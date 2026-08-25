using System;
using System.IO;
using MicroCapture.Core.Models;
using SkiaSharp;

namespace MicroCapture.Processing;

/// <summary>Draws a <see cref="WatermarkPreset"/> onto an already-open SkiaSharp canvas. Shared
/// by <see cref="BatchExportService"/>'s real PDF export and <see cref="WatermarkPreviewRenderer"/>'s
/// live editor preview, so the two can never visually diverge.</summary>
public static class WatermarkRenderer
{
    /// <summary>Draws <paramref name="preset"/> onto <paramref name="canvas"/>, which is already
    /// sized to <paramref name="pageBitmap"/>'s own pixel dimensions (e.g.
    /// BeginPage(bitmap.Width, bitmap.Height)) — the same coordinate-space convention the
    /// existing invisible-OCR-text overlay relies on. The preset's X/Y/Width/Height/Opacity are
    /// normalized 0..1 fractions of the page, converted to this specific page's pixel space by
    /// simple multiplication — this is exactly why the preset stores fractions, not pixels: it
    /// makes the watermark land in the same proportional spot regardless of what page size the
    /// preset was originally authored against.</summary>
    public static void Draw(SKCanvas canvas, SKBitmap pageBitmap, WatermarkPreset preset)
    {
        var boxX = preset.X * pageBitmap.Width;
        var boxY = preset.Y * pageBitmap.Height;
        var boxW = preset.Width * pageBitmap.Width;
        var boxH = preset.Height * pageBitmap.Height;
        if (boxW <= 0 || boxH <= 0) return;

        var cx = boxX + boxW / 2;
        var cy = boxY + boxH / 2;

        canvas.Save();
        canvas.RotateDegrees((float)preset.RotationDegrees, (float)cx, (float)cy);

        var alpha = (byte)Math.Clamp((int)Math.Round(preset.Opacity * 255), 0, 255);

        if (preset.WatermarkType == "Logo" && !string.IsNullOrEmpty(preset.LogoImagePath) && File.Exists(preset.LogoImagePath))
        {
            DrawLogo(canvas, preset, boxW, boxH, cx, cy, alpha);
        }
        else if (preset.WatermarkType == "Text" && !string.IsNullOrWhiteSpace(preset.TextContent))
        {
            DrawText(canvas, preset, boxX, boxW, cy, alpha);
        }

        canvas.Restore();
    }

    private static void DrawLogo(SKCanvas canvas, WatermarkPreset preset, double boxW, double boxH, double cx, double cy, byte alpha)
    {
        using var data = SKData.Create(preset.LogoImagePath);
        using var logoImage = data == null ? null : SKImage.FromEncodedData(data);
        if (logoImage == null) return;

        // Preserve the logo's own aspect ratio, fit inside the box (letterboxed within it)
        // rather than stretching — an operator-supplied institutional logo distorted to fill an
        // arbitrary box is a much worse default than a small margin inside the box. Same
        // "fit inside, centered" convention every image preview in this app already uses.
        var scale = Math.Min(boxW / logoImage.Width, boxH / logoImage.Height);
        var drawW = logoImage.Width * scale;
        var drawH = logoImage.Height * scale;
        var drawX = cx - drawW / 2;
        var drawY = cy - drawH / 2;

        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
        canvas.DrawImage(logoImage,
            new SKRect((float)drawX, (float)drawY, (float)(drawX + drawW), (float)(drawY + drawH)),
            SKSamplingOptions.Default, paint);
    }

    private static void DrawText(SKCanvas canvas, WatermarkPreset preset, double boxX, double boxW, double cy, byte alpha)
    {
        var color = ParseHexColor(preset.TextColor) ?? new SKColor(128, 128, 128);
        using var paint = new SKPaint { Color = color.WithAlpha(alpha), IsAntialias = true };
        var typeface = string.IsNullOrWhiteSpace(preset.FontFamily) ? SKTypeface.Default : SKTypeface.FromFamilyName(preset.FontFamily);
        using var font = new SKFont(typeface) { Size = (float)Math.Max(preset.FontSize, 1) };

        // Scale the font so the text's rendered width fits boxW — the operator drags the box to
        // the desired footprint in the editor; the size that achieves that footprint on THIS
        // page's pixel dimensions is derived here rather than trusting the preset's stored
        // FontSize verbatim, since FontSize was seeded relative to whatever sample page the
        // preset was originally authored against and pages can vary in size across a batch, or
        // across different batches reusing the same preset. FontSize on the entity remains
        // useful only as the editor's own live-preview starting point.
        var measuredWidth = font.MeasureText(preset.TextContent);
        if (measuredWidth > 0) font.Size *= (float)(boxW / measuredWidth);

        // Baseline vertical-center approximation: SkiaSharp positions text by its baseline, not
        // its visual center, so nudge down by roughly a third of the font's own size to land the
        // glyphs' visual middle close to the box's own vertical center.
        canvas.DrawText(preset.TextContent, (float)boxX, (float)(cy + font.Size / 3), SKTextAlign.Left, font, paint);
    }

    private static SKColor? ParseHexColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return null;
        return new SKColor(r, g, b);
    }
}
