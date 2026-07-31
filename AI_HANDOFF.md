# AI HANDOFF

CURRENT STATE

Version:
0.3.0

Working:
- Repository initialized
- Project documentation created
- .NET 8 SDK installed & Avalonia UI shell scaffolded
- Core camera abstraction (`ICameraService`) and `MockCameraService` created
- Phase 3 Complete: SQLite database models (EF Core 8) for Project, Batch, CaptureJob and `CaptureQueueService`.

In progress:
- Phase 4: Canon EOS R8 integration / Live View UI wiring

Next:
- Wire the UI (MainWindow) to use MockCameraService and display Live View frames and trigger captures.
- Implement Canon EDSDK adapter (using C# wrappers for EDSDK).

Known issue:
- None

Last verified commit:
[See latest commit hash in git log]
