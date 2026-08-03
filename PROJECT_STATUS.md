# Project Status

| Feature | Status | Tests | Notes |
|---------|--------|-------|-------|
| Project Initialization | DONE | - | Basic repo and docs |
| Desktop UI shell | DONE | PASS | Avalonia UI |
| Camera abstraction | DONE | PASS | Core interface |
| MockCameraService | DONE | PASS | SkiaSharp based |
| SQLite Schema | DONE | PASS | Entity Framework Core |
| Durable Capture Queue | DONE | PASS | CaptureQueueService |
| Canon EOS R8 integration | DONE | Hardware verification required | EDSDK capture, transfer, live view |
| Camera controls | DONE | Hardware verification required | Runtime-discovered EDSDK exposure/focus controls |
| Live View | DONE | PASS | Frame backpressure and auto-capture gating |
| Capture mechanism | DONE | Hardware verification required | Durable queue and recapture superseding |
| Image Processing | DONE | Needs image fixtures | Boundary auto-crop, perspective correction, deskew, enhancement |
| OCR | DONE | Needs image fixtures | Tesseract sidecars and basic searchable-PDF layer |
| Export | DONE | Needs integration tests | PDF, TIFF, JPG and PNG output verification |
| DMS integration | PLANNED | - | No HTTP/REST client yet |
