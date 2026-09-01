using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;

namespace MicroCapture.UI.ViewModels;

/// <summary>One batch offered in the Open Batch list.</summary>
public partial class OpenBatchRow : ObservableObject
{
    public string FolderPath { get; init; } = string.Empty;
    public string BatchCode { get; init; } = string.Empty;
    public string ProjectCode { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    /// <summary>Set when the folder can't be reached — a NAS that's offline, a USB stick that's
    /// been unplugged, a sharing PC that's asleep. Shown as unavailable rather than hidden, so the
    /// operator can tell "gone" from "never existed".</summary>
    public bool IsUnavailable { get; init; }
    /// <summary>When the batch was last written — its manifest's UpdatedUtc, or DateTime.MinValue
    /// if that couldn't be read. Batches sort newest-first within their project, and a project
    /// sorts by its newest batch. A recent folder with no readable manifest keeps its position in
    /// the recent list via <see cref="RecentRank"/> instead.</summary>
    public DateTime UpdatedUtc { get; init; }
    /// <summary>Index of this folder in the operator's recent list, or int.MaxValue if it wasn't
    /// in it. Lower = more recently opened. Used to order projects/batches whose manifests give no
    /// usable timestamp.</summary>
    public int RecentRank { get; init; } = int.MaxValue;
}

/// <summary>A project and its batches, as shown in the Open Batch list: a header row that expands
/// to the batches under it, newest first.</summary>
public partial class OpenBatchProjectGroup : ObservableObject
{
    public string ProjectCode { get; init; } = string.Empty;
    public IReadOnlyList<OpenBatchRow> Batches { get; init; } = System.Array.Empty<OpenBatchRow>();

    /// <summary>Count line under the project name — "3 batches", "1 batch".</summary>
    public string Summary => Batches.Count == 1 ? "1 batch" : $"{Batches.Count} batches";

    /// <summary>The most recently opened project starts expanded so reopening the batch you were
    /// just in is a single click; the rest collapse to keep a shop's worth of projects scannable.</summary>
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>Backs the Open Batch dialog.
///
/// <para>Three routes in — browse the configured batch locations, pick any folder from the drive,
/// or reopen something recent — but one way out: every route resolves to a batch FOLDER and runs
/// the same validation. Recent is a list of folder paths, not database rows, so it stays a
/// convenience cache rather than a second, competing idea of what a batch is.</para></summary>
public partial class OpenBatchViewModel : ObservableObject
{
    private readonly BatchManifestService _manifests;
    private readonly IReadOnlyList<string> _searchRoots;

    public OpenBatchViewModel(BatchManifestService manifests, IEnumerable<string> searchRoots, IEnumerable<string> recentFolders)
    {
        _manifests = manifests;
        _searchRoots = searchRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        RecentFolders = recentFolders.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
    }

    public IReadOnlyList<string> RecentFolders { get; }

    /// <summary>Projects, newest-active first, each holding its batches. The dialog binds to this;
    /// <see cref="Batches"/> is the flat list it's built from.</summary>
    public ObservableCollection<OpenBatchProjectGroup> Projects { get; } = new();

    public ObservableCollection<OpenBatchRow> Batches { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasStatusMessage;

    /// <summary>The chosen batch folder, or null if the operator closed without picking.</summary>
    public string? SelectedFolder { get; private set; }

    public event EventHandler? CloseRequested;
    public event EventHandler? BrowseRequested;

    /// <summary>Builds the list from the recent folders plus a scan of each configured location.
    ///
    /// <para>The scan is what makes batches visible across machines without a server: a batch is
    /// discovered by finding its manifest on disk, so any batch on a reachable drive can be
    /// opened, whichever workstation created it. It's read-only, so several machines scanning the
    /// same share can't conflict, and the result can always be rebuilt by scanning again.</para></summary>
    public async Task LoadAsync()
    {
        IsScanning = true;
        try
        {
            var rows = await Task.Run(() => Discover());

            Batches.Clear();
            foreach (var row in rows) Batches.Add(row);

            Projects.Clear();
            foreach (var group in GroupByProject(rows)) Projects.Add(group);
            if (Projects.Count > 0) Projects[0].IsExpanded = true;

            IsEmpty = Batches.Count == 0;
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Turns the flat scan into project groups. Batches sort newest-first inside a
    /// project (by manifest UpdatedUtc, falling back to recent-list position); projects sort by
    /// their newest batch, so the project you last worked in is on top.</summary>
    private static IEnumerable<OpenBatchProjectGroup> GroupByProject(IEnumerable<OpenBatchRow> rows)
    {
        static (DateTime, int) Key(OpenBatchRow r) => (r.UpdatedUtc, -r.RecentRank);

        return rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.ProjectCode) ? "(no project)" : r.ProjectCode)
            .Select(g => new OpenBatchProjectGroup
            {
                ProjectCode = g.Key,
                Batches = g.OrderByDescending(Key).ToList()
            })
            .OrderByDescending(p => p.Batches.Select(Key).Max())
            .ToList();
    }

    private List<OpenBatchRow> Discover()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<OpenBatchRow>();

        // Recent first — the common case is reopening what you were just working on, and it
        // should not be buried under a scan of every batch in the shop. The recent list is
        // already in most-recent-first order, so its index is the recency rank for batches whose
        // manifest gives no usable timestamp.
        for (var i = 0; i < RecentFolders.Count; i++)
        {
            var folder = RecentFolders[i];
            if (!seen.Add(Path.GetFullPath(folder))) continue;
            rows.Add(Describe(folder, i));
        }

        foreach (var root in _searchRoots)
        {
            foreach (var folder in FindBatchFolders(root))
            {
                if (!seen.Add(Path.GetFullPath(folder))) continue;
                rows.Add(Describe(folder, int.MaxValue));
            }
        }

        return rows;
    }

