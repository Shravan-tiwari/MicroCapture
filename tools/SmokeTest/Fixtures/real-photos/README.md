# Real-photo fixtures

Real camera captures used to validate the Phase 1 accuracy rework (boundary detection,
trapezoid correction, book-curve dewarp, deskew, binarization, lens calibration) against
actual capture conditions instead of only the synthetic shapes `tools/SmokeTest` generates.

- `book-curve/` — 4 photos of an open book on a black-background copy stand, showing visible
  page bow near the spine. Used to validate `DetectDewarpCurve`/`ApplyDewarp`.
- `trapezoid/` — 4 photos of a hand-held book photographed at an angle, showing perspective
  (trapezoidal) skew with no curvature. Used to validate `TryAutoCrop`/`WarpQuad`.
- `calibration/` — 8 JPEGs (quality 85, downsampled), a spread subset of a 40-image ChArUco
  calibration capture set (20 "left turn" / 20 "right turn" board tilt poses — pose variety
  for `Cv2.CalibrateCameraCharuco`, not two separate cameras). This subset is only large enough
  to smoke-test ChArUco marker detection — it is **not** enough images, nor high enough
  resolution, to produce a production-quality lens calibration. For that, supply the full
  40-image TIFF set locally (not committed — at ~25MB/file it's ~1GB) to
  `tools/DewarpDiagnostic --calibrate <folder>`.
- `before-processed-example.pdf` — an already-processed output from the pre-Phase-1 pipeline,
  showing the defects being fixed (visible skew, wavy/curved borders, binarization speckle).
  Used as the visual "before" reference when comparing `tools/DewarpDiagnostic` output.

Run `tools/DewarpDiagnostic` against this folder to print detection/skew/dewarp metrics and
write processed output for visual before/after comparison. See that project's own notes for
usage.
