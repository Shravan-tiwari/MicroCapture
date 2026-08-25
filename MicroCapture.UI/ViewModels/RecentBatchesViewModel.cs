using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;

namespace MicroCapture.UI.ViewModels;

/// <summary>One row in the Recent Batches list — a read-only projection of a <see cref="Batch"/>
/// plus its parent <see cref="Project"/>'s code, formatted for display. Carries the underlying
/// <see cref="Batch"/> so selecting a row can hand the real entity back to the caller.</summary>
public class RecentBatchRow
{
    public required Batch Batch { get; init; }
    public required string ProjectCode { get; init; }
    public required string BatchCode { get; init; }
    public required string Status { get; init; }
    public required string Subtitle { get; init; }
}

/// <summary>Drives the "Recent Batches" picker dialog — lists every batch (Active, Completed, or
/// Exported) across all projects, most-recently-started first, so the operator can reopen one
/// without retyping its exact Project Code + Batch Code from memory. Selecting a row (regardless
/// of the batch's current status) is always treated as "reopen this" — the caller
/// (<see cref="MainWindowViewModel"/>) is responsible for flipping its Status back to Active and
/// hydrating the UI, matching the existing "resume an Active batch" flow so reopening a finished
/// batch behaves identically to resuming one still in progress.</summary>
public partial class RecentBatchesViewModel : ViewModelBase
{
    private readonly AppDbContext _dbContext;

    public ObservableCollection<RecentBatchRow> Batches { get; } = new();

    [ObservableProperty] private bool _isEmpty;

    /// <summary>Set by the dialog's Select command; the code-behind reads this after
    /// <see cref="Window.ShowDialog"/> returns to know which batch (if any) was picked.</summary>
    public Batch? SelectedBatch { get; private set; }

    public event EventHandler? CloseRequested;

    public RecentBatchesViewModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Design-time constructor.</summary>
    public RecentBatchesViewModel()
    {
        _dbContext = null!;
    }

    public async Task LoadAsync()
    {
        var currentDevice = Environment.MachineName;
        // AsNoTracking: this AppDbContext is the same long-lived instance MainWindowViewModel
        // uses for every capture (see CaptureQueueService.EnqueueCaptureAsync), so every
        // CaptureJob it has ever created is already sitting in its identity map. A tracked
        // query here would return those frozen-at-creation-time instances instead of each
        // job's real current status, making page counts/status badges wrong for any batch
        // already touched this session. Filter by current device so only this machine's batches appear.
        var batches = await _dbContext.Batches
            .AsNoTracking()
            .Where(b => b.DeviceId == currentDevice)
            .Include(b => b.Project)
            .Include(b => b.Captures)
            .OrderByDescending(b => b.StartTime)
            .ToListAsync();

        Batches.Clear();
        foreach (var batch in batches)
        {
            var pageCount = batch.Captures.Count > 0 ? batch.Captures.Max(c => c.PageNumber) : 0;
            Batches.Add(new RecentBatchRow
            {
                Batch = batch,
                ProjectCode = batch.Project?.Name ?? "(unknown project)",
                BatchCode = batch.BatchCode,
                Status = batch.Status,
                Subtitle = $"{pageCount} page{(pageCount == 1 ? "" : "s")} — started {batch.StartTime.ToLocalTime():MMM d, yyyy h:mm tt}"
            });
        }
        IsEmpty = Batches.Count == 0;
    }

    [RelayCommand]
    private void Select(RecentBatchRow row)
    {
        SelectedBatch = row.Batch;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close()
    {
        SelectedBatch = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
