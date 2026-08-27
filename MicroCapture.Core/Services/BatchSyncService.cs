using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;

namespace MicroCapture.Core.Services;

/// <summary>Reconciles a batch folder's manifest with the local database.
///
/// <para>The manifest is the source of truth; the local database is a per-machine working store
/// for the capture queue and processing status. Opening a batch on a machine that didn't create
/// it therefore has to rebuild those rows from the manifest — which in a shop where batches move
/// between workstations, USB drives and shares is the ordinary case, not a migration edge.</para>
///
/// <para>Where the two disagree, the manifest wins. It is the copy that travelled with the images
/// and the copy every machine sees; a local row can be stale simply because the batch was last
/// worked somewhere else.</para></summary>
public class BatchSyncService
{
    private readonly AppDbContext _dbContext;
    private readonly BatchManifestService _manifests;

    public BatchSyncService(AppDbContext dbContext, BatchManifestService manifests)
    {
        _dbContext = dbContext;
        _manifests = manifests;
    }

    /// <summary>Makes the local database match the manifest, creating the project, batch and
    /// capture rows if this machine has never seen them. Returns the batch, tracked and ready to
    /// load into the UI.</summary>
    public async Task<Batch> AdoptAsync(string batchFolder, BatchManifest manifest)
    {
        // Rows this context touched earlier in the session are frozen at that moment; a batch
        // being re-adopted must be read fresh or the manifest gets reconciled against a stale
        // snapshot. Same reasoning as OpenRecentBatchesAsync's own ChangeTracker.Clear().
        _dbContext.ChangeTracker.Clear();

        var project = await EnsureProjectAsync(manifest);
        var batch = await _dbContext.Batches
            .Include(b => b.Captures)
            .FirstOrDefaultAsync(b => b.Id == manifest.BatchId);

        if (batch == null)
        {
            // Keyed on the manifest's own batch id, not a fresh one, so a batch keeps a single
            // identity across every machine that opens it.
            batch = new Batch { Id = manifest.BatchId };
            _dbContext.Batches.Add(batch);
        }

        batch.ProjectId = project.Id;
        batch.BatchCode = manifest.BatchCode;
        batch.Name = manifest.BatchCode;
        batch.FolderPath = batchFolder;
        // Opening a batch makes it workable again. The previous expression was a tautology that
        // always kept the manifest's status, so reopening an exported batch left it labelled
        // "Exported" while the operator captured new pages into it.
        batch.Status = "Active";
        batch.StartTime = manifest.CreatedUtc;

        ApplySettings(batch, manifest.Settings);
        await SyncCapturesAsync(batchFolder, manifest, batch);

        await _dbContext.SaveChangesAsync();
        return batch;
    }

    private async Task<Project> EnsureProjectAsync(BatchManifest manifest)
    {
        var code = string.IsNullOrWhiteSpace(manifest.ProjectCode) ? "Default" : manifest.ProjectCode;
        var project = await _dbContext.Projects.FirstOrDefaultAsync(p => p.Name == code);
        if (project != null) return project;

        project = new Project
        {
            // Reuse the manifest's project id where it carries one, so two machines opening the
            // same batch agree on the project rather than each minting their own.
            Id = string.IsNullOrWhiteSpace(manifest.ProjectId) ? Guid.NewGuid().ToString() : manifest.ProjectId,
            Name = code,
            Customer = string.Empty,
            Description = "Restored from a batch folder",
            CreatedBy = Environment.UserName,
            // Only a fallback for legacy paths: a batch that has its own folder keeps everything
            // inside it, so nothing new should ever be written here.
            OutputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MicroCapture", code)
        };
        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();
        return project;
    }

    private static void ApplySettings(Batch batch, BatchManifestSettings settings)
    {
        batch.Dpi = settings.Dpi;
        batch.PreferredExportFormat = settings.PreferredExportFormat;
        batch.DewarpEnabled = settings.DewarpEnabled;
        batch.SplitBookPages = settings.SplitBookPages;
        batch.BinarizeEnabled = settings.BinarizeEnabled;
        batch.BleedthroughEnabled = settings.BleedthroughEnabled;
        batch.FixedFrames = settings.FixedFrames;
        batch.FixedFrameImageWidth = settings.FixedFrameImageWidth;
        batch.FixedFrameImageHeight = settings.FixedFrameImageHeight;
        batch.UseFixedFrames = !string.IsNullOrWhiteSpace(settings.FixedFrames);
        batch.WatermarkEnabled = settings.WatermarkEnabled;
        batch.WatermarkPresetId = settings.WatermarkPresetId;
    }

