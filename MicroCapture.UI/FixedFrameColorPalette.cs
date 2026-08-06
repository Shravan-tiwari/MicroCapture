using Avalonia.Media;

namespace MicroCapture.UI;

/// <summary>Shared color-by-index palette for fixed-frame rectangles, used both when drawing
/// frames in <see cref="Views.FrameCalibrationWindow"/> and when coloring each frame's output
/// thumbnail border in the main capture strip — so a frame's color means the same thing in both
/// places. Color is derived from index, not persisted anywhere.</summary>
public static class FixedFrameColorPalette
{
    private static readonly Color[] Colors =
    {
        Color.Parse("#5e6ad2"), Color.Parse("#828fff"), Color.Parse("#e5484d"), Color.Parse("#f5a623"),
        Color.Parse("#4cb782"), Color.Parse("#00b8d9"), Color.Parse("#c93ea9"), Color.Parse("#8b5cf6"),
    };

    public static Color GetColor(int index) => Colors[index % Colors.Length];
}
