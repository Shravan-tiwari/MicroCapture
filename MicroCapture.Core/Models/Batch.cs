using System;
using System.Collections.Generic;

namespace MicroCapture.Core.Models;

public class Batch
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BatchCode { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public string Status { get; set; } = "Active"; // Active, Completed, Exported
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    
    public bool SplitBookPages { get; set; } = false;

    // Fixed-frame capture: one or more operator-calibrated rectangles reused for every
    // capture in the batch, instead of per-shot auto-crop detection. FixedFrames holds
    // "X,Y,Width,Height" rects (in FixedFrameImageWidth/Height's pixel space) joined by
    // ';' — see ImageProcessor.ParseFixedFrames/FormatFixedFrames.
    public bool UseFixedFrames { get; set; } = false;
    public string? FixedFrames { get; set; }
    public int FixedFrameImageWidth { get; set; }
    public int FixedFrameImageHeight { get; set; }

    public string PreferredExportFormat { get; set; } = "PDF";

    public Project? Project { get; set; }
    public ICollection<CaptureJob> Captures { get; set; } = new List<CaptureJob>();
}
