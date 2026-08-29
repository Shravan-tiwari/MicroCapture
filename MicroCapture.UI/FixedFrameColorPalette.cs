using Avalonia.Media;

namespace MicroCapture.UI;

/// <summary>Shared color-by-index palette for fixed-frame rectangles, used both when drawing
/// frames in <see cref="Controls.FixedFrameOverlayEditor"/> and when coloring each frame's output
/// thumbnail border in the main capture strip — so a frame's color means the same thing in both
/// places. Color is derived from index, not persisted anywhere.</summary>
public static class FixedFrameColorPalette
{
    // Ordered so ADJACENT indices are clearly different hues, which is the only ordering that
    // matters here — frames are numbered from 0 upward, so entries 0 and 1 are the pair an
    // operator sees most. Those used to be #5e6ad2 and #828fff: two shades of the same
    // indigo, indistinguishable on a live view, so the first two frames drawn looked identical.
    private static readonly Color[] Colors =
    {
        Color.Parse("#5e6ad2"), // indigo — matches the app's primary
        Color.Parse("#f5a623"), // amber
        Color.Parse("#4cb782"), // green
        Color.Parse("#e5484d"), // red
        Color.Parse("#00b8d9"), // cyan
        Color.Parse("#8b5cf6"), // violet
        Color.Parse("#9aa81f"), // olive
        Color.Parse("#c93ea9"), // magenta
    };

    public static Color GetColor(int index) => Colors[index % Colors.Length];
}
