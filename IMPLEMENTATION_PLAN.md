# Implementation Plan

## PHASE 0: Repository + documentation + build system
- [x] Initialize Git
- [x] Create directory structure and persistent documentation
- [x] Document final Windows technology stack
- [x] Commit checkpoint

## PHASE 1: Desktop UI shell
- [x] Create basic main operator UI shell (Avalonia/XAML)

## PHASE 2: Camera abstraction + MockCamera
- [x] Create camera abstraction interfaces
- [x] Implement MockCameraService

## PHASE 3: Capture Reliability Basics
- [x] Initial SQLite schema/migration architecture
- [x] Durable capture queue structure

## PHASE 4: Canon EOS R8 integration
- [x] Canon EDSDK integration
- [x] Live View

## PHASE 5: Reliable capture/download
- [x] PC image download
- [x] Crash recovery (Background Queue)

## PHASE 6: Project/batch management
- [x] Projects, Batches, Configurable Naming

## PHASE 7: Auto-crop/deskew/perspective
- [x] Image Processing pipeline
- [x] OpenCV Integration

## PHASE 8: Image enhancement & QC
- [x] Brightness, contrast, blur detection

## PHASE 9: OCR & PDF
- [x] Tesseract offline OCR
- [x] Searchable PDF generation

## PHASE 10: Split Book Pages & Multi-Format Export
- [x] Intelligent 50/50 page splitting via OpenCV
- [x] Manual crop review UI for precise boundaries
- [x] Export to PDF, TIFF, JPG, and PNG (Local File System)

## PHASE 11: Operator Reliability and Camera Controls
- [x] Recover jobs interrupted while processing
- [x] Prevent superseded recaptures from exporting
- [x] Discover and apply supported Canon camera settings from the dashboard
- [x] Verify local export files are written before reporting success
- [ ] Hardware verification on EOS R8 and representative document fixtures

## PHASE 12: DMS Integration
- [ ] Implement authenticated HTTP/REST batch synchronization
