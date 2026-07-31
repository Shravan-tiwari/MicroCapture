using System;
using System.Collections.Generic;

namespace MicroCapture.Core.Models;

public class Batch
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    
    public Project? Project { get; set; }
    public ICollection<CaptureJob> Captures { get; set; } = new List<CaptureJob>();
}
