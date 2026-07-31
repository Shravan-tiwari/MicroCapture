# AI HANDOFF

CURRENT STATE

Version:
0.2.0

Working:
- Repository initialized
- Project documentation created
- .NET 8 SDK installed & Avalonia UI shell scaffolded
- Core camera abstraction (`ICameraService`) and `MockCameraService` created
- Solution builds successfully
- Canon EDSDK (Windows) added to `lib/EDSDK` folder (gitignored)

In progress:
- Phase 3: Capture Reliability Basics (SQLite schema, durable capture queue structure)

Next:
- Implement SQLite local DB and migrations.
- Build durable queue for processing jobs.
- Wire the MockCameraService to the Main UI Live View to verify the shell.

Known issue:
- None

Last verified commit:
[See latest commit hash in git log]
