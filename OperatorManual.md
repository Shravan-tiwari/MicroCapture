# Micrographics Capture Software - Operator Manual

Welcome to the Micrographics Capture Software. This tool is designed to provide high-speed, reliable digitization of microfilm and microfiche using a physical Canon EOS R8 camera.

## 1. Getting Started

### Prerequisites
- Windows 10/11 for Canon EOS R8 control through Canon EDSDK. macOS runs the mock camera for workflow development.
- Canon EOS R8 Camera connected via USB.
- `.NET 8` Runtime installed (if not running the self-contained executable).

### Launching the Application
Run `MicroCapture.UI.exe` on Windows or `MicroCapture.UI` on macOS.

## 2. Main Interface

The application interface consists of the following sections:
- **Top Toolbar**: Connection controls, Project/Batch configuration, and Page counters.
- **Main Live View**: A real-time feed from your camera.
- **Right Panel (Status & Controls)**: Focus/Exposure/Document QC status and large capture buttons.
- **Bottom Strip**: A scrolling history of your recent captures in the current batch.

## 3. Standard Workflow

1. **Connect Camera**
   Ensure your Canon camera is powered on and connected via USB. Click the **CONNECT** button in the top left. The status should turn to `CONNECTED` and you will see the Live View feed.
2. **Set camera controls**
   The right panel shows the settings supported by the connected camera and lens: exposure mode, shutter, aperture, ISO, exposure compensation, white balance, image quality, drive mode, and focus mode. Changes apply immediately; unsupported properties are omitted.
3. **Setup Project & Batch**
   - Enter a **Project Code** (e.g., `Archive_2026`).
   - Enter a **Batch Code** (e.g., `Box_01`).
   - Click **Start Batch**. This initializes the database and creates the output directory.
4. **Capture Images**
   Place your document beneath the camera.
   - Wait for the Live View overlay to display **READY TO CAPTURE** (a document boundary has been detected and the project/batch are set). Camera focus and exposure remain operator-controlled through the dashboard.
   - Press **SPACEBAR** or click the **CAPTURE** button to take a photo.
5. **Review Thumbnails and Crop**
   As you capture, thumbnails appear in the bottom strip. The background worker automatically processes the images (cropping, deskewing). OCR does *not* run automatically — see step 6.
   - If a page needs to be retaken, press **R**. The replacement supersedes the earlier image and only the replacement is exported.
   - Click a thumbnail for crop review. For a single page, enter the crop rectangle in source-image pixels. For a book, set the split percentage. Saving reprocesses the preserved original.
6. **OCR and Export**
   OCR runs on demand, not automatically: click **Run OCR** once thumbnails show Processed, or export straight to PDF (which runs OCR first if it hasn't already). This requires a separate **Tesseract OCR** install on the workstation (the CLI binary, on PATH or in its default Program Files location) — the app bundles only the English language data, not the OCR engine itself. If Tesseract isn't found, OCR is skipped and the status bar says so; PDFs exported without it have no searchable text layer.
   Once thumbnails show Processed, select PDF, TIFF, JPG, or PNG and click **Export Batch**. All output files are checked before export reports success.

## 4. Keyboard Shortcuts

- **SPACE**: Capture a new frame.
- **R**: Recapture the last frame.
- **A**: Toggle Auto-Capture. It captures when the live-view readiness condition is met, with a 1.5-second minimum interval.

## 5. Troubleshooting

- **Camera Not Connecting**: Ensure Canon EOS Utility is closed. Only one application can control the camera at a time.
- **Export Failed**: This means the background worker is still processing your images, or an image failed Quality Control. Check the bottom status bar for details.
- **Blurry Images**: Ensure your physical setup has adequate lighting and the camera is properly auto-focusing before pressing Capture.
