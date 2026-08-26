using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MicroCapture.UI;

/// <summary>Small per-machine conveniences that belong to this workstation rather than to any
/// batch: which folders batches were last opened from, and where to look for them.
///
/// <para>Deliberately not in the database and never in a batch's manifest. These are one
/// operator's habits on one machine — a recent-folder list that travelled inside a batch folder
/// would follow the batch onto other people's machines and describe paths that don't exist there.
/// Losing this file costs nothing: every batch is still openable by browsing to its folder.</para></summary>
public class AppPreferences
{
    private const int MaxRecentFolders = 12;

    /// <summary>Batch folders opened recently, most recent first. Paths, not database rows —
    /// clicking one runs exactly the same validated open as browsing to it, so this stays a
    /// shortcut rather than a competing idea of what a batch is.</summary>
    public List<string> RecentBatchFolders { get; set; } = new();

    /// <summary>Last folder a batch was created in, used to seed the New Batch dialog.</summary>
    public string? LastBatchLocation { get; set; }

    /// <summary>Folders scanned to list available batches — typically the shared drive batches are
    /// kept on. Scanning is read-only, so several machines pointed at one share can't conflict.</summary>
    public List<string> BatchSearchRoots { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MicroCapture", "preferences.json");

    public static AppPreferences Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new AppPreferences();
            return JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new AppPreferences();
        }
        catch (Exception)
        {
            // Corrupt or unreadable preferences must never stop the app starting — defaults are
            // perfectly usable, and nothing here is irreplaceable.
            return new AppPreferences();
        }
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Best-effort: failing to remember a recent folder is not worth interrupting work for.
        }
    }

    /// <summary>Moves a folder to the top of the recent list. Kept case-insensitive and
    /// de-duplicated so reopening the same batch doesn't fill the list with one entry.</summary>
    public void RememberBatchFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        RecentBatchFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentBatchFolders.Insert(0, folder);
        if (RecentBatchFolders.Count > MaxRecentFolders)
            RecentBatchFolders.RemoveRange(MaxRecentFolders, RecentBatchFolders.Count - MaxRecentFolders);
    }

    /// <summary>Where to look for batches: the configured roots, plus the parents of recently used
    /// folders so batches sitting beside one already opened are found without any setup. A batch
    /// on an unreachable root is simply not found — never an error.</summary>
    public IEnumerable<string> EffectiveSearchRoots()
    {
        foreach (var root in BatchSearchRoots) yield return root;

        foreach (var parent in RecentBatchFolders
                     .Select(f => Path.GetDirectoryName(f))
                     .Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return parent!;
        }

        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MicroCapture");
        yield return defaultRoot;
    }
}
