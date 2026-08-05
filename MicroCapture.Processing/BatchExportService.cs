using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using OpenCvSharp;
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
        var normalizedFormat = format.Trim().ToUpperInvariant();
        if (normalizedFormat is not ("PDF" or "TIFF" or "JPG" or "PNG"))
            throw new ArgumentException($"Unsupported export format: {format}", nameof(format));
        // The capture worker updates a separate DbContext; use a no-tracking query so
        // export sees its latest statuses rather than stale navigation properties.
        var batch = await _dbContext.Batches
            .AsNoTracking()
            .Include(b => b.Captures)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch == null)
            throw new Exception($"Batch with ID {batchId} not found.");

        if (batch.Captures == null || batch.Captures.Count == 0)
            throw new Exception("Batch contains no capture jobs.");

        // QC is advisory until a dedicated QC-review screen exists. Do not silently
        // discard a successfully produced image solely due to an automatic heuristic.
        // A recapture landing while the prior attempt is still InProgress can otherwise
        // leave both attempts "Completed" for the same page — exclude anything marked
        // Superseded and keep only the latest attempt per page as a second safety net.
        var jobsToExport = batch.Captures
            .Where(j => j.ProcessingStatus == "Completed" && j.ExportStatus != "Superseded")
            .GroupBy(j => j.PageNumber)
            .Select(group => group.OrderByDescending(j => j.Timestamp).First())
            .OrderBy(j => j.PageNumber)
            .ToList();

        if (batch.Captures.Any(j => j.ProcessingStatus is "Pending" or "InProgress"))
            throw new InvalidOperationException("Images are still being processed.");

        if (jobsToExport.Count == 0)
            throw new Exception("No successfully processed images found to export in this batch.");

        string batchPrefix = SanitizeFileName(string.IsNullOrEmpty(batch.Name) ? batch.Id : batch.Name);
        
        Directory.CreateDirectory(outputDirectory);

        if (normalizedFormat == "PDF")
        {
            string pdfFileName = $"{batchPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            string pdfFilePath = Path.Combine(outputDirectory, pdfFileName);
            string temporaryPath = pdfFilePath + ".partial";

            using (var stream = new SKFileWStream(temporaryPath))
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
                        canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
                        DrawSearchText(canvas, f);
                        document.EndPage();
                    }
                    job.ExportStatus = "Completed";
                }
                document.Close();
            }
            File.Move(temporaryPath, pdfFilePath, true);
            batch.Status = "Exported";
            AttachOrUpdateBatch(batch);
            AttachOrUpdateJobs(jobsToExport);
            await _dbContext.SaveChangesAsync();
            return pdfFilePath;
        }
        else
        {
            // TIFF, JPG, PNG -> Export to subfolder
            string subDirName = $"{batchPrefix}_{normalizedFormat}_{DateTime.Now:yyyyMMdd_HHmmss}";
            string exportDir = Path.Combine(outputDirectory, subDirName);
            Directory.CreateDirectory(exportDir);

            int pageIndex = 1;
            foreach (var job in jobsToExport)
            {
                var files = GetProcessedFilesForJob(job);
                foreach (var f in files)
                {
                    string targetExt = normalizedFormat == "JPG" ? ".jpg" : (normalizedFormat == "TIFF" ? ".tif" : ".png");
                    string targetName = $"{batchPrefix}_Page_{pageIndex:D6}{targetExt}";
                    string targetPath = Path.Combine(exportDir, targetName);
                    
                    if (normalizedFormat is "JPG" or "PNG")
                    {
                        // Use SkiaSharp to convert
                        using var bitmap = SKBitmap.Decode(f);
                        if (bitmap != null)
                        {
                            using var img = SKImage.FromBitmap(bitmap);
                            using var data = img.Encode(normalizedFormat == "JPG" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 95);
                            using var outStream = File.OpenWrite(targetPath);
                            data.SaveTo(outStream);
                        }
                    }
                    else
                    {
                        // OpenCV writes a real TIFF rather than mislabelling a PNG.
                        using var image = Cv2.ImRead(f, ImreadModes.Unchanged);
                        if (image.Empty() || !Cv2.ImWrite(targetPath, image))
                            throw new IOException($"Could not write TIFF export: {targetPath}");
                    }
                    if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                        throw new IOException($"Export output was not created: {targetPath}");
                    pageIndex++;
                }
                job.ExportStatus = "Completed";
            }
            batch.Status = "Exported";
            AttachOrUpdateBatch(batch);
            AttachOrUpdateJobs(jobsToExport);
            await _dbContext.SaveChangesAsync();
            return exportDir;
        }
    }

    private void AttachOrUpdateBatch(Batch batch)
    {
        var tracked = _dbContext.ChangeTracker.Entries<Batch>().FirstOrDefault(entry => entry.Entity.Id == batch.Id)?.Entity;
        if (tracked != null)
        {
            tracked.Status = batch.Status;
        }
        else
        {
            _dbContext.Batches.Update(batch);
        }
    }

    private void AttachOrUpdateJobs(List<CaptureJob> jobs)
    {
        foreach (var job in jobs)
        {
            var tracked = _dbContext.ChangeTracker.Entries<CaptureJob>().FirstOrDefault(entry => entry.Entity.Id == job.Id)?.Entity;
            if (tracked != null)
            {
                tracked.ExportStatus = job.ExportStatus;
            }
            else
            {
                // Avoid attaching duplicate Batch navigation references when the job
                // was loaded with a related Batch entity from another query/context.
                job.Batch = null;
                _dbContext.CaptureJobs.Update(job);
            }
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
                .Where(IsExportableImage)
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

    private static void DrawSearchText(SKCanvas canvas, string imagePath)
    {
        var textPath = Path.ChangeExtension(imagePath, ".txt");
        if (!File.Exists(textPath)) return;
        var text = File.ReadAllText(textPath);
        if (string.IsNullOrWhiteSpace(text)) return;

        // Skia embeds this nearly transparent text in the PDF content stream. It
        // preserves search/copy capability without affecting the scanned image.
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 1), IsAntialias = false };
        using var font = new SKFont { Size = 1 };
        var y = 1f;
        foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
        {
            canvas.DrawText(line, 1, y, SKTextAlign.Left, font, paint);
            y += 1.2f;
        }
    }

    private static bool IsExportableImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff" or ".jpg" or ".jpeg" or ".png";

    private static string SanitizeFileName(string name) => MicroCapture.Core.FileNaming.Sanitize(name);
}
