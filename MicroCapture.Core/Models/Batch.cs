using System;
using System.Collections.Generic;

namespace MicroCapture.Core.Models;

public class Batch
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BatchCode { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string Status { get; set; } = "Active"; // Active, Completed, Exported
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    
    public bool SplitBookPages { get; set; } = false;

    // Fixed-frame capture: one or more operator-calibrated rectangles reused for every
    // capture in the batch, instead of per-shot auto-crop detection. FixedFrames holds
    // "X,Y,Width,Height" rects (in FixedFrameImageWidth/Height's pixel space) joined by
    // ';' — see ImageProcessor.ParseFixedFrames/FormatFixedFrames.
    public bool UseFixedFrames { get; set; } = false;
    public string? FixedFrames { get; set; }
    public int FixedFrameImageWidth { get; set; }
    public int FixedFrameImageHeight { get; set; }

    public string PreferredExportFormat { get; set; } = "PDF";

    // Written into every processed TIFF's resolution tag (dots per inch) for this batch.
    // This is metadata only — it documents intended print/archival resolution, it does not
    // change the pixel dimensions the camera actually captured.
    public int Dpi { get; set; } = 300;

    // Corrects spine-curvature distortion on bound-book captures (the page bulging away from
    // flat near the gutter) — a per-column vertical remap, distinct from the perspective/quad
    // crop every batch already gets. See ImageProcessor.DetectDewarpCurve/ApplyDewarp.
    public bool DewarpEnabled { get; set; } = false;

    // Converts every processed page to pure black-and-white via Sauvola local-adaptive
    // thresholding, written out as a genuine 1-bit/CCITT-Group-4 TIFF (not just an 8-bit image
    // that happens to look bitonal) — smaller files and crisper OCR input, at the cost of
    // losing color/grayscale content. See ImageProcessor.ApplySauvolaBinarization/WriteBitonalTiff.
    public bool BinarizeEnabled { get; set; } = false;

    // Snapshots whichever CameraCalibration was active (see CameraCalibration.IsActive) at
    // Start Batch — so recalibrating the rig mid-batch, or later, can never retroactively
    // change how an already-queued batch's captures get undistorted. Null means no lens
    // calibration has been performed for this rig yet; every geometric correction still runs,
    // just without the one-time lens-undistortion pre-step. See ImageProcessor.Undistort.
    public string? CameraCalibrationId { get; set; }
    public CameraCalibration? CameraCalibration { get; set; }

    public Project? Project { get; set; }
    public ICollection<CaptureJob> Captures { get; set; } = new List<CaptureJob>();
}