    /// <summary>Finds batch folders under a root by looking for manifests. Bounded in depth: a
    /// batch folder sits at <c>&lt;chosen location&gt;/&lt;projectCode&gt;/&lt;batchCode&gt;</c>
    /// (older flat batches one level shallower), so an unbounded walk of a whole network share
    /// would cost far more than it could find.</summary>
    private static IEnumerable<string> FindBatchFolders(string root, int maxDepth = 3)
    {
        var results = new List<string>();
        try
        {
            if (!Directory.Exists(root)) return results;
            Walk(root, 0);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return results;

        void Walk(string folder, int depth)
        {
            if (depth > maxDepth) return;
            try
            {
                if (BatchFolder.LooksLikeBatch(folder))
                {
                    results.Add(folder);
                    // A batch never nests inside another batch; stop rather than descending into
                    // its output and thumbnail folders.
                    return;
                }
                foreach (var child in Directory.EnumerateDirectories(folder)) Walk(child, depth + 1);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Best-guess project code for a folder we can't read a manifest from: the batch
    /// folder now sits at <c>.../&lt;projectCode&gt;/&lt;batchCode&gt;</c>, so its parent's name is
    /// the project. Empty for a flat (pre-nesting) batch folder or a drive root.</summary>
    private static string ProjectFromParent(string folder)
    {
        var parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(parent) ? string.Empty : Path.GetFileName(parent);
    }

    private OpenBatchRow Describe(string folder, int recentRank)
    {
        if (!Directory.Exists(folder))
        {
            return new OpenBatchRow
            {
                FolderPath = folder,
                BatchCode = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
                ProjectCode = ProjectFromParent(folder),
                Subtitle = $"Unavailable — {folder}",
                Status = "Offline",
                IsUnavailable = true,
                RecentRank = recentRank
            };
        }

        var manifest = _manifests.Load(folder);
        if (manifest == null)
        {
            return new OpenBatchRow
            {
                FolderPath = folder,
                BatchCode = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)),
                ProjectCode = ProjectFromParent(folder),
                Subtitle = folder,
                Status = "Not a batch",
                IsUnavailable = true,
                RecentRank = recentRank
            };
        }

        var pages = manifest.PageCount == 1 ? "1 page" : $"{manifest.PageCount} pages";
        var device = string.IsNullOrWhiteSpace(manifest.CreatedOnDevice) ? "" : $" · {manifest.CreatedOnDevice}";
        return new OpenBatchRow
        {
            FolderPath = folder,
            BatchCode = manifest.BatchCode,
            ProjectCode = manifest.ProjectCode,
            Subtitle = $"{pages} · {manifest.Settings.Dpi} DPI {manifest.Settings.CaptureFormat}{device} · {folder}",
            Status = manifest.Status,
            UpdatedUtc = manifest.UpdatedUtc,
            RecentRank = recentRank
        };
    }

    [RelayCommand]
    private void Select(OpenBatchRow? row)
    {
        if (row == null) return;
        Choose(row.FolderPath);
    }

    /// <summary>Validates and accepts a folder, whichever route it arrived by. Anything that isn't
    /// an openable batch is reported here with the specific reason, so the operator never gets a
    /// bare failure after the dialog has closed.</summary>
    public void Choose(string folder)
    {
        var validation = _manifests.Validate(folder);
        if (!validation.IsValid)
        {
            StatusMessage = validation.Error ?? "That folder can't be opened as a batch.";
            HasStatusMessage = true;
            return;
        }

        SelectedFolder = folder;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Browse() => BrowseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Close()
    {
        SelectedFolder = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
