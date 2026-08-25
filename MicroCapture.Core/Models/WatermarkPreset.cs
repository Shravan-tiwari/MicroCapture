using System;

namespace MicroCapture.Core.Models;

/// <summary>A named, reusable watermark configuration — text or logo, with position/size/
/// rotation/opacity — picked from a library and applied per-Batch (see
/// Batch.WatermarkPresetId) at PDF export time by WatermarkRenderer.Draw. Unlike
/// CameraCalibration, a Batch holds a LIVE reference to its preset rather than a snapshot:
/// watermarking only ever touches already-processed pages at Finalize/Export time, so there is
/// no "captured under stale settings" risk a snapshot would need to guard against, and editing
/// a preset (e.g. fixing a typo) should simply take effect on the next export.</summary>
public class WatermarkPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Operator-facing name shown in the Finalize dialog's preset picker — e.g. "Institution
    // Logo Bottom-Right", "Draft Stamp". Unique-in-practice but not DB-enforced, same laxness
    // as CameraCalibration.Label.
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // "Text" or "Logo" — a string discriminator, matching this codebase's existing convention
    // of string-typed mode fields (Batch.Status, Batch.PreferredExportFormat,
    // CaptureJob.ProcessingStatus) rather than an EF-mapped enum.
    public string WatermarkType { get; set; } = "Text";

    // Text-mode fields (ignored when WatermarkType == "Logo").
    public string? TextContent { get; set; }
    public string? FontFamily { get; set; }
    public double FontSize { get; set; } = 48.0;
    public string? TextColor { get; set; } = "#808080";

    // Logo-mode fields (ignored when WatermarkType == "Text"). LogoImagePath is the copied-in
    // managed asset path (see WatermarkAssetPaths), never the operator's original file path, so
    // the preset survives the source file moving or being deleted.
    public string? LogoImagePath { get; set; }

    // Geometry — normalized 0..1 fractions of the page, not pixels, so one preset renders
    // correctly across pages of different pixel dimensions. X/Y is the top-left corner of the
    // watermark's bounding box before rotation; rotation pivots around the box's own center.
    public double X { get; set; } = 0.7;
    public double Y { get; set; } = 0.85;
    public double Width { get; set; } = 0.2;
    public double Height { get; set; } = 0.1;
    public double RotationDegrees { get; set; } = 0.0;
    public double Opacity { get; set; } = 0.5;
}
