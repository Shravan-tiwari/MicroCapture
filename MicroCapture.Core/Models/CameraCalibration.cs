using System;

namespace MicroCapture.Core.Models;

/// <summary>One-time camera lens intrinsics/distortion calibration, computed by
/// LensCalibrationService (MicroCapture.Processing) from photos of a physical ChArUco
/// calibration board and applied via ImageProcessor.Undistort at the very start of the capture
/// pipeline, before any cropping. Mirrors Batch's fixed-frame calibration in spirit — a
/// calibration performed once against the physical rig, reused across many later captures —
/// but persists independently of any single batch, since a lens calibration outlives any one
/// project the way a fixed-frame rectangle doesn't.
///
/// Only one row should have <see cref="IsActive"/> set at a time (enforced by the calibration
/// UI, not the database) — that's the calibration new batches snapshot onto their own
/// <see cref="Models.Batch.CameraCalibrationId"/> at Start Batch, so recalibrating the rig
/// later can't retroactively change an already-queued batch's processing.</summary>
public class CameraCalibration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Operator-facing label — defaults to the connected camera's model/serial when available,
    // editable so multiple calibrations (e.g. before/after a lens clean or a recalibration)
    // stay distinguishable in history.
    public string Label { get; set; } = string.Empty;

    public DateTime CalibratedUtc { get; set; } = DateTime.UtcNow;

    // Pixel resolution the calibration photos were captured at — ImageProcessor.Undistort
    // scales the focal length/principal point if a later capture's resolution ever differs.
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    // "fx,fy,cx,cy" and "k1,k2,p1,p2,k3" — see ImageProcessor.FormatLensCalibration/
    // ParseLensCalibration for the exact format both this and Batch.CameraCalibration share.
    public string CameraMatrix { get; set; } = string.Empty;
    public string DistCoeffs { get; set; } = string.Empty;

    // RMS reprojection error in pixels, as returned by Cv2.CalibrateCamera — shown to the
    // operator so a bad calibration (too few/too similar photos, wrong board) is visible
    // rather than silently accepted.
    public double ReprojectionErrorPx { get; set; }

    public bool IsActive { get; set; }

    // Physical-size DPI calibration — entirely distinct from the lens intrinsics fields above
    // (Fx/Fy/Cx/Cy/DistCoeffs correct optical distortion in an arbitrary/unscaled coordinate
    // system; these four fields instead tie the rig's pixel grid to a real physical
    // measurement). Operator enters the known physical size of a calibration target
    // (TargetWidthInches/TargetHeightInches) and the app measures how many pixels that target
    // spans in a captured frame (MeasuredPixelWidth/MeasuredPixelHeight) — together they give
    // pixels-per-inch, the actual measured DPI of this rig at this camera distance/zoom. Null
    // means "not yet calibrated" — see ImageProcessor.MeasuredDpi for the uncalibrated fallback.
    public double? TargetWidthInches { get; set; }
    public double? TargetHeightInches { get; set; }
    public double? MeasuredPixelWidth { get; set; }
    public double? MeasuredPixelHeight { get; set; }
}
