using System;
using System.Collections.Generic;

namespace MicroCapture.Core.Models;

public class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Customer { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    
    public ICollection<Batch> Batches { get; set; } = new List<Batch>();
}
