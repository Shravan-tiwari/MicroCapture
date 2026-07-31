# Implementation Plan

## PHASE 0: Repository + documentation + build system
- [x] Initialize Git
- [x] Create directory structure and persistent documentation
- [ ] Document final Windows technology stack
- [ ] Commit checkpoint

## PHASE 1: Desktop UI shell
- [ ] Create basic main operator UI shell (WPF)

## PHASE 2: Camera abstraction + MockCamera
- [ ] Create camera abstraction interfaces
- [ ] Implement MockCameraService

## PHASE 3: Capture Reliability Basics
- [ ] Initial SQLite schema/migration architecture
- [ ] Durable capture queue structure

## PHASE 4: Canon EOS R8 integration
- [ ] Canon EDSDK integration
- [ ] Live View

## PHASE 5: Reliable capture/download
- [ ] PC image download
- [ ] Crash recovery

## PHASE 6: Project/batch management
- [ ] Projects, Batches, Configurable Naming

## PHASE 7: Auto-crop/deskew/perspective
- [ ] Image Processing pipeline
- [ ] OpenCV Integration

## PHASE 8: Image enhancement & QC
- [ ] Brightness, contrast, blur detection

## PHASE 9: OCR & PDF
- [ ] Tesseract offline OCR
- [ ] Searchable PDF generation

## PHASE 10: Export & DMS Integration
- [ ] HTTP/REST synchronization
