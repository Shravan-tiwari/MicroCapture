# Architecture

## Overview
The application follows a layered architecture to decouple the UI from the camera integration and background processing tasks.

```mermaid
graph TD
    UI[Avalonia Desktop UI] --> AppServices[Application Services]
    AppServices --> CaptureEngine[Capture & Workflow Engine]
    CaptureEngine --> CameraAbstraction[Camera Abstraction]
    CameraAbstraction --> CanonSDK[Canon EDSDK Adapter]
    
    CaptureEngine --> DB[(SQLite Database)]
    CaptureEngine --> JobQueue[Durable Job Queue]
    
    JobQueue --> ImageProcessing[Image Processing Pipeline]
    JobQueue --> QC[Quality Control]
    JobQueue --> OCR[Offline OCR - Tesseract]
    JobQueue --> Export[Local Export]
    JobQueue -. planned .-> DMS[DMS Sync]
```

## Layers
1. **UI Layer**: C# Avalonia (.NET 8). Handles Live View, camera controls, thumbnail rendering, manual crop review, and status displays.
2. **Application Services**: Manages projects, batches, profiles, operators, and metadata.
3. **Capture Engine**: Coordinates camera capture, safe storage of original images (zero data loss), and enqueues background jobs.
4. **Camera Abstraction (`ICameraService`)**: Defines standard camera capabilities (Live View, Capture, Download, Settings) without Canon-specific knowledge.
5. **Background Processors**: Asynchronous workers consuming the durable job queue (Image Processing via OpenCvSharp, OCR via Tesseract, and local export). DMS upload via HTTP/REST remains planned.

## Zero-Data-Loss Principle
Camera captures are immediately downloaded, saved to disk, and logged in SQLite. Image processing happens strictly on derivatives. The master file is never automatically deleted.

## Capture lifecycle
`Capture → durable CaptureJob → Pending → InProgress → Completed/Failed`.

On worker startup, interrupted `InProgress` jobs return to `Pending`. A recapture marks earlier attempts for its page as `Superseded`, so only the replacement is exported. Manual crop clears prior derivatives and requeues the original image.
