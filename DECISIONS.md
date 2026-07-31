# Architecture Decision Records

## ADR-001: Technology Stack
- **Decision:** Use C# with WPF (.NET 8) for UI, SQLite for database, OpenCvSharp for CV, and Tesseract for OCR.
- **Reason:** C# and WPF provide a mature, highly responsive native Windows desktop experience required for smooth Live View and multithreaded hardware interactions. SQLite is perfect for local transactional reliability.
- **Status:** Proposed (Pending developer approval)

## ADR-002: Camera Abstraction
- **Decision:** Wrap Canon EDSDK calls in a generic `ICameraService`.
- **Reason:** To allow development without physical hardware using a MockCameraService, and support other camera vendors or models in the future.
- **Status:** Accepted

## ADR-003: Zero-Data-Loss Capture
- **Decision:** Capture -> Download -> Safe Write -> DB Log -> Enqueue Job.
- **Reason:** The original raw/jpeg capture must never be lost due to crashes or failed processing.
- **Status:** Accepted
