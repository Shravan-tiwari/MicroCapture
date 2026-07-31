using System;

namespace MicroCapture.Core.Models;

public class CaptureJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BatchId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string OriginalFilePath { get; set; } = string.Empty;
    
    // Processing States
    public string ProcessingStatus { get; set; } = "Pending"; // Pending, InProgress, Completed, Failed
    public string QcStatus { get; set; } = "Pending";
    public string OcrStatus { get; set; } = "Pending";
    public string ExportStatus { get; set; } = "Pending";
    
    public Batch? Batch { get; set; }
}
