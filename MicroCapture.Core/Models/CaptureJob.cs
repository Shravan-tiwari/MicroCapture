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

    public Batch? Batch { get; set; }
}
