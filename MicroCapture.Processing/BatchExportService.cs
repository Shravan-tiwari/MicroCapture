using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
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
        var jobsToExport = CaptureQueueService.GetCompletedJobsForBatch(batch.Captures);

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
                        using var bitmap = DecodeImage(f);
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
                        using var bitmap = DecodeImage(f);
                        if (bitmap != null)
                        {
                            using var img = SKImage.FromBitmap(bitmap);
                            using var data = img.Encode(normalizedFormat == "JPG" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 95);
                            using var outStream = File.OpenWrite(targetPath);
                            data.SaveTo(outStream);
                        }
                    }
                    else if (Path.GetExtension(f).ToLowerInvariant() is ".tif" or ".tiff")
                    {
                        // The source is already a TIFF ImageProcessor wrote (with its own DPI/
                        // Author/Software tags, and — when binarized — genuine 1-bit/CCITT-G4
                        // encoding). Copy it byte-for-byte rather than decoding and
                        // re-encoding through OpenCV, which would silently re-inflate a
                        // binarized page back to an ordinary 8-bit grayscale TIFF and throw
                        // away the compression/size win that was the point of binarizing.
                        File.Copy(f, targetPath, overwrite: true);
                    }
                    else
                    {
                        // Non-TIFF processed source (e.g. the SkiaSharp fallback path's .jpg) —
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

    /// <summary>Deterministically resolves a job's processed output file(s) on disk from its
    /// original capture path — shared with <see cref="BatchOcrService"/> so both locate exactly
    /// the same files without duplicating the glob logic.
    ///
    /// Four tiers, each falling through to the next only if it finds nothing:
    /// 1. <see cref="CaptureJob.ProcessedFilePath"/> — the exact path(s) BackgroundProcessingWorker
    ///    recorded on success (';'-joined for multi-output jobs). The authoritative answer for
    ///    every job processed after this field was introduced; filtered to paths that still
    ///    exist, since a job's derivative can be deleted outside this flow (e.g. Crop Review
    ///    reprocessing cleanup) without the DB field being cleared.
    /// 2. A glob of the main capture folder (the current layout — processed derivatives now live
    ///    alongside the retained original, not a separate subfolder) by filename prefix, for rows
    ///    with no ProcessedFilePath yet (older schema, or a job that failed to persist it).
    /// 3. A glob of the OLD "Processed" subfolder by the same prefix — backward compatibility for
    ///    batches processed before this change, whose derivatives still live there and were never
    ///    moved.
    /// 4. <see cref="CaptureJob.OriginalFilePath"/> itself, if nothing else was found.</summary>
    internal static List<string> GetProcessedFilesForJob(CaptureJob job)
    {
        // Tier 1: the exact recorded path(s), if this job has them and they still exist.
        if (!string.IsNullOrWhiteSpace(job.ProcessedFilePath))
        {
            var recorded = job.ProcessedFilePath
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(File.Exists)
                .ToList();
            if (recorded.Count > 0)
                return recorded;
        }

        var dir = ProcessedFilePaths.OutputDirectoryFor(job.OriginalFilePath);

        // Tier 2: main folder glob (current layout) — e.g. {fileName}.tif, {fileName}_1_left.tif.
        // Uses the boundary-aware derivative matcher, not a raw "{fileName}*" glob: with
        // originals now retained alongside their derivatives, a raw prefix glob would also match
        // an unrelated job's recapture original ("{fileName}_R_{timestamp}.jpg") whose name
        // happens to start with this job's file name.
        var mainFolderFiles = ProcessedFilePaths.EnumerateDerivatives(dir, job.OriginalFilePath)
            .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(job.OriginalFilePath), StringComparison.OrdinalIgnoreCase))
            .Where(IsExportableImage)
            .OrderBy(f => f) // alphabetical order ensures _1_left before _2_right
            .ToList();
        if (mainFolderFiles.Count > 0)
            return mainFolderFiles;

        // Tier 3: old "Processed" subfolder glob — backward compatibility for batches processed
        // before derivatives moved into the main capture folder. Originals were never written
        // into this legacy subfolder, so a plain prefix glob here doesn't share Tier 2's
        // recapture-collision risk, but use the same matcher for consistency.
        var legacyProcessedDir = Path.Combine(dir, "Processed");
        if (Directory.Exists(legacyProcessedDir))
        {
            var legacyFiles = ProcessedFilePaths.EnumerateDerivatives(legacyProcessedDir, job.OriginalFilePath)
                .Where(IsExportableImage)
                .OrderBy(f => f)
                .ToList();
            if (legacyFiles.Count > 0)
                return legacyFiles;
        }

        // Tier 4: last resort, the original capture itself.
        var list = new List<string>();
        if (File.Exists(job.OriginalFilePath))
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

    /// <summary>Decodes an image file to an <see cref="SKBitmap"/> for PDF/JPG/PNG export.
    /// SkiaSharp's own decoder cannot read the TIFF files <see cref="ImageProcessor"/> writes,
    /// so this bridges through <see cref="ImageDecodeHelper"/> (shared with the UI's thumbnail
    /// display) rather than reimplementing the OpenCV round-trip here.</summary>
    private static SKBitmap? DecodeImage(string path)
    {
        var bytes = ImageDecodeHelper.GetDisplayBytes(path);
        return bytes == null ? null : SKBitmap.Decode(bytes);
    }

    private static string SanitizeFileName(string name) => MicroCapture.Core.FileNaming.Sanitize(name);
}
