# AI HANDOFF

CURRENT STATE

Version:
0.4.0

Working:
- Repository initialized
- Project documentation created
- .NET 8 SDK installed & Avalonia UI shell scaffolded
- Core camera abstraction (`ICameraService`) and `MockCameraService` created
- Phase 3 Complete: SQLite database models (EF Core 8) for Project, Batch, CaptureJob and `CaptureQueueService`.
- Full operator UI with Live View, shortcuts, thumbnail strip, project/batch management.
- Image processing pipeline via OpenCvSharp (auto-crop, deskew, CLAHE enhancement, blur detection).
- Background worker for durable queue processing.

In progress:
- Phase 4: Canon EOS R8 integration (EDSDK)

Next:
- Implement `CanonCameraService` using EDSDK wrappers (or native interop).
- Implement OCR pipeline.
- Implement Export/DMS pipeline.

Known issue:
- None

Last verified commit:
[See latest commit hash in git log]
