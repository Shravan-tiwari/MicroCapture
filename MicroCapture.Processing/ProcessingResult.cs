using System;
using System.Collections.Generic;

namespace MicroCapture.Processing;

/// <summary>
/// Result of running an image through the processing pipeline.
/// </summary>
public class ProcessingResult
{
    public bool Success { get; set; }
    public List<string> OutputFilePaths { get; set; } = new();
    public string OriginalFilePath { get; set; } = string.Empty;

    // Auto-crop
    public bool WasCropped { get; set; }
    public double CropConfidence { get; set; }

    // Deskew
    public bool WasDeskewed { get; set; }
    public double OriginalSkewDegrees { get; set; }
    public double AppliedCorrectionDegrees { get; set; }

    // QC results
    public double BlurScore { get; set; }
    public double ExposureScore { get; set; }
    public string QcVerdict { get; set; } = "Pending"; // PASS, WARNING, FAIL

    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
}
