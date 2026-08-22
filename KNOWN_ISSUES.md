# Known Issues

| Issue | Severity | Workaround | Planned Fix | Status |
|-------|----------|------------|-------------|--------|
| Canon controls require a physical Windows/EDSDK test pass; only properties exposed by the body/lens are shown. | High | Use the camera body controls. | Add a hardware integration checklist. | Open |
| No automated test project or image fixtures. | High | Verify a representative batch before production use. | Add unit and integration tests. | Open |
| DMS/HTTP synchronization is not implemented. | Medium | Export locally. | Implement an authenticated DMS connector. | Open |
| Live-view editing of fixed frames needs a Windows/EDSDK pass: confirm the Canon live stream's decoded size lands in the frame reference dims, and that live view and capture really do share an aspect ratio. | High | Capture one page and check the crop before shooting a batch; the app warns on an aspect mismatch. | Hardware verification on the real rig. | Open |
| If a camera's live view and capture have different aspect ratios, frames are stretched to fit rather than letterboxed. | Low | The app surfaces a one-time warning after the first capture. | Map the feed's true field of view if a real body needs it. | Open |
