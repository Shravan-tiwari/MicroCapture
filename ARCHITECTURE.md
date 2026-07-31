# Architecture

## Overview
The application follows a layered architecture to decouple the UI from the camera integration and background processing tasks.

```mermaid
graph TD
    UI[WPF Desktop UI] --> AppServices[Application Services]
    AppServices --> CaptureEngine[Capture & Workflow Engine]
    CaptureEngine --> CameraAbstraction[Camera Abstraction]
    CameraAbstraction --> CanonSDK[Canon EDSDK Adapter]
    
    CaptureEngine --> DB[(SQLite Database)]
    CaptureEngine --> JobQueue[Durable Job Queue]
    
    JobQueue --> ImageProcessing[Image Processing Pipeline]
    JobQueue --> QC[Quality Control]
    JobQueue --> OCR[Offline OCR - Tesseract]
    JobQueue --> Export[Export & DMS Sync]
```

## Layers
1. **UI Layer**: C# WPF (.NET 8). Handles Live View, thumbnail rendering, operator inputs, and status displays.
2. **Application Services**: Manages projects, batches, profiles, operators, and metadata.
3. **Capture Engine**: Coordinates camera capture, safe storage of original images (zero data loss), and enqueues background jobs.
4. **Camera Abstraction (`ICameraService`)**: Defines standard camera capabilities (Live View, Capture, Download, Settings) without Canon-specific knowledge.
5. **Background Processors**: Asynchronous workers consuming the durable job queue (Image Processing via OpenCvSharp, OCR via Tesseract, DMS Uploads via HTTP/REST).

## Zero-Data-Loss Principle
Camera captures are immediately downloaded, saved to disk, and logged in SQLite. Image processing happens strictly on derivatives. The master file is never automatically deleted.
