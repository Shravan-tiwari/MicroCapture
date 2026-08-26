using System.IO;

namespace MicroCapture.Core.Models;

/// <summary>The on-disk layout of a batch folder, which is the source of truth for a batch.
///
/// <para>Batch settings and page order used to live only in the SQLite database at
/// <c>%LocalAppData%\MicroCapture.db</c> — machine-local and per-user. Since batches themselves
/// live on shared drives, USB sticks, or simply get copied elsewhere, a batch created on one
/// workstation could never be opened properly on another: the images were reachable but the
/// information describing them was not. Putting a manifest inside the batch folder makes a batch
/// self-describing, so it opens correctly wherever it travels, and survives the local database
/// being lost or rebuilt.</para>
///
/// <para>Paths recorded inside the manifest are always RELATIVE to the batch folder. The same
/// batch is reached as <c>E:\</c> on one machine, <c>F:\</c> on another, <c>Z:\Scans\…</c> as a
/// mapped drive, and <c>\\PC\Scans\…</c> over Windows file sharing — an absolute path stored in
/// the manifest would break on the first of those transitions.</para></summary>
public static class BatchFolder
{
    /// <summary>The manifest — settings, page count and page order. The file whose presence makes
    /// a folder a batch.</summary>
    public const string ManifestFileName = "batch.mcbatch";

    /// <summary>Names the machine and user currently working the batch, refreshed while it stays
    /// open. Advisory only: a second machine is warned, not blocked.</summary>
    public const string LockFileName = "batch.lock";

    /// <summary>Written beside the manifest before each save and kept as the previous good copy,
    /// so a manifest truncated by a pulled USB stick or a crash mid-write is recoverable.</summary>
    public const string ManifestBackupFileName = "batch.mcbatch.bak";

    public const string ThumbnailsFolderName = "thumbnails";
    public const string TempFolderName = "temp";
    public const string OutputFolderName = "output";

    public static string ManifestPath(string batchFolder) => Path.Combine(batchFolder, ManifestFileName);
    public static string ManifestBackupPath(string batchFolder) => Path.Combine(batchFolder, ManifestBackupFileName);
    public static string LockPath(string batchFolder) => Path.Combine(batchFolder, LockFileName);
    public static string ThumbnailsPath(string batchFolder) => Path.Combine(batchFolder, ThumbnailsFolderName);
    public static string TempPath(string batchFolder) => Path.Combine(batchFolder, TempFolderName);
    public static string OutputPath(string batchFolder) => Path.Combine(batchFolder, OutputFolderName);

    /// <summary>Subfolders every batch is expected to carry, in the order they're reported to the
    /// operator when something is missing.</summary>
    public static readonly string[] RequiredFolders = { ThumbnailsFolderName, TempFolderName, OutputFolderName };

    /// <summary>Creates the folder and its subfolders. Safe to re-run on an existing batch —
    /// used both when creating one and when repairing a folder whose subfolders were removed.</summary>
    public static void EnsureLayout(string batchFolder)
    {
        Directory.CreateDirectory(batchFolder);
        foreach (var folder in RequiredFolders) Directory.CreateDirectory(Path.Combine(batchFolder, folder));
    }

    /// <summary>Whether the folder even claims to be a batch. Distinct from a full validation:
    /// this only asks whether a manifest is present, which is what a folder picker needs in order
    /// to tell "not a batch at all" from "a batch that's missing pieces".</summary>
    public static bool LooksLikeBatch(string batchFolder) => File.Exists(ManifestPath(batchFolder));

    /// <summary>Converts an absolute path inside the batch folder to the manifest's relative,
    /// forward-slash form. Returns null for a path outside the batch folder, which must never be
    /// recorded in the manifest — it wouldn't survive the batch being moved.</summary>
    public static string? ToRelative(string batchFolder, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        var root = Path.GetFullPath(batchFolder);
        var full = Path.GetFullPath(absolutePath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Resolves a manifest-relative path back against wherever the batch folder happens
    /// to live now.</summary>
    public static string? ToAbsolute(string batchFolder, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var native = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(batchFolder, native));
    }
}
