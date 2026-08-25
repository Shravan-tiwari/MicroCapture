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

    // The CaptureJob this result belongs to — stamped by BackgroundProcessingWorker after
    // Process()/ProcessFixedFrames returns. Needed because several sibling jobs (one per fixed
    // frame) can share the same OriginalFilePath, so JobCompleted's UI handler can no longer use
    // OriginalFilePath alone to find the one thumbnail this result is actually for.
    public string JobId { get; set; } = string.Empty;

    // Auto-crop
    public bool WasCropped { get; set; }
    public double CropConfidence { get; set; }

    // Deskew
    public bool WasDeskewed { get; set; }
    public double OriginalSkewDegrees { get; set; }
    public double AppliedCorrectionDegrees { get; set; }

    // Binarization — true when this page was thresholded to pure black-and-white and written
    // as a genuine 1-bit/CCITT-G4 TIFF (see ImageProcessor.WriteBitonalTiff), not the normal
    // 8-bit color/grayscale TIFF. WriteTiff call sites read this to pick the right writer.
    public bool WasBinarized { get; set; }

    // QC results
    public double BlurScore { get; set; }
    public double ExposureScore { get; set; }
    public string QcVerdict { get; set; } = "Pending"; // PASS, WARNING, FAIL
    public string OcrStatus { get; set; } = "Pending";

    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
}
