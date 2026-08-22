# Changelog

All notable changes to this project will be documented in this file.
The format is based on Keep a Changelog.

## [Unreleased]
### Added
- Fixed frames are now drawn and edited directly on the live view, at any time: drag empty
  space to create one, drag it to move, its handles to resize, and the × badge or DEL to
  remove. Frames can be drawn before a batch is started and carry onto it.
- Auto-capture now works in fixed-frame mode, triggering on content change inside the drawn
  frames rather than on a detected page boundary. It suspends while a frame is being edited.
- Phase 0 repository initialization and project documentation.

### Changed
- The "Use Fixed Frames" checkbox and the modal "Calibrate Frames" panel (with its throwaway
  full-resolution calibration shot) are gone. Frame count alone decides the mode: zero frames
  means auto-detect, one or more means crop to those regions.

### Fixed
- Fixed-frame crops now honor the resolution the frames were authored against. Frame rects
  were previously applied as direct pixel coordinates on the capture, so any batch whose
  capture size differed from its calibration shot cropped the wrong region.
