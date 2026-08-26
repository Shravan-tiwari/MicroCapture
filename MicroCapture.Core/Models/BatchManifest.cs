using System;
using System.Collections.Generic;

namespace MicroCapture.Core.Models;

/// <summary>The authoritative, portable record of a batch — settings, page count and page order —
/// written as JSON into the batch folder as <see cref="BatchFolder.ManifestFileName"/>. See
/// <see cref="BatchFolder"/> for why this exists rather than the local database alone.
///
/// <para>The local SQLite database remains the runtime working store (the capture queue and
/// live processing status), but it is a cache: opening a batch on a machine that didn't create it
/// rebuilds those rows from this file, and where the two disagree this file wins.</para></summary>
public class BatchManifest
{
    /// <summary>Bumped when the shape changes incompatibly, so an older build can refuse a
    /// manifest it would otherwise misread rather than silently losing fields.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public const int CurrentSchemaVersion = 1;

    /// <summary>Carried across machines so a batch keeps one identity no matter where it's
    /// opened — the local database row is recreated against this id rather than a new one.</summary>
    public string BatchId { get; set; } = string.Empty;
    public string BatchCode { get; set; } = string.Empty;
    public string ProjectCode { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Active / Completed / Exported, mirroring <see cref="Batch.Status"/>.</summary>
    public string Status { get; set; } = "Active";

    /// <summary>Machine that created the batch. Informational — helps an operator work out where
    /// a batch came from; never used to restrict who can open it.</summary>
    public string? CreatedOnDevice { get; set; } = Environment.MachineName;

    public BatchManifestSettings Settings { get; set; } = new();

    /// <summary>Every page in the batch, in capture order. Written when a page is CAPTURED rather
    /// than when processing finishes, so a machine opening the batch immediately sees pages
    /// another machine has just shot — and so the page numbers already claimed are visible before
    /// assigning the next one.</summary>
    public List<BatchManifestPage> Pages { get; set; } = new();

    public int PageCount => Pages.Count;
}

/// <summary>Batch-level capture and processing settings. Chosen once when the batch is created
/// and fixed thereafter, so every page in a batch is produced the same way.</summary>
public class BatchManifestSettings
{
    public int Dpi { get; set; } = 150;

    /// <summary>Per-capture file format (TIFF/JPG/PNG...).</summary>
    public string CaptureFormat { get; set; } = "TIFF";

    /// <summary>Format the finished batch is exported to at Finalize.</summary>
    public string PreferredExportFormat { get; set; } = "PDF";

    public bool DewarpEnabled { get; set; }
    public bool SplitBookPages { get; set; }
    public bool BinarizeEnabled { get; set; }
    public bool BleedthroughEnabled { get; set; }

    /// <summary>Operator-drawn capture frames, in the same "X,Y,W,H" list form as
    /// <see cref="Batch.FixedFrames"/>, together with the resolution they were authored against
    /// (see ADR-005). Carried so a batch reopened elsewhere keeps its framing.</summary>
    public string? FixedFrames { get; set; }
    public int FixedFrameImageWidth { get; set; }
    public int FixedFrameImageHeight { get; set; }

    public bool WatermarkEnabled { get; set; }
    public string? WatermarkPresetId { get; set; }
}

/// <summary>One captured page. File references are RELATIVE to the batch folder — see
/// <see cref="BatchFolder"/> — so they still resolve after the batch moves between drive letters,
/// a UNC share, and a USB stick.</summary>
public class BatchManifestPage
{
    public int PageNumber { get; set; }

    /// <summary>Matches the local <see cref="CaptureJob.Id"/> so a rebuilt database keeps the same
    /// job identity, and a batch opened on two machines doesn't produce colliding job rows.</summary>
    public string JobId { get; set; } = string.Empty;

    public string? OriginalFile { get; set; }

    /// <summary>Processed outputs for this page, relative to the batch folder. A page can produce
    /// more than one file (a split spread), matching CaptureJob's ';'-joined convention.</summary>
    public List<string> ProcessedFiles { get; set; } = new();

    public string? ThumbnailFile { get; set; }

    /// <summary>Pending / InProgress / Completed / Failed / Superseded, mirroring
    /// <see cref="CaptureJob.ProcessingStatus"/>.</summary>
    public string ProcessingStatus { get; set; } = "Pending";

    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Machine that shot this page — the only way to attribute pages when a batch has
    /// been worked by more than one workstation.</summary>
    public string? CapturedOnDevice { get; set; } = Environment.MachineName;

    /// <summary>The non-destructive adjustment recipe for this page, carried so re-editing still
    /// works after the batch moves. Values match <see cref="CaptureJob"/>'s own fields.</summary>
    public BatchManifestAdjustments? Adjustments { get; set; }
}

/// <summary>Per-page manual adjustments. Kept as a recipe against the preserved original rather
/// than baked into the output, so they remain editable after capture — including on another
/// machine, provided the originals travelled with the batch.</summary>
public class BatchManifestAdjustments
{
    public int RotationDegrees { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }
    public double Sharpness { get; set; }
    public double WhiteBalance { get; set; }
}