    private async Task SyncCapturesAsync(string batchFolder, BatchManifest manifest, Batch batch)
    {
        var existing = await _dbContext.CaptureJobs
            .Where(j => j.BatchId == batch.Id)
            .ToDictionaryAsync(j => j.Id);

        foreach (var page in manifest.Pages)
        {
            if (!existing.TryGetValue(page.JobId, out var job))
            {
                job = new CaptureJob { Id = page.JobId, BatchId = batch.Id };
                _dbContext.CaptureJobs.Add(job);
            }

            job.PageNumber = page.PageNumber;
            job.Timestamp = page.CapturedUtc;
            job.ProcessingStatus = page.ProcessingStatus;
            job.Dpi = manifest.Settings.Dpi;
            job.CaptureFormat = manifest.Settings.CaptureFormat;

            // Manifest paths are relative to the batch folder, so they resolve against wherever
            // it lives now — the whole reason a batch survives moving between drives and shares.
            // Only overwrite when the manifest actually carries a path. A manifest that lists a
            // page without file references describes where the files are NOT, and blanking a
            // working local path against it loses the only pointer this machine had to the image.
            var resolvedOriginal = BatchFolder.ToAbsolute(batchFolder, page.OriginalFile);
            if (!string.IsNullOrWhiteSpace(resolvedOriginal)) job.OriginalFilePath = resolvedOriginal!;

            if (page.ProcessedFiles.Count > 0)
            {
                var resolved = page.ProcessedFiles
                    .Select(p => BatchFolder.ToAbsolute(batchFolder, p))
                    .Where(p => p != null)
                    .ToList();
                if (resolved.Count > 0) job.ProcessedFilePath = string.Join(";", resolved);
            }

            if (page.Adjustments is { } adjustments)
            {
                job.HasManualAdjustments = true;
                job.RotationDegrees = adjustments.RotationDegrees;
                job.FlipHorizontal = adjustments.FlipHorizontal;
                job.FlipVertical = adjustments.FlipVertical;
                job.Brightness = adjustments.Brightness;
                job.Contrast = adjustments.Contrast;
                job.Saturation = adjustments.Saturation;
                job.Sharpness = adjustments.Sharpness;
                job.WhiteBalance = adjustments.WhiteBalance;
            }
        }

        // Rows this machine holds that the manifest doesn't list are pages deleted on another
        // machine. Marked Superseded rather than deleted outright — the same status the existing
        // recapture path uses to retire an attempt — so they drop out of the cart and exports
        // without destroying anything locally.
        var manifestJobIds = manifest.Pages.Select(p => p.JobId).ToHashSet();
        foreach (var (id, job) in existing)
        {
            if (!manifestJobIds.Contains(id) && job.ProcessingStatus != "Superseded")
                job.ProcessingStatus = "Superseded";
        }
    }

    /// <summary>Writes the current database state back out to the manifest. Called whenever
    /// something the manifest describes changes — a new page, a reorder, a deletion, a settings
    /// change — so the folder stays the copy that can be trusted.</summary>
    public async Task PublishAsync(Batch batch)
    {
        if (string.IsNullOrWhiteSpace(batch.FolderPath)) return;

        var manifest = _manifests.Load(batch.FolderPath) ?? new BatchManifest { BatchId = batch.Id };
        var project = await _dbContext.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == batch.ProjectId);

        manifest.BatchId = batch.Id;
        manifest.ProjectId = batch.ProjectId;
        manifest.BatchCode = batch.BatchCode;
        manifest.ProjectCode = project?.Name ?? manifest.ProjectCode;
        manifest.Status = batch.Status;
        manifest.Settings = new BatchManifestSettings
        {
            Dpi = batch.Dpi,
            CaptureFormat = manifest.Settings.CaptureFormat,
            PreferredExportFormat = batch.PreferredExportFormat,
            DewarpEnabled = batch.DewarpEnabled,
            SplitBookPages = batch.SplitBookPages,
            BinarizeEnabled = batch.BinarizeEnabled,
            BleedthroughEnabled = batch.BleedthroughEnabled,
            FixedFrames = batch.FixedFrames,
            FixedFrameImageWidth = batch.FixedFrameImageWidth,
            FixedFrameImageHeight = batch.FixedFrameImageHeight,
            WatermarkEnabled = batch.WatermarkEnabled,
            WatermarkPresetId = batch.WatermarkPresetId
        };

