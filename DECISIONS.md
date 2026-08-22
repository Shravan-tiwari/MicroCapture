# Architecture Decision Records

## ADR-001: Technology Stack
- **Decision:** Use C# with Avalonia (.NET 8) for UI, SQLite for database, OpenCvSharp for CV, and Tesseract for OCR.
- **Reason:** C# and Avalonia provide a responsive desktop UI while retaining a mock-camera workflow on non-Windows systems. SQLite provides local transactional reliability.
- **Status:** Accepted

## ADR-002: Camera Abstraction
- **Decision:** Wrap Canon EDSDK calls in a generic `ICameraService`.
- **Reason:** To allow development without physical hardware using a MockCameraService, and support other camera vendors or models in the future.
- **Status:** Accepted

## ADR-003: Zero-Data-Loss Capture
- **Decision:** Capture -> Download -> Safe Write -> DB Log -> Enqueue Job.
- **Reason:** The original raw/jpeg capture must never be lost due to crashes or failed processing.
- **Status:** Accepted

## ADR-004: Fixed Frames Are Edited Live, Not Calibrated
- **Decision:** Frames are drawn and adjusted directly on the live view at any time. There is no
  "use fixed frames" toggle and no modal calibration step — the frame count alone determines the
  mode (zero = auto-detect, one or more = crop to those regions).
- **Reason:** The previous flow made frame geometry a two-step, up-front commitment: tick a
  checkbox, then fire a throwaway full-resolution shot and place rectangles on a still image,
  repeating the whole ritual to change anything. Operators adjust framing constantly as the rig
  and the material change, so the geometry belongs where they are already looking.
- **Status:** Accepted

## ADR-005: Frames Store Pixels Plus a Reference Resolution
- **Decision:** Keep the existing `"X,Y,W,H"` pixel format for `Batch.FixedFrames` and treat
  `FixedFrameImageWidth/Height` as the authoritative space those coordinates live in.
  `ProcessFixedFrames` projects them onto each capture's own resolution.
- **Reason:** Frames are now authored against a ~960px live feed but applied to ~6000px captures,
  so some resolution mapping is unavoidable. Storing fractions instead would have required a
  format change (`F1` formatting is useless over a 0–1 range) and a way to distinguish old pixel
  specs from new fractional ones, since a small fractional value is a legal pixel rect. Reference
  dimensions avoid both, need no migration, and match the existing `LensCalibration`
  `CalibratedWidth/Height` pattern. Old batches keep working and become *more* correct, since
  their reference dims were always recorded but never honored downstream.
- **Status:** Accepted
