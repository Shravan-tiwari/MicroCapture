using System;

namespace MicroCapture.Core.Models;

public class CaptureJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BatchId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string OriginalFilePath { get; set; } = string.Empty;
    
    // Processing States
    public string ProcessingStatus { get; set; } = "Pending"; // Pending, InProgress, Completed, Failed
    public string QcStatus { get; set; } = "Pending";
    public string OcrStatus { get; set; } = "Pending";
    public string ExportStatus { get; set; } = "Pending";
    
    // Manual Crop Override
    public bool ManualOverrideApplied { get; set; } = false;
    public string? LeftCropBox { get; set; } // Format: "X,Y,Width,Height"
    public string? RightCropBox { get; set; }

    // Manual Dewarp Override — 5 control points per edge, pixel coords in this page's own
    // (post-crop) image space. Format: "top:x1,y1,x2,y2,x3,y3,x4,y4,x5,y5;bottom:x1,y1,...".
    // See ImageProcessor.ParseDewarpCurve/FormatDewarpCurve.
    public bool DewarpManualOverrideApplied { get; set; } = false;
    public string? DewarpCurve { get; set; }

    // Manual post-capture adjustments (rotate/flip/tone/color/sharpen) — applied after the
    // rest of the automatic pipeline (crop, dewarp, CLAHE enhancement), immediately before the
    // final TIFF write. See ImageProcessor.ApplyManualAdjustments/AdjustmentGeometry.
    // HasManualAdjustments distinguishes "never touched" from "explicitly reset to defaults",
    // which matters for batch-apply eligibility and for proving untouched pages are unaffected.
    public bool HasManualAdjustments { get; set; } = false;
    public int RotationDegrees { get; set; } = 0; // 0, 90, 180, 270 — applied clockwise
    public bool FlipHorizontal { get; set; } = false;
    public bool FlipVertical { get; set; } = false;
    public double Brightness { get; set; } = 0.0; // -1.0..+1.0
    public double Contrast { get; set; } = 0.0; // -1.0..+1.0
    public double Saturation { get; set; } = 0.0; // -1.0..+1.0
    public double Sharpness { get; set; } = 0.0; // 0.0..+1.0, unsharp-mask strength
    public double WhiteBalance { get; set; } = 0.0; // -1.0 (cool/blue) .. +1.0 (warm/amber)

    // Exact on-disk path(s) of this job's processed derivative(s), written by
    // BackgroundProcessingWorker after a successful Process/ProcessFixedFrames run — lets
    // downstream readers (BatchExportService.GetProcessedFilesForJob, export/cleanup code) find
    // the real output file(s) directly instead of globbing a folder by filename prefix. Multiple
    // outputs (split left/right pages, or one per fixed frame) are joined by ';', the same
    // convention Batch.FixedFrames/CaptureJob.DewarpCurve already use for multi-value string
    // fields. Null for older rows written before this field existed, and for any job still
    // Pending/InProgress/Failed — callers must fall back to a folder glob (or OriginalFilePath)
    // in that case; see GetProcessedFilesForJob's own three-tier fallback.
    public string? ProcessedFilePath { get; set; }

    // Sticky per-capture output file format, read directly at capture-enqueue time (see
    // MainWindowViewModel.SelectedCaptureFormat) rather than stored on Batch — the operator can
    // change it capture-to-capture within the same batch, unlike DPI/dewarp/binarize which are
    // fixed per batch. "TIFF" default matches all historical behavior (every capture before this
    // field existed was effectively TIFF). Binarized output is always written as TIFF regardless
    // of this value — see ImageProcessor's WriteTiff/WriteJpeg selection logic.
    public string CaptureFormat { get; set; } = "TIFF";

    public Batch? Batch { get; set; }
}