        var jobs = await _dbContext.CaptureJobs.AsNoTracking()
            .Where(j => j.BatchId == batch.Id)
            .OrderBy(j => j.PageNumber)
            .ToListAsync();

        manifest.Pages = jobs
            .Where(j => j.ProcessingStatus != "Superseded")
            .Select(j => ToManifestPage(batch.FolderPath!, j))
            .ToList();

        _manifests.Save(batch.FolderPath!, manifest);
    }

    private static BatchManifestPage ToManifestPage(string batchFolder, CaptureJob job) => new()
    {
        PageNumber = job.PageNumber,
        JobId = job.Id,
        ProcessingStatus = job.ProcessingStatus,
        CapturedUtc = job.Timestamp,
        OriginalFile = BatchFolder.ToRelative(batchFolder, job.OriginalFilePath),
        ProcessedFiles = (job.ProcessedFilePath ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => BatchFolder.ToRelative(batchFolder, p))
            .Where(p => p != null)
            .Select(p => p!)
            .ToList(),
        ThumbnailFile = $"{BatchFolder.ThumbnailsFolderName}/{job.PageNumber:D6}.png",
        Adjustments = job.HasManualAdjustments
            ? new BatchManifestAdjustments
            {
                RotationDegrees = job.RotationDegrees,
                FlipHorizontal = job.FlipHorizontal,
                FlipVertical = job.FlipVertical,
                Brightness = job.Brightness,
                Contrast = job.Contrast,
                Saturation = job.Saturation,
                Sharpness = job.Sharpness,
                WhiteBalance = job.WhiteBalance
            }
            : null
    };

    /// <summary>Adopts a batch that predates batch folders, but ONLY when its files genuinely sit
    /// inside the folder being claimed.
    ///
    /// <para>This used to claim <c>&lt;projectOutputDir&gt;/&lt;batchCode&gt;/</c> unconditionally and
    /// publish a manifest for it. A legacy batch's images live flat in
    /// <c>&lt;projectOutputDir&gt;/</c> itself — outside that new folder — and
    /// <see cref="BatchFolder.ToRelative"/> refuses to record a path outside the batch folder, so
    /// every page was written with no file references at all. Opening the batch a second time
    /// then read that manifest back and blanked <c>OriginalFilePath</c> and
    /// <c>ProcessedFilePath</c> on every job, breaking Crop Review, thumbnails and export for a
    /// batch that had been perfectly intact.</para>
    ///
    /// <para>A legacy batch is therefore left alone: no folder, no manifest, and it keeps working
    /// exactly as it did before batch folders existed. It gains portability only if its files
    /// are actually moved into a batch folder, which is a deliberate migration rather than
    /// something to do silently behind an operator opening a batch.</para></summary>
    public async Task<string?> BackfillLegacyBatchAsync(Batch batch, string projectOutputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(batch.FolderPath)) return batch.FolderPath;
        if (string.IsNullOrWhiteSpace(projectOutputDirectory)) return null;

        var folder = Path.Combine(projectOutputDirectory, batch.BatchCode);
        if (!Directory.Exists(folder)) return null;

        // Only adopt if this batch's pages really are inside that folder. Anything else would
        // produce a manifest that describes none of them.
        var jobs = await _dbContext.CaptureJobs.AsNoTracking()
            .Where(j => j.BatchId == batch.Id)
            .ToListAsync();
        var referenced = jobs
            .SelectMany(j => new[] { j.OriginalFilePath, j.ProcessedFilePath })
            .Where(pth => !string.IsNullOrWhiteSpace(pth))
            .SelectMany(pth => pth!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        if (referenced.Count > 0 && !referenced.Any(pth => BatchFolder.ToRelative(folder, pth) != null))
            return null;

        BatchFolder.EnsureLayout(folder);
        batch.FolderPath = folder;
        await _dbContext.SaveChangesAsync();
        await PublishAsync(batch);
        return folder;
    }
}
