using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using SkiaSharp;

namespace MicroCapture.Processing;

public class BatchExportService
{
    private readonly AppDbContext _dbContext;

    public BatchExportService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string> ExportBatchAsync(string batchId, string outputDirectory, string format)
    {
        var batch = await _dbContext.Batches
            .Include(b => b.Captures)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch == null)
            throw new Exception($"Batch with ID {batchId} not found.");

        if (batch.Captures == null || batch.Captures.Count == 0)
            throw new Exception("Batch contains no capture jobs.");

        var jobsToExport = batch.Captures
            .Where(j => j.ProcessingStatus == "Completed" && j.QcStatus != "FAIL")
            .OrderBy(j => j.PageNumber)
            .ToList();

        if (jobsToExport.Count == 0)
            throw new Exception("No successfully processed images found to export in this batch.");

        string batchPrefix = string.IsNullOrEmpty(batch.Name) ? batch.Id : batch.Name;
        
        Directory.CreateDirectory(outputDirectory);

        if (format.ToUpper() == "PDF")
        {
            string pdfFileName = $"{batchPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string pdfFilePath = Path.Combine(outputDirectory, pdfFileName);

            using (var stream = new SKFileWStream(pdfFilePath))
            using (var document = SKDocument.CreatePdf(stream))
            {
                foreach (var job in jobsToExport)
                {
                    var files = GetProcessedFilesForJob(job);
                    foreach (var f in files)
                    {
                        using var bitmap = SKBitmap.Decode(f);
                        if (bitmap == null) continue;
                        using var canvas = document.BeginPage(bitmap.Width, bitmap.Height);
                        canvas.DrawBitmap(bitmap, 0, 0);
                        document.EndPage();
                    }
                    job.ExportStatus = "Completed";
                }
                document.Close();
            }
            batch.Status = "Exported";
            await _dbContext.SaveChangesAsync();
            return pdfFilePath;
        }
        else
        {
            // TIFF, JPG, PNG -> Export to subfolder
            string subDirName = $"{batchPrefix}_{format.ToUpper()}_{DateTime.Now:yyyyMMdd_HHmmss}";
            string exportDir = Path.Combine(outputDirectory, subDirName);
            Directory.CreateDirectory(exportDir);

            int pageIndex = 1;
            foreach (var job in jobsToExport)
            {
                var files = GetProcessedFilesForJob(job);
                foreach (var f in files)
                {
                    string targetExt = format.ToLower() == "jpg" ? ".jpg" : (format.ToLower() == "tiff" ? ".tif" : ".png");
                    string targetName = $"{batchPrefix}_Page_{pageIndex:D6}{targetExt}";
                    string targetPath = Path.Combine(exportDir, targetName);
                    
                    if (format.ToUpper() == "JPG" || format.ToUpper() == "PNG")
                    {
                        // Use SkiaSharp to convert
                        using var bitmap = SKBitmap.Decode(f);
                        if (bitmap != null)
                        {
                            using var img = SKImage.FromBitmap(bitmap);
                            using var data = img.Encode(format.ToUpper() == "JPG" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 95);
                            using var outStream = File.OpenWrite(targetPath);
                            data.SaveTo(outStream);
                        }
                    }
                    else
                    {
                        // TIFF - we can just copy if the processor outputted a tif, else convert
                        if (Path.GetExtension(f).ToLower() == ".tif" || Path.GetExtension(f).ToLower() == ".tiff")
                        {
                            File.Copy(f, targetPath, true);
                        }
                        else
                        {
                            // Convert to png temporarily, skia can't natively encode TIFF easily without extension
                            File.Copy(f, targetPath + ".png", true); // Fallback
                        }
                    }
                    pageIndex++;
                }
                job.ExportStatus = "Completed";
            }
            batch.Status = "Exported";
            await _dbContext.SaveChangesAsync();
            return exportDir;
        }
    }

    private List<string> GetProcessedFilesForJob(CaptureJob job)
    {
        var dir = Path.GetDirectoryName(job.OriginalFilePath) ?? ".";
        var processedDir = Path.Combine(dir, "Processed");
        var fileName = Path.GetFileNameWithoutExtension(job.OriginalFilePath);

        var list = new List<string>();
        if (Directory.Exists(processedDir))
        {
            // The ImageProcessor creates files like {fileName}_processed.tif or {fileName}_1_left.tif
            var files = Directory.GetFiles(processedDir, $"{fileName}*.*")
                .OrderBy(f => f) // alphabetical order ensures _1_left before _2_right
                .ToList();
            if (files.Count > 0)
                list.AddRange(files);
        }

        if (list.Count == 0 && File.Exists(job.OriginalFilePath))
        {
            list.Add(job.OriginalFilePath);
        }

        return list;
    }
}
