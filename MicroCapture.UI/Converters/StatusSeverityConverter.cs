using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MicroCapture.UI.Converters;

/// <summary>Maps the small, known set of status strings this app actually produces
/// (<c>DocumentStatus</c>, <c>CaptureReadiness</c>, <c>ThumbnailItem.Status</c>,
/// <c>BoundaryHintText</c>) to one of four semantic brushes. Deliberately pattern-matches the
/// exact strings the ViewModels assign today rather than adding a new severity enum — this is
/// a styling-layer change, not a behavior change, so it reads the same text the UI already
/// displays instead of asking the ViewModels to expose anything new.</summary>
public class StatusSeverityConverter : IValueConverter
{
    public static readonly StatusSeverityConverter Instance = new();

    // Keep in sync with the semantic tokens in Styles/Theme.axaml.
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#27a644"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#d99a3d"));
    private static readonly IBrush Fail = new SolidColorBrush(Color.Parse("#e5484d"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#8a8f98"));
    // A capture actively being processed — distinct from Neutral (which otherwise also covers
    // "no status yet"/unrecognized strings) so "in progress" reads as a visibly different state
    // from "nothing has happened" or "processed", not the same grey dot as both.
    private static readonly IBrush InProgress = new SolidColorBrush(Color.Parse("#5e6ad2"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string;
        if (string.IsNullOrEmpty(status)) return Neutral;

        if (status.Contains("QC fail", StringComparison.OrdinalIgnoreCase) || status == "Processing failed")
            return Fail;

        if (status is "NOT READY" or "SET PROJECT & BATCH"
            || status.Contains("OCR failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("needs review", StringComparison.OrdinalIgnoreCase)
            || status.Contains("lower confidence", StringComparison.OrdinalIgnoreCase)
            || status.Contains("No boundary detected", StringComparison.OrdinalIgnoreCase))
            return Warning;

        if (status.StartsWith('✓') || status is "Processed" or "READY TO CAPTURE" or "AUTO CAPTURE ACTIVE")
            return Success;

        if (status is "Processing" or "Recapturing" or "Reprocessing…"
            || status.Contains("Processing", StringComparison.OrdinalIgnoreCase))
            return InProgress;

        return Neutral;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
