using System;
using System.IO;

namespace MicroCapture.Processing;

/// <summary>Where a watermark preset's logo image is copied to and read from — never the
/// operator's original file path, so a saved preset (reusable across batches/projects, like
/// CameraCalibration) stays valid indefinitely even if the operator moves or deletes their
/// source file. Mirrors AppDbContext's own LocalApplicationData/MicroCapture convention (see
/// MainWindowViewModel's identical fallback for LensCalibration) since a preset's logo, like a
/// lens calibration, is not scoped to any one project's output directory.</summary>
public static class WatermarkAssetPaths
{
    public static string DirectoryFor() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroCapture", "WatermarkAssets");

    // presetId is used as the stored file's own base name (not the operator's original
    // filename) so re-saving a preset with a new logo cleanly overwrites/replaces the old asset
    // file rather than accumulating orphans, and so two presets can never collide even if both
    // started from a file called "logo.png".
    public static string FileFor(string presetId, string originalExtension) =>
        Path.Combine(DirectoryFor(), $"{presetId}{originalExtension}");
}
