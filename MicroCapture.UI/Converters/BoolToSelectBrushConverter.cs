using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MicroCapture.UI.Converters;

/// <summary>Fills the thumbnail selection-toggle button: accent color when selected, transparent
/// (so only its hairline border shows) otherwise — makes the always-visible toggle read as an
/// unchecked checkbox until the operator selects it.</summary>
public class BoolToSelectBrushConverter : IValueConverter
{
    public static readonly BoolToSelectBrushConverter Instance = new();

    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#5e6ad2"));
    private static readonly IBrush Unselected = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Selected : Unselected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
