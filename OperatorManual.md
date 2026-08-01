# Micrographics Capture Software - Operator Manual

Welcome to the Micrographics Capture Software. This tool is designed to provide high-speed, reliable digitization of microfilm and microfiche using a physical Canon EOS R8 camera.

## 1. Getting Started

### Prerequisites
- Windows 10/11 or macOS.
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
2. **Setup Project & Batch**
   - Enter a **Project Code** (e.g., `Archive_2026`).
   - Enter a **Batch Code** (e.g., `Box_01`).
   - Click **Start Batch**. This initializes the database and creates the output directory.
3. **Capture Images**
   Place your document beneath the camera.
   - Wait for the Live View overlay to display **READY TO CAPTURE** (which means Focus, Exposure, and Document detection have passed).
   - Press **SPACEBAR** or click the **CAPTURE** button to take a photo.
4. **Review Thumbnails**
   As you capture, thumbnails appear in the bottom strip. The background worker automatically processes the images (cropping, deskewing, OCR).
   - If a page needs to be retaken, select it and press **R** (Recapture).
5. **Export to PDF**
   Once your batch is complete, click **Export Batch**. The software will compile all successfully processed images and OCR text into a single, searchable PDF located in your `Pictures/MicroCapture` directory.

## 4. Keyboard Shortcuts

- **SPACE**: Capture a new frame.
- **R**: Recapture the last frame.
- **A**: Toggle Auto-Capture (automatically triggers capture when a document is placed and focused).

## 5. Troubleshooting

- **Camera Not Connecting**: Ensure Canon EOS Utility is closed. Only one application can control the camera at a time.
- **Export Failed**: This means the background worker is still processing your images, or an image failed Quality Control. Check the bottom status bar for details.
- **Blurry Images**: Ensure your physical setup has adequate lighting and the camera is properly auto-focusing before pressing Capture.
