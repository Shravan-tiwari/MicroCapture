# AI HANDOFF

CURRENT STATE

Version:
0.5.0 (unverified build in this checkout)

Working:
- Repository initialized
- Project documentation created
- .NET 8 SDK installed & Avalonia UI shell scaffolded
- Core camera abstraction (`ICameraService`) and `MockCameraService` created
- Phase 3 Complete: SQLite database models (EF Core 8) for Project, Batch, CaptureJob and `CaptureQueueService`.
- Full operator UI with Live View, shortcuts, thumbnail strip, project/batch management.
- Image processing pipeline via OpenCvSharp (auto-crop, deskew, CLAHE enhancement, blur detection).
- Background worker for durable queue processing.

- Phase 4: Canon EOS R8 integration (EDSDK) completed.
- OCR pipeline (Tesseract) and local export pipeline completed.
- Runtime camera-settings dashboard, crash recovery, recapture superseding, perspective-corrected boundary detection, and TIFF output verification added.

In progress:
- Hardware validation of Canon camera controls and capture workflow.
- Add automated image-fixture and export tests.

Next:
- Implement DMS / HTTP synchronization.

Known issue:
- The local development environment currently has no dotnet CLI; build and hardware verification remain required.

Last verified commit:
[See latest commit hash in git log]
