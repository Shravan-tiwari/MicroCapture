using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MicroCapture.UI.Converters;

/// <summary>The selection control's glyph: a filled check when the page is selected, an empty box
/// when it isn't. A glyph rather than a coloured dot because the dot only conveyed state to
/// someone who already knew what it meant — a tick reads as "selected" without being taught.</summary>
public class BoolToSelectGlyphConverter : IValueConverter
{
    public static readonly BoolToSelectGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "✓" : "☐";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
