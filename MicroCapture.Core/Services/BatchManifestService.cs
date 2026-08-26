using System.Text.Json;
using System.Text.Json.Serialization;
using MicroCapture.Core.Models;

namespace MicroCapture.Core.Services;

/// <summary>Result of checking whether a folder is an openable batch. Carries a specific reason
/// rather than a bare bool: the operator needs to be told exactly which file or folder is missing,
/// which is a requirement in its own right and impossible to convey from a failed open alone.</summary>
public sealed record BatchFolderValidation(bool IsValid, string? Error, BatchManifest? Manifest)
{
    public static BatchFolderValidation Ok(BatchManifest manifest) => new(true, null, manifest);
    public static BatchFolderValidation Fail(string error) => new(false, error, null);
}

/// <summary>Reads and writes the batch manifest that makes a batch folder self-describing.
/// See <see cref="BatchFolder"/> for the rationale and the relative-path rule.</summary>
public class BatchManifestService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Writes the manifest so that an interrupted write can never leave a batch
    /// unopenable. The previous manifest is kept as a backup, the new one is written to a temp
    /// file and then moved into place — a move being the closest thing to an atomic swap the
    /// filesystem offers. This matters most on removable media, which can be unplugged mid-write.</summary>
    public void Save(string batchFolder, BatchManifest manifest)
    {
        BatchFolder.EnsureLayout(batchFolder);
        manifest.UpdatedUtc = DateTime.UtcNow;

        var path = BatchFolder.ManifestPath(batchFolder);
        var temp = path + ".tmp";
        var backup = BatchFolder.ManifestBackupPath(batchFolder);

        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, SerializerOptions));
        if (File.Exists(path))
        {
            // Replace keeps the old contents as the backup in one operation where the platform
            // supports it. Fall back to an explicit copy for the cases it doesn't (notably across
            // some network and removable filesystems), which is exactly where this protection
            // matters most — so never let the fallback path be the one that throws.
            try
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }

            try { File.Copy(path, backup, overwrite: true); } catch (IOException) { }
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Loads the manifest, falling back to the backup if the main file is missing or
    /// unreadable — the situation a crash or a pulled drive mid-write leaves behind.</summary>
    public BatchManifest? Load(string batchFolder)
    {
        var manifest = TryRead(BatchFolder.ManifestPath(batchFolder));
        if (manifest != null) return manifest;
        return TryRead(BatchFolder.ManifestBackupPath(batchFolder));
    }

    private static BatchManifest? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<BatchManifest>(File.ReadAllText(path), SerializerOptions);
            // A file that parses as JSON but carries no batch identity isn't a manifest — treat it
            // as absent so the caller falls through to the backup rather than opening a shell.
            return string.IsNullOrWhiteSpace(manifest?.BatchId) ? null : manifest;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Checks a folder is a batch this build can open, naming the specific problem when
    /// it isn't. Missing subfolders are reported but not treated as fatal — they hold derived
    /// files that can be recreated, and refusing to open a batch whose images are all present
    /// because an empty <c>temp/</c> was tidied away would lose real work for no reason.</summary>
    public BatchFolderValidation Validate(string batchFolder, bool repairMissingFolders = true)
    {
        if (string.IsNullOrWhiteSpace(batchFolder))
            return BatchFolderValidation.Fail("No batch folder was given.");

        if (!Directory.Exists(batchFolder))
            return BatchFolderValidation.Fail($"The folder no longer exists: {batchFolder}. If it's on a network drive or USB stick, check that it's still connected.");

        var manifestPath = BatchFolder.ManifestPath(batchFolder);
        var backupPath = BatchFolder.ManifestBackupPath(batchFolder);
        if (!File.Exists(manifestPath) && !File.Exists(backupPath))
            return BatchFolderValidation.Fail($"This folder isn't a MicroCapture batch — it has no {BatchFolder.ManifestFileName} file.");

        var manifest = Load(batchFolder);
        if (manifest == null)
            return BatchFolderValidation.Fail($"{BatchFolder.ManifestFileName} is damaged and couldn't be read, and no usable backup was found.");

        if (manifest.SchemaVersion > BatchManifest.CurrentSchemaVersion)
            return BatchFolderValidation.Fail($"This batch was made by a newer version of MicroCapture (format {manifest.SchemaVersion}, this build reads {BatchManifest.CurrentSchemaVersion}). Update MicroCapture to open it.");

        var missing = BatchFolder.RequiredFolders
            .Where(name => !Directory.Exists(Path.Combine(batchFolder, name)))
            .ToList();
        if (missing.Count > 0)
        {
            if (!repairMissingFolders)
                return BatchFolderValidation.Fail($"The batch is missing these folders: {string.Join(", ", missing)}.");
            BatchFolder.EnsureLayout(batchFolder);
        }

        return BatchFolderValidation.Ok(manifest);
    }

    /// <summary>Page files the manifest lists but that aren't actually on disk. Reported to the
    /// operator as a warning rather than blocking the open: a batch missing some originals is
    /// still worth opening to recover the rest.</summary>
    public IReadOnlyList<string> FindMissingPageFiles(string batchFolder, BatchManifest manifest)
    {
        var missing = new List<string>();
        foreach (var page in manifest.Pages)
        {
            if (page.ProcessingStatus == "Superseded") continue;
            foreach (var relative in EnumeratePageFiles(page))
            {
                var absolute = BatchFolder.ToAbsolute(batchFolder, relative);
                if (absolute != null && !File.Exists(absolute)) missing.Add(relative);
            }
        }
        return missing;
    }

    private static IEnumerable<string> EnumeratePageFiles(BatchManifestPage page)
    {
        if (!string.IsNullOrWhiteSpace(page.OriginalFile)) yield return page.OriginalFile!;
        foreach (var processed in page.ProcessedFiles)
            if (!string.IsNullOrWhiteSpace(processed)) yield return processed;
    }

    /// <summary>Next free page number, taken from the manifest rather than a local row count.
    ///
    /// <para>Two machines working one batch each assign page numbers from their own database, so
    /// they can land on the same number — and page number drives output filenames and
    /// recapture/supersede identity, so a collision is real corruption rather than a cosmetic
    /// clash. Reading the shared manifest immediately before assigning makes that unlikely without
    /// a reservation protocol. It is a safety net, not a guarantee: two machines capturing at the
    /// same instant can still race, which is why simultaneous capture into one batch stays
    /// discouraged and warned about.</para></summary>
    public int NextPageNumber(string batchFolder)
    {
        var manifest = Load(batchFolder);
        if (manifest == null || manifest.Pages.Count == 0) return 1;
        return manifest.Pages.Max(p => p.PageNumber) + 1;
    }
}
