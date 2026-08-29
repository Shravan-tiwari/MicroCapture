using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MicroCapture.UI.Theming;

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

    // Shared, theme-reactive instances rather than fixed colours: a converter only runs when its
    // binding source changes, and switching day/night mode changes no status text, so returning a
    // literal colour here would leave every status stuck in the previous palette. SemanticBrushes
    // recolours these in place instead. See AppTheme.
    private static IBrush Success => SemanticBrushes.Success;
    private static IBrush Warning => SemanticBrushes.Warning;
    private static IBrush Fail => SemanticBrushes.Fail;
    private static IBrush Neutral => SemanticBrushes.Neutral;
    // A capture actively being processed — distinct from Neutral (which otherwise also covers
    // "no status yet"/unrecognized strings) so "in progress" reads as a visibly different state
    // from "nothing has happened" or "processed", not the same grey dot as both.
    private static IBrush InProgress => SemanticBrushes.InProgress;

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
