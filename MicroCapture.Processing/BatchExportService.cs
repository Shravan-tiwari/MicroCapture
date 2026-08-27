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

    /// <param name="orderedJobIds">Overrides the default export order/page-set — when provided,
    /// exports exactly these <see cref="CaptureJob"/> IDs, in this order, instead of
    /// <see cref="CaptureQueueService.GetCompletedJobsForBatch"/>'s default (all non-superseded
    /// completed jobs, sorted by <see cref="CaptureJob.PageNumber"/>). This only affects THIS
    /// export's page order/inclusion — it never writes back to PageNumber or any other
    /// persisted field, so it can't collide with PageNumber's other roles (recapture/supersede
    /// identity — see CaptureJob.PageNumber's own remarks). Used by the Finalize Batch dialog's
    /// reorder/delete UI, which is deliberately export-scoped only.</param>
    /// <param name="customFileName">Overrides the auto-generated
    /// <c>{batchPrefix}_{timestamp}.pdf</c> / export-subfolder name with this exact name
    /// (extension appended/subfolder-suffix still applied as normal) when provided.</param>
    /// <param name="customOutputDirectory">Overrides <paramref name="outputDirectory"/> when
    /// provided — lets the Finalize dialog's destination picker route the export somewhere other
    /// than the batch's default project folder.</param>
    public async Task<string> ExportBatchAsync(string batchId, string outputDirectory, string format,
        IReadOnlyList<string>? orderedJobIds = null, string? customFileName = null, string? customOutputDirectory = null)
    {
        var exportFormat = ExportFormat.Resolve(format)
            ?? throw new ArgumentException($"Unsupported export format: {format}", nameof(format));
        var normalizedFormat = exportFormat.Name.ToUpperInvariant();
        // The capture worker updates a separate DbContext; use a no-tracking query so
        // export sees its latest statuses rather than stale navigation properties.
        var batch = await _dbContext.Batches
            .AsNoTracking()
            .Include(b => b.Captures)
            .Include(b => b.WatermarkPreset)
            .FirstOrDefaultAsync(b => b.Id == batchId);

        if (batch == null)
            throw new Exception($"Batch with ID {batchId} not found.");

        if (batch.Captures == null || batch.Captures.Count == 0)
            throw new Exception("Batch contains no capture jobs.");

        if (batch.Captures.Any(j => j.ProcessingStatus is "Pending" or "InProgress"))
            throw new InvalidOperationException("Images are still being processed.");

        List<CaptureJob> jobsToExport;
        if (orderedJobIds != null)
        {
            var byId = batch.Captures.ToDictionary(j => j.Id);
            jobsToExport = orderedJobIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        else
        {
            // QC is advisory until a dedicated QC-review screen exists. Do not silently
            // discard a successfully produced image solely due to an automatic heuristic.
            // A recapture landing while the prior attempt is still InProgress can otherwise
            // leave both attempts "Completed" for the same page — exclude anything marked
            // Superseded and keep only the latest attempt per page as a second safety net.
            jobsToExport = CaptureQueueService.GetCompletedJobsForBatch(batch.Captures);
        }

        if (jobsToExport.Count == 0)
            throw new Exception("No successfully processed images found to export in this batch.");

        string batchPrefix = SanitizeFileName(string.IsNullOrEmpty(batch.Name) ? batch.Id : batch.Name);
        outputDirectory = customOutputDirectory ?? outputDirectory;

        Directory.CreateDirectory(outputDirectory);

        if (exportFormat.Kind == ExportKind.TextOnly)
        {
            return await ExportOcrTextAsync(batch, jobsToExport, outputDirectory, batchPrefix, customFileName);
        }

        if (exportFormat.Kind == ExportKind.MultipageTiff)
        {
            return await ExportMultipageTiffAsync(batch, jobsToExport, outputDirectory, batchPrefix, customFileName);
        }

        if (exportFormat.Kind == ExportKind.Pdf)
        {
            string pdfFileName = string.IsNullOrWhiteSpace(customFileName)
                ? $"{batchPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
                : SanitizeFileName(customFileName) + ".pdf";
            string pdfFilePath = Path.Combine(outputDirectory, pdfFileName);
            string temporaryPath = pdfFilePath + ".partial";

            var pdfMetadata = new SKDocumentPdfMetadata
            {
                Title = batch.Name ?? batchPrefix,
                Author = Environment.UserName,
                Creator = "MicroCapture",
                Producer = "MicroCapture",
                Creation = DateTime.Now,
                Modified = DateTime.Now,
                // MUST be set explicitly. SKDocumentPdfMetadata.Default carries 101 (>100 means
                // store images losslessly), but a freshly constructed instance defaults this to
                // 0 — maximum JPEG compression. Passing a metadata object at all therefore
                // silently swapped lossless pages for heavily degraded ones: a 7MB source page
                // came out around 295KB. Archival scans must not be re-compressed on the way
                // into the PDF.
                EncodingQuality = 101,
                // PDF/A requires the text layer to be genuinely searchable and the document
                // tagged; PdfA also makes Skia embed the fonts it draws with, which is the part
                // an image-only scan plus an invisible OCR layer would otherwise fail on.
                PdfA = exportFormat.RequiresPdfA
            };

            using (var stream = new SKFileWStream(temporaryPath))
            using (var document = SKDocument.CreatePdf(stream, pdfMetadata))
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
                        // Watermark is drawn last so it stays the topmost layer on the page —
                        // PDF only, per the feature's current scope (see WatermarkRenderer).
                        if (batch.WatermarkEnabled && batch.WatermarkPreset != null)
                            WatermarkRenderer.Draw(canvas, bitmap, batch.WatermarkPreset);
                        document.EndPage();
                    }
                    job.ExportStatus = "Completed";
                }
                document.Close();
            }
            File.Move(temporaryPath, pdfFilePath, true);
            batch.Status = "Exported";
            DeleteOriginals(jobsToExport);
            DeleteOcrSidecars(jobsToExport);
            AttachOrUpdateBatch(batch);
            AttachOrUpdateJobs(jobsToExport);
            await _dbContext.SaveChangesAsync();
            return pdfFilePath;
        }
        else
        {
            // TIFF, JPG, PNG -> Export to subfolder
            string subDirName = string.IsNullOrWhiteSpace(customFileName)
                ? $"{batchPrefix}_{normalizedFormat}_{DateTime.Now:yyyyMMdd_HHmmss}"
                : SanitizeFileName(customFileName);
            string exportDir = Path.Combine(outputDirectory, subDirName);
            Directory.CreateDirectory(exportDir);

            int pageIndex = 1;
            foreach (var job in jobsToExport)
            {
                var files = GetProcessedFilesForJob(job);
                foreach (var f in files)
                {
                    string targetExt = exportFormat.Extension;
                    string targetName = $"{batchPrefix}_Page_{pageIndex:D6}{targetExt}";
                    string targetPath = Path.Combine(exportDir, targetName);

                    var isJpeg = targetExt == ".jpg";
                    var isPng = targetExt == ".png";

                    // Composited once per page and reused by whichever writer runs below. Null
                    // when the batch has no watermark, which keeps the untouched fast paths
                    // (notably the byte-for-byte TIFF copy) available.
                    using var watermarked = RenderWatermarked(f, batch);

                    if (isJpeg || isPng)
                    {
                        using var bitmap = watermarked ?? DecodeImage(f);
                        if (bitmap != null)
                        {
                            using var img = SKImage.FromBitmap(bitmap);
                            using var data = img.Encode(isJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 95);
                            // SKImage.Encode never writes a DPI/density field for either format —
                            // patch it in afterward (same fix WriteJpeg already applies to the
                            // direct-capture JPG path) so exported JPG/PNG pages don't silently
                            // read back as 96 DPI regardless of what was actually captured.
                            var bytes = data.ToArray();
                            if (isJpeg)
                                ImageProcessor.StampJfifDensity(bytes, job.Dpi);
                            else
                                ImageProcessor.StampPngDensity(ref bytes, job.Dpi);
                            File.WriteAllBytes(targetPath, bytes);
                        }
                    }
                    else if (targetExt is ".jp2" or ".bmp")
                    {
                        if (watermarked != null)
                        {
                            if (!WriteWatermarkedWithOpenCv(watermarked, targetPath))
                                throw new IOException($"Could not write watermarked {exportFormat.Name} export: {targetPath}");
                            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                                throw new IOException($"Export output was not created: {targetPath}");
                            pageIndex++;
                            continue;
                        }

                        // OpenCV owns both of these. Neither carries a DPI field the way TIFF and
                        // JPEG do — BMP's header pixels-per-metre is widely ignored, and JPEG 2000
                        // resolution boxes aren't written by OpenCV — so unlike the paths above
                        // there's nothing to stamp; the DPI lives only in the batch's records.
                        using var image = Cv2.ImRead(f, ImreadModes.Unchanged);
                        if (image.Empty())
                            throw new IOException($"Could not read page for export: {f}");
                        if (!Cv2.ImWrite(targetPath, image))
                            throw new IOException(
                                $"Could not write {exportFormat.Name} export: {targetPath}. " +
                                "This build of OpenCV may not include that encoder.");
                    }
                    else if (watermarked != null)
                    {
                        // A watermarked TIFF has to be re-encoded rather than copied — the point
                        // of the copy below is preserving the source bytes, which no longer
                        // represent what should be exported once a mark is burned in.
                        if (!WriteWatermarkedWithOpenCv(watermarked, targetPath))
                            throw new IOException($"Could not write watermarked TIFF export: {targetPath}");
                    }
                    else if (Path.GetExtension(f).ToLowerInvariant() is ".tif" or ".tiff"
                             && exportFormat.Compression != "None")
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
            DeleteOriginals(jobsToExport);
            DeleteOcrSidecars(jobsToExport);
            AttachOrUpdateBatch(batch);
            AttachOrUpdateJobs(jobsToExport);
            await _dbContext.SaveChangesAsync();
            return exportDir;
        }
    }

    /// <summary>Burns the batch's watermark into a decoded page, returning the composited image.
    ///
    /// <para>The watermark used to reach PDF exports only, because PDF was the first format it
    /// was built for — so a batch exported as TIFF or JPEG came out unmarked with no indication
    /// anything had been skipped. Since a watermark is usually there for provenance or ownership,
    /// silently dropping it from some formats is worse than not offering it at all.</para>
    ///
    /// <para>Returns null when there is nothing to draw, so callers can use the original file
    /// untouched (and keep byte-for-byte TIFF copying, which is what preserves bitonal encoding)
    /// rather than needlessly decoding and re-encoding every page.</para></summary>
    private static SKBitmap? RenderWatermarked(string sourcePath, Batch batch)
    {
        if (!batch.WatermarkEnabled || batch.WatermarkPreset == null) return null;

        var bytes = ImageDecodeHelper.GetDisplayBytes(sourcePath);
        if (bytes == null) return null;
        using var source = SKBitmap.Decode(bytes);
        if (source == null) return null;

        var surface = new SKBitmap(source.Width, source.Height);
        using (var canvas = new SKCanvas(surface))
        {
            canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default);
            WatermarkRenderer.Draw(canvas, source, batch.WatermarkPreset);
            canvas.Flush();
        }
        return surface;
    }

    /// <summary>Writes a watermarked bitmap out through OpenCV, for the formats OpenCV owns.
    ///
    /// <para>Goes via a PNG round-trip rather than copying SKBitmap's raw buffer into a Mat.
    /// Skia's in-memory channel order is platform-dependent (BGRA on some targets, RGBA on
    /// others), and assuming one produced silently colour-swapped output — a red watermark came
    /// out blue, and the mark looked absent to anything checking for red. PNG is lossless, so the
    /// round-trip costs a little time at export and nothing in fidelity.</para></summary>
    private static bool WriteWatermarkedWithOpenCv(SKBitmap bitmap, string targetPath)
    {
        using var mat = WatermarkedToMat(bitmap);
        return !mat.Empty() && Cv2.ImWrite(targetPath, mat);
    }

    /// <summary>Writes every page of the batch into one multi-page TIFF.
    ///
    /// <para>Uses LibTiff directly rather than OpenCV, which can only write a single image per
    /// file. Each page is appended as its own IFD/directory, carrying its own dimensions and DPI,
    /// so pages of differing size stay correct — which matters here because a split spread
    /// produces halves that need not match the pages around them.</para>
    ///
    /// <para>Binarized pages keep 1-bit CCITT Group 4 encoding rather than being re-inflated to
    /// 8-bit grey, preserving the size win that was the point of binarizing.</para></summary>
    private async Task<string> ExportMultipageTiffAsync(Batch batch, List<CaptureJob> jobsToExport,
        string outputDirectory, string batchPrefix, string? customFileName)
    {
        var fileName = string.IsNullOrWhiteSpace(customFileName)
            ? $"{batchPrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.tif"
            : SanitizeFileName(customFileName) + ".tif";
        var filePath = Path.Combine(outputDirectory, fileName);
        var temporaryPath = filePath + ".partial";

        Directory.CreateDirectory(outputDirectory);

        var pages = jobsToExport
            .SelectMany(job => GetProcessedFilesForJob(job).Select(file => (Job: job, File: file)))
            .ToList();
        if (pages.Count == 0) throw new Exception("No processed images found to export in this batch.");

        using (var output = BitMiracle.LibTiff.Classic.Tiff.Open(temporaryPath, "w"))
        {
            if (output == null) throw new IOException($"Could not create multi-page TIFF: {temporaryPath}");

            for (var i = 0; i < pages.Count; i++)
            {
                var (job, file) = pages[i];
                AppendTiffPage(output, file, job.Dpi, i, pages.Count, batch);
            }
        }

        File.Move(temporaryPath, filePath, true);

        foreach (var job in jobsToExport) job.ExportStatus = "Completed";
        batch.Status = "Exported";
        DeleteOriginals(jobsToExport);
        DeleteOcrSidecars(jobsToExport);
        AttachOrUpdateBatch(batch);
        AttachOrUpdateJobs(jobsToExport);
        await _dbContext.SaveChangesAsync();
        return filePath;
    }

    /// <summary>Appends one page to an open multi-page TIFF.</summary>
    private static void AppendTiffPage(BitMiracle.LibTiff.Classic.Tiff output, string sourcePath, int dpi, int pageIndex, int pageCount, Batch batch)
    {
        // A watermarked page is composited to RGB first, so it takes the colour path below —
        // a mark burned into a bitonal page could not survive 1-bit encoding anyway.
        using var watermarked = RenderWatermarked(sourcePath, batch);
        using var composited = watermarked != null ? WatermarkedToMat(watermarked) : null;
        using var read = watermarked == null ? Cv2.ImRead(sourcePath, ImreadModes.Unchanged) : new Mat();
        var image = composited ?? read;
        if (image.Empty()) throw new IOException($"Could not read page for export: {sourcePath}");

        // A genuinely bitonal source stays bitonal. Cv2.ImRead returns it as 8-bit with only 0
        // and 255 present, so detect that rather than trusting the file extension.
        var isBitonal = image.Channels() == 1 && IsBlackAndWhiteOnly(image);

        using var rgb = new Mat();
        if (!isBitonal && image.Channels() == 1) Cv2.CvtColor(image, rgb, ColorConversionCodes.GRAY2RGB);
        else if (!isBitonal && image.Channels() == 4) Cv2.CvtColor(image, rgb, ColorConversionCodes.BGRA2RGB);
        else if (!isBitonal) Cv2.CvtColor(image, rgb, ColorConversionCodes.BGR2RGB);

        var source = isBitonal ? image : rgb;
        var width = source.Cols;
        var height = source.Rows;

        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH, width);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH, height);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL, isBitonal ? 1 : 3);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.BITSPERSAMPLE, isBitonal ? 1 : 8);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.ORIENTATION, BitMiracle.LibTiff.Classic.Orientation.TOPLEFT);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.PLANARCONFIG, BitMiracle.LibTiff.Classic.PlanarConfig.CONTIG);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.PHOTOMETRIC, isBitonal
            ? BitMiracle.LibTiff.Classic.Photometric.MINISBLACK
            : BitMiracle.LibTiff.Classic.Photometric.RGB);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.COMPRESSION, isBitonal
            ? BitMiracle.LibTiff.Classic.Compression.CCITTFAX4
            : BitMiracle.LibTiff.Classic.Compression.LZW);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.XRESOLUTION, (double)dpi);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.YRESOLUTION, (double)dpi);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.RESOLUTIONUNIT, BitMiracle.LibTiff.Classic.ResUnit.INCH);
        // PAGENUMBER is what makes this a page sequence rather than an arbitrary pile of images,
        // and is what viewers read to show "page 3 of 12".
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.SUBFILETYPE, BitMiracle.LibTiff.Classic.FileType.PAGE);
        output.SetField(BitMiracle.LibTiff.Classic.TiffTag.PAGENUMBER, pageIndex, pageCount);

        if (isBitonal)
        {
            source.GetArray(out byte[] grey);
            var stride = (width + 7) / 8;
            var packed = new byte[stride];
            for (var y = 0; y < height; y++)
            {
                Array.Clear(packed, 0, packed.Length);
                var rowStart = y * width;
                for (var x = 0; x < width; x++)
                    if (grey[rowStart + x] != 0) packed[x / 8] |= (byte)(0x80 >> (x % 8));
                output.WriteScanline(packed, y);
            }
        }
        else
        {
            // GetArray only unpacks single-channel Mats into byte[]; reshape the interleaved
            // 3-channel buffer to one channel of width*3 so the same bytes come out flat.
            using var flat = source.Reshape(1);
            flat.GetArray(out byte[] rgbBytes);
            var stride = width * 3;
            var row = new byte[stride];
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgbBytes, y * stride, row, 0, stride);
                output.WriteScanline(row, y);
            }
        }

        output.WriteDirectory();
    }

    /// <summary>Converts a composited watermark bitmap into an OpenCV BGR mat, via a lossless PNG
    /// round-trip so Skia's platform-dependent channel order can't swap the colours — see
    /// <see cref="WriteWatermarkedWithOpenCv"/>.</summary>
    private static Mat WatermarkedToMat(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data == null) return new Mat();
        return Cv2.ImDecode(data.ToArray(), ImreadModes.Color);
    }

    /// <summary>Whether a single-channel image contains only pure black and white, i.e. it is
    /// genuinely bitonal content that happens to be carried in an 8-bit buffer.</summary>
    private static bool IsBlackAndWhiteOnly(Mat grey)
    {
        grey.GetArray(out byte[] pixels);
        foreach (var pixel in pixels)
            if (pixel != 0 && pixel != 255) return false;
        return true;
    }

    /// <summary>Writes the batch's OCR text with no images — one text file per page plus a
    /// combined file, since either shape is the one somebody wants and producing both costs
    /// nothing.
    ///
    /// <para>Unlike the image exports this deliberately does NOT delete the originals: no archival
    /// image output has been produced, so treating a text dump as "the batch has been exported"
    /// and discarding the captures would destroy the actual work.</para></summary>
    private async Task<string> ExportOcrTextAsync(Batch batch, List<CaptureJob> jobsToExport,
        string outputDirectory, string batchPrefix, string? customFileName)
    {
        var subDirName = string.IsNullOrWhiteSpace(customFileName)
            ? $"{batchPrefix}_OCR_{DateTime.Now:yyyyMMdd_HHmmss}"
            : SanitizeFileName(customFileName);
        var exportDir = Path.Combine(outputDirectory, subDirName);
        Directory.CreateDirectory(exportDir);

        var combined = new System.Text.StringBuilder();
        var pageIndex = 1;
        var pagesWithText = 0;

        foreach (var job in jobsToExport)
        {
            foreach (var file in GetProcessedFilesForJob(job))
            {
                var sidecar = Path.ChangeExtension(file, ".txt");
                var text = File.Exists(sidecar) ? await File.ReadAllTextAsync(sidecar) : string.Empty;
                if (!string.IsNullOrWhiteSpace(text)) pagesWithText++;

                await File.WriteAllTextAsync(Path.Combine(exportDir, $"{batchPrefix}_Page_{pageIndex:D6}.txt"), text);
                combined.AppendLine($"--- Page {pageIndex} ---");
                combined.AppendLine(text);
                combined.AppendLine();
                pageIndex++;
            }
            job.ExportStatus = "Completed";
        }

        await File.WriteAllTextAsync(Path.Combine(exportDir, $"{batchPrefix}_AllPages.txt"), combined.ToString());

        if (pagesWithText == 0)
            throw new InvalidOperationException(
                "No OCR text was found for this batch. Run OCR first, then export again.");

        batch.Status = "Exported";
        AttachOrUpdateBatch(batch);
        AttachOrUpdateJobs(jobsToExport);
        await _dbContext.SaveChangesAsync();
        return exportDir;
    }

    /// <summary>Deletes each exported job's original capture file, now that the batch's final
    /// output has been produced and Crop Review no longer needs it. Originals are deliberately
    /// kept until this point (see BackgroundProcessingWorker, which used to delete them right
    /// after processing — moved here since Crop Review re-crops from OriginalFilePath and needs
    /// it to survive for as long as the operator might still revisit the batch before finalizing
    /// it). Each deletion is independently try/caught so one locked/missing file can't stop the
    /// rest from being cleaned up, and a failure here never fails the export itself — the export
    /// already succeeded by the time this runs.</summary>
    private static void DeleteOriginals(List<CaptureJob> jobs)
    {
        foreach (var job in jobs)
        {
            try
            {
                if (File.Exists(job.OriginalFilePath))
                    File.Delete(job.OriginalFilePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not delete original '{job.OriginalFilePath}' after export: {ex.Message}");
            }
        }
    }

    /// <summary>Deletes the .txt/.tsv OCR sidecars OcrProcessor writes next to each job's
    /// processed derivative(s) — working files only, meaningful up to the moment DrawSearchText
    /// reads them to embed searchable text in a PDF export. Left behind after that, they just
    /// clutter the batch's output folder alongside the actual image files. Same
    /// independently-try/caught, never-fails-the-export pattern as <see cref="DeleteOriginals"/>.</summary>
    private static void DeleteOcrSidecars(List<CaptureJob> jobs)
    {
        foreach (var job in jobs)
        {
            foreach (var processedFile in GetProcessedFilesForJob(job))
            {
                foreach (var ext in new[] { ".txt", ".tsv" })
                {
                    var sidecarPath = Path.ChangeExtension(processedFile, ext);
                    try
                    {
                        if (File.Exists(sidecarPath))
                            File.Delete(sidecarPath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Could not delete OCR sidecar '{sidecarPath}' after export: {ex.Message}");
                    }
                }
            }
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
    public static List<string> GetProcessedFilesForJob(CaptureJob job)
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
        // Skia embeds this nearly transparent text in the PDF content stream. It preserves
        // search/copy capability without affecting the visible scanned image.
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 1), IsAntialias = false };

        // Preferred: per-word boxes from OcrProcessor's tsv output, drawn directly on top of
        // each word at its real position/size — this is what makes the text actually
        // selectable/clickable like a normal scanned PDF, not just found by Ctrl+F. Without
        // this, the old approach (a single block of 1pt-font lines crammed at the page's
        // top-left corner) was searchable but had no usable per-word hit target to click or
        // drag-select — confirmed the actual reported bug ("ctrl+f works but clickable text
        // don't"). BeginPage(bitmap.Width, bitmap.Height) means this canvas's coordinate space
        // is 1 unit = 1 pixel of the same decoded image OCR ran against, so tsv pixel
        // coordinates need no scaling.
        var tsvPath = Path.ChangeExtension(imagePath, ".tsv");
        var words = OcrProcessor.ReadWordBoxes(tsvPath);
        if (words.Count > 0)
        {
            foreach (var word in words)
            {
                if (word.Width <= 0 || word.Height <= 0) continue;
                using var font = new SKFont { Size = word.Height };
                // Skia's DrawText baseline convention needs the y coordinate at the text's
                // baseline, not its top — approximate the baseline as the box's bottom edge,
                // close enough at invisible-text sizes where exact typographic metrics don't
                // matter, only the click target's position/size.
                canvas.DrawText(word.Text, word.Left, word.Top + word.Height, SKTextAlign.Left, font, paint);
            }
            return;
        }

        // Fallback for a page with no tsv (OCR ran with an older build, or the tsv failed to
        // parse/persist) — old single-blob behavior, still searchable even if not clickable.
        var textPath = Path.ChangeExtension(imagePath, ".txt");
        if (!File.Exists(textPath)) return;
        var text = File.ReadAllText(textPath);
        if (string.IsNullOrWhiteSpace(text)) return;

        using var fallbackFont = new SKFont { Size = 1 };
        var y = 1f;
        foreach (var line in text.Replace("\r", string.Empty).Split('\n'))
        {
            canvas.DrawText(line, 1, y, SKTextAlign.Left, fallbackFont, paint);
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
