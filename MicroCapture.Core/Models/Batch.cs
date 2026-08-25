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
    public string? DeviceId { get; set; } = Environment.MachineName;
    
    public bool SplitBookPages { get; set; } = false;

    // NOTE: this batch used to carry a UseAltBoundaryPipeline opt-in toggle, selecting between
    // the original contour/confidence-based pipeline and an alternative ported from
    // tools/phaseA-prototype/boundary_prototype.ipynb. That toggle is gone — the notebook-ported
    // pipeline (now with a true Method 4 side-edge detector, not just the notebook's diagnostic-
    // only Cell 6A baseline) is the ONLY automatic boundary/dewarp path ImageProcessor.Process/
    // ProcessFixedFrames run; there is no longer a choice to make. The underlying DB column
    // (Batches.UseAltBoundaryPipeline) is left in place on existing databases as a harmless
    // orphaned column — see CaptureQueueService.EnsureCompatibleSchema — rather than migrated
    // away, since no code reads or writes it anymore.

    // Fixed-frame capture: one or more operator-calibrated rectangles reused for every
    // capture in the batch, instead of per-shot auto-crop detection. FixedFrames holds
    // "X,Y,Width,Height" rects (in FixedFrameImageWidth/Height's pixel space) joined by
    // ';' — see ImageProcessor.ParseFixedFrames/FormatFixedFrames.
    public bool UseFixedFrames { get; set; } = false;
    public string? FixedFrames { get; set; }
    public int FixedFrameImageWidth { get; set; }
    public int FixedFrameImageHeight { get; set; }

    public string PreferredExportFormat { get; set; } = "PDF";

    // Written into every processed TIFF's resolution tag (dots per inch) for this batch, AND
    // used to resample pixel dimensions — see ImageProcessor.BaselineDpi/ResizeForDpi for the
    // baseline/scaling convention (150 = native captured size, higher values upsample).
    public int Dpi { get; set; } = 150;

    // Corrects spine-curvature distortion on bound-book captures (the page bulging away from
    // flat near the gutter) — a per-column vertical remap, distinct from the perspective/quad
    // crop every batch already gets. See ImageProcessor.DetectDewarpCurve/ApplyDewarp.
    public bool DewarpEnabled { get; set; } = false;

    // Converts every processed page to pure black-and-white via Sauvola local-adaptive
    // thresholding, written out as a genuine 1-bit/CCITT-Group-4 TIFF (not just an 8-bit image
    // that happens to look bitonal) — smaller files and crisper OCR input, at the cost of
    // losing color/grayscale content. See ImageProcessor.ApplySauvolaBinarization/WriteBitonalTiff.
    public bool BinarizeEnabled { get; set; } = false;

    // Suppresses show-through from text/images printed on the reverse side of a thin page
    // bleeding into the scan. Local-background-relative depth thresholding — confirmed NOT
    // effective on colored-image bleedthrough (only grayscale/simple text show-through), so
    // this is an opt-in per-batch toggle, not on by default. See
    // ImageProcessor.TryRemoveBleedthrough.
    public bool BleedthroughEnabled { get; set; } = false;

    // Snapshots whichever CameraCalibration was active (see CameraCalibration.IsActive) at
    // Start Batch — so recalibrating the rig mid-batch, or later, can never retroactively
    // change how an already-queued batch's captures get undistorted. Null means no lens
    // calibration has been performed for this rig yet; every geometric correction still runs,
    // just without the one-time lens-undistortion pre-step. See ImageProcessor.Undistort.
    public string? CameraCalibrationId { get; set; }
    public CameraCalibration? CameraCalibration { get; set; }

    // Whether a watermark should be burned into this batch's exported PDF pages at
    // Finalize/Export time — kept independent of WatermarkPresetId so an operator can pick a
    // preset ahead of time and still switch watermarking off for one particular export,
    // mirroring DewarpEnabled/BinarizeEnabled/BleedthroughEnabled's "toggle independent of
    // configuration" shape.
    public bool WatermarkEnabled { get; set; } = false;

    // Live reference to the chosen WatermarkPreset — intentionally NOT snapshotted at Start
    // Batch, see WatermarkPreset's own doc comment. Null means no preset chosen yet, or the
    // previously-chosen preset was deleted (WatermarkPresetId is set null, not cascaded).
    public string? WatermarkPresetId { get; set; }
    public WatermarkPreset? WatermarkPreset { get; set; }

    public Project? Project { get; set; }
    public ICollection<CaptureJob> Captures { get; set; } = new List<CaptureJob>();
}
