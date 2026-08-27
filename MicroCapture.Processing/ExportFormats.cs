using System;
using System.Collections.Generic;
using System.Linq;

namespace MicroCapture.Processing;

/// <summary>A step of an export, reported so the operator can see what the app is doing.
///
/// <para>An export of a few hundred full-resolution pages takes minutes of solid CPU work.
/// Without this the window simply stopped responding, which is indistinguishable from a crash —
/// operators reasonably concluded the app had hung and killed it mid-write.</para></summary>
/// <param name="Phase">What is happening now, in the operator's terms ("Running OCR",
/// "Writing PDF"), not the method's.</param>
/// <param name="Current">Items finished so far, or 0 when the total isn't known yet.</param>
/// <param name="Total">Items in this phase, or 0 for work that can't be counted.</param>
public sealed record ExportProgress(string Phase, int Current, int Total)
{
    /// <summary>Work whose length can't be known in advance, so the UI shows motion rather than
    /// a misleading percentage.</summary>
    public bool IsIndeterminate => Total <= 0;

    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Current / Total, 0, 1);

    public override string ToString() =>
        Total <= 0 ? Phase : $"{Phase} — {Current} of {Total}";
}

/// <summary>How a chosen export format is actually produced.</summary>
public enum ExportKind
{
    /// <summary>One image file per page, written into an export subfolder.</summary>
    ImagePerPage,
    /// <summary>Every page in one PDF.</summary>
    Pdf,
    /// <summary>Every page in one multi-page TIFF.</summary>
    MultipageTiff,
    /// <summary>The batch's OCR text, with no images at all.</summary>
    TextOnly
}

/// <summary>One selectable export format, and what producing it involves.
///
/// <para>The operator-facing names come from the requirements and don't map one-to-one onto file
/// types: "PDF-Multipage" and "Searchable PDF" are both PDFs differing only in whether an OCR
/// text layer is embedded, and "TIFF" vs "TIFF LZW" are the same file type at different
/// compression. Keeping that mapping in one place stops each caller inventing its own
/// interpretation of the name.</para></summary>
public sealed record ExportFormat(
    string Name,
    ExportKind Kind,
    string Extension,
    bool EmbedsText = false,
    bool RequiresPdfA = false,
    string? Compression = null)
{
    /// <summary>Every format offered at export, in the order the requirements list them.</summary>
    public static readonly IReadOnlyList<ExportFormat> All = new[]
    {
        new ExportFormat("PDF", ExportKind.Pdf, ".pdf"),
        new ExportFormat("PDF-Multipage", ExportKind.Pdf, ".pdf"),
        new ExportFormat("Searchable PDF", ExportKind.Pdf, ".pdf", EmbedsText: true),
        new ExportFormat("PDF/A", ExportKind.Pdf, ".pdf", EmbedsText: true, RequiresPdfA: true),
        new ExportFormat("TIFF", ExportKind.ImagePerPage, ".tif", Compression: "LZW"),
        new ExportFormat("TIFF LZW", ExportKind.ImagePerPage, ".tif", Compression: "LZW"),
        new ExportFormat("TIFF Uncompressed", ExportKind.ImagePerPage, ".tif", Compression: "None"),
        new ExportFormat("TIFF-Multipage", ExportKind.MultipageTiff, ".tif", Compression: "LZW"),
        new ExportFormat("JPEG", ExportKind.ImagePerPage, ".jpg"),
        new ExportFormat("JPG", ExportKind.ImagePerPage, ".jpg"),
        new ExportFormat("PNG", ExportKind.ImagePerPage, ".png"),
        new ExportFormat("JPEG 2000", ExportKind.ImagePerPage, ".jp2"),
        new ExportFormat("BMP", ExportKind.ImagePerPage, ".bmp"),
        new ExportFormat("OCR Text", ExportKind.TextOnly, ".txt")
    };

    /// <summary>Names offered in the UI. "JPG" is accepted for backwards compatibility with
    /// batches and callers that already store it, but isn't offered alongside "JPEG".</summary>
    public static IReadOnlyList<string> SelectableNames { get; } =
        All.Where(f => f.Name != "JPG").Select(f => f.Name).ToList();

    /// <summary>Resolves an operator-facing or stored format name, case- and spacing-insensitively.
    /// Returns null for anything unrecognised so the caller can reject it explicitly rather than
    /// silently exporting something other than what was asked for.</summary>
    public static ExportFormat? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Trim();
        return All.FirstOrDefault(f => string.Equals(f.Name, normalized, StringComparison.OrdinalIgnoreCase))
            // Tolerate punctuation differences between how a name is written and how it was
            // stored ("PDF-A" vs "PDF/A", "TIFF Multipage" vs "TIFF-Multipage").
            ?? All.FirstOrDefault(f => string.Equals(Canonicalize(f.Name), Canonicalize(normalized), StringComparison.OrdinalIgnoreCase));
    }

    private static string Canonicalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray());
}
