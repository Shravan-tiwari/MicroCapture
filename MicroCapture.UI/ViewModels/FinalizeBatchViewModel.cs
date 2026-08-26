using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.Processing;
using MicroCapture.UI.Views;

namespace MicroCapture.UI.ViewModels;

/// <summary>One row in the Finalize dialog's page list — a reorderable/deletable projection of
/// one <see cref="CaptureJob"/>, seeded from its persisted thumbnail (see
/// <see cref="ThumbnailPaths"/>) so the list can render instantly without decoding full-size
/// processed images.</summary>
public partial class FinalizePageRow : ViewModelBase
{
    public required string JobId { get; init; }
    public required int PageNumber { get; init; }
    [ObservableProperty] private Bitmap? _thumbnail;
}

/// <summary>Outcome of a Finalize dialog run, handed back to <see cref="MainWindowViewModel"/> so
/// it can show the right status message — mirrors what the old standalone ExportBatchAsync used
/// to compute inline.</summary>
public record FinalizeResult(string ExportPath, bool MissingOcrText);

/// <summary>Drives the Finalize Batch dialog: review the batch's completed pages, reorder or
/// delete them (export-scoped only — see <see cref="BatchExportService.ExportBatchAsync"/>'s
/// <c>orderedJobIds</c> parameter; this never touches <see cref="CaptureJob.PageNumber"/> or the
/// recapture/supersede logic keyed on it), pick an export format/filename/destination, and
/// choose whether to embed searchable OCR text before exporting. Replaces the old standalone
/// Export Batch button + format dropdown.</summary>
public partial class FinalizeBatchViewModel : ViewModelBase
{
    private readonly AppDbContext _dbContext;
    private readonly Batch _batch;
    private readonly string _defaultOutputDirectory;
    private readonly DispatcherTimer? _refreshTimer;
    private bool _everHadPages;

    public ObservableCollection<FinalizePageRow> Pages { get; } = new();
    // Sourced from the exporter itself rather than restated here, so the list offered can never
    // drift from the list that can actually be produced.
    public IReadOnlyList<string> AvailableFormats { get; } = MicroCapture.Processing.ExportFormat.SelectableNames;
    public ObservableCollection<WatermarkPreset> WatermarkPresets { get; } = new();

    [ObservableProperty] private string _selectedFormat = "PDF";
    [ObservableProperty] private bool _embedSearchableText = true;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _destinationDirectory = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _watermarkEnabled;
    [ObservableProperty] private WatermarkPreset? _selectedWatermarkPreset;

    public bool IsPdfFormat => SelectedFormat == "PDF";
    partial void OnSelectedFormatChanged(string value) => OnPropertyChanged(nameof(IsPdfFormat));

    // Unlike MainWindowViewModel's PersistBatchSettingAsync (immediate persistence during a
    // live capture session, since the setting must take effect for the very next shutter
    // press), these two toggles only ever matter at export time — but persisting them
    // immediately anyway matches the rest of the codebase's "every batch-setting toggle
    // persists the moment it changes" convention, and means ExportAsync doesn't need its own
    // separate save-then-export step.
    partial void OnWatermarkEnabledChanged(bool value) => PersistWatermarkSetting(b => b.WatermarkEnabled = value);
    partial void OnSelectedWatermarkPresetChanged(WatermarkPreset? value) => PersistWatermarkSetting(b => b.WatermarkPresetId = value?.Id);

    private async void PersistWatermarkSetting(Action<Batch> apply)
    {
        if (_dbContext == null!) return; // design-time instance
        try
        {
            var batch = await _dbContext.Batches.FindAsync(_batch.Id);
            if (batch == null) return;
            apply(batch);
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save watermark setting: {ex.Message}";
        }
    }

    public FinalizeResult? Result { get; private set; }
    public event EventHandler? CloseRequested;

    public FinalizeBatchViewModel(AppDbContext dbContext, Batch batch, string outputDirectory)
    {
        _dbContext = dbContext;
        _batch = batch;
        _defaultOutputDirectory = outputDirectory;
        SelectedFormat = string.IsNullOrWhiteSpace(batch.PreferredExportFormat) ? "PDF" : batch.PreferredExportFormat;
        DestinationDirectory = outputDirectory;
        FileName = MicroCapture.Core.FileNaming.Sanitize(string.IsNullOrEmpty(batch.Name) ? batch.BatchCode : batch.Name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _watermarkEnabled = batch.WatermarkEnabled;
        _ = LoadWatermarkPresetsAsync(batch.WatermarkPresetId);

        // LoadPages() originally ran exactly once, against the Batch.Captures snapshot handed in
        // by MainWindowViewModel.OpenFinalizeBatchAsync at the moment the dialog opened. If any
        // page was still Pending/InProgress at that instant, it was excluded from Pages forever
        // for this dialog session — even after BackgroundProcessingWorker (a separate DB
        // connection) finished it moments later — because nothing here ever re-queried. That's
        // why the "wait for thumbnails to show Processed" message never went away: the operator
        // could wait indefinitely and this dialog would never notice. Poll instead, same pattern
        // as CropReviewViewModel's own _previewTimer, stopping once every page has actually
        // finished processing (Completed or Failed) so it isn't left running for no reason.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshPagesAsync();
        _refreshTimer.Start();
    }

    /// <summary>Design-time constructor.</summary>
    public FinalizeBatchViewModel()
    {
        _dbContext = null!;
        _batch = null!;
        _defaultOutputDirectory = string.Empty;
    }

    public void LoadPages()
    {
        var jobs = CaptureQueueService.GetCompletedJobsForBatch(_batch.Captures);
        ApplyPages(jobs);
    }

    private async Task LoadWatermarkPresetsAsync(string? selectedPresetId)
    {
        if (_dbContext == null!) return; // design-time instance
        List<WatermarkPreset> presets;
        try
        {
            presets = await _dbContext.WatermarkPresets.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FinalizeBatchViewModel] Could not load watermark presets: {ex.Message}");
            return;
        }

        WatermarkPresets.Clear();
        foreach (var p in presets) WatermarkPresets.Add(p);
        // Assign the backing field directly, not the property — the property's generated setter
        // fires OnSelectedWatermarkPresetChanged, which persists to the DB (see below). That
        // would be wrong here: this call is restoring the batch's already-saved choice after an
        // async reload, not the operator making a new one.
        _selectedWatermarkPreset = WatermarkPresets.FirstOrDefault(p => p.Id == selectedPresetId);
        OnPropertyChanged(nameof(SelectedWatermarkPreset));
    }

    /// <summary>Re-queries the DB (not the <see cref="_batch"/>.Captures snapshot captured when
    /// the dialog opened) for this batch's now-current job statuses, so a page that finishes
    /// processing while the dialog is already open gets picked up — see the constructor's
    /// comment for why a one-shot <see cref="LoadPages"/> alone left the dialog permanently
    /// stuck showing "still processing" for any page not yet Completed at open time. Stops
    /// polling once nothing is left Pending/InProgress, since there's nothing further to wait
    /// for at that point.</summary>
    private async Task RefreshPagesAsync()
    {
        if (_dbContext == null!) return; // design-time instance
        List<CaptureJob> allJobs;
        try
        {
            allJobs = await _dbContext.CaptureJobs.AsNoTracking()
                .Where(j => j.BatchId == _batch.Id)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FinalizeBatchViewModel] Refresh failed: {ex.Message}");
            return;
        }

        var stillProcessing = allJobs.Any(j => j.ProcessingStatus is "Pending" or "InProgress");
        var completed = CaptureQueueService.GetCompletedJobsForBatch(allJobs);
        // Only re-render the list when the completed set actually changed — otherwise this
        // would wipe out the operator's in-progress reorder every second.
        var currentIds = Pages.Select(p => p.JobId).ToList();
        var newIds = completed.Select(j => j.Id).ToList();
        if (!currentIds.SequenceEqual(newIds))
        {
            ApplyPages(completed);
        }

        if (!stillProcessing)
        {
            _refreshTimer?.Stop();
        }
    }

    private void ApplyPages(List<CaptureJob> jobs)
    {
        foreach (var row in Pages) row.Thumbnail?.Dispose();
        Pages.Clear();
        foreach (var job in jobs)
        {
            Bitmap? thumb = null;
            try
            {
                var thumbPath = ThumbnailPaths.FileFor(_defaultOutputDirectory, _batch.BatchCode, job.PageNumber);
                if (File.Exists(thumbPath))
                    thumb = new Bitmap(thumbPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not load thumbnail for page {job.PageNumber}: {ex.Message}");
            }
            Pages.Add(new FinalizePageRow { JobId = job.Id, PageNumber = job.PageNumber, Thumbnail = thumb });
        }
        _everHadPages = _everHadPages || Pages.Count > 0;
        StatusText = Pages.Count == 0
            ? (_everHadPages ? "No completed pages to finalize yet." : "Images are still processing — wait for thumbnails to show Processed.")
            : $"{Pages.Count} page(s) ready.";
        ExportCommand.NotifyCanExecuteChanged();
    }

    private bool CanMove(FinalizePageRow? row) => row != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanMove))]
    private void MoveUp(FinalizePageRow row)
    {
        var index = Pages.IndexOf(row);
        if (index > 0) Pages.Move(index, index - 1);
    }

    [RelayCommand(CanExecute = nameof(CanMove))]
    private void MoveDown(FinalizePageRow row)
    {
        var index = Pages.IndexOf(row);
        if (index >= 0 && index < Pages.Count - 1) Pages.Move(index, index + 1);
    }

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task DeletePageAsync(FinalizePageRow row)
    {
        if (IsBusy) return;
        var queueService = new CaptureQueueService(_dbContext);
        await queueService.DeleteCaptureAsync(row.JobId);
        row.Thumbnail?.Dispose();
        Pages.Remove(row);
        StatusText = $"{Pages.Count} page(s) ready.";
    }

    [RelayCommand]
    private async Task BrowseDestinationAsync(Avalonia.Controls.Window owner)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(owner);
        if (topLevel?.StorageProvider is not { } storageProvider) return;

        var startFolder = Directory.Exists(DestinationDirectory)
            ? await storageProvider.TryGetFolderFromPathAsync(new Uri(DestinationDirectory))
            : null;
        var folders = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose export destination",
            SuggestedStartLocation = startFolder,
            AllowMultiple = false
        });
        var picked = folders.FirstOrDefault();
        if (picked?.TryGetLocalPath() is { } localPath)
            DestinationDirectory = localPath;
    }

    [RelayCommand]
    private async Task EditWatermarkAsync(Avalonia.Controls.Window owner)
    {
        if (Pages.Count == 0)
        {
            StatusText = "Wait for at least one page to finish processing before editing a watermark.";
            return;
        }

        var job = await _dbContext.CaptureJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == Pages[0].JobId);
        if (job == null) return;
        var samplePath = BatchExportService.GetProcessedFilesForJob(job).FirstOrDefault();
        if (samplePath == null)
        {
            StatusText = "Could not find a sample page to preview the watermark against.";
            return;
        }

        var saved = await WatermarkEditorDialog.RunAsync(owner, _dbContext, SelectedWatermarkPreset, samplePath);
        if (saved == null) return;

        await LoadWatermarkPresetsAsync(saved.Id);
        WatermarkEnabled = true; // opening the editor and saving implies the operator wants it applied
    }

    private bool CanExport() => !IsBusy && Pages.Count > 0;

    [ObservableProperty] private string _ocrStatusText = string.Empty;
    [ObservableProperty] private bool _hasOcrStatus;

    /// <summary>Runs OCR ahead of the export rather than as part of it. Export already runs OCR
    /// for any page lacking it, but on a large batch that lands the whole cost at the moment the
    /// operator is trying to finish; doing it here first makes the export itself quick. This is
    /// where the old toolbar "Run OCR" button went when the toolbar was cut back to the four
    /// workflow buttons.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task RunOcrNowAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            OcrStatusText = "Running OCR…";
            HasOcrStatus = true;
            var summary = await new BatchOcrService(_dbContext).RunOcrForBatchAsync(_batch.Id);
            OcrStatusText = summary.CliMissing
                ? "OCR isn't available — Tesseract wasn't found on this machine."
                : summary.Failed > 0
                    ? $"OCR finished with {summary.Failed} page(s) failing — the export will still run."
                    : "OCR complete — the export won't need to wait for it.";
        }
        catch (Exception ex)
        {
            OcrStatusText = $"OCR failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var missingOcrText = false;
            if (IsPdfFormat && EmbedSearchableText)
            {
                StatusText = "Preparing searchable text...";
                var ocrService = new BatchOcrService(_dbContext);
                var summary = await ocrService.RunOcrForBatchAsync(_batch.Id);
                missingOcrText = summary is { CliMissing: true } or { Failed: > 0 };
            }

            StatusText = $"Exporting to {SelectedFormat}...";
            var exportService = new BatchExportService(_dbContext);
            var orderedJobIds = Pages.Select(p => p.JobId).ToList();
            var exportPath = await exportService.ExportBatchAsync(
                _batch.Id, _defaultOutputDirectory, SelectedFormat,
                orderedJobIds: orderedJobIds,
                customFileName: string.IsNullOrWhiteSpace(FileName) ? null : FileName,
                customOutputDirectory: string.IsNullOrWhiteSpace(DestinationDirectory) ? null : DestinationDirectory);

            // Not exported as searchable text when the format isn't PDF or the toggle is off —
            // export path itself always embeds whatever .txt sidecars happen to exist (see
            // BatchExportService.DrawSearchText), which is only meaningful for PDF.
            missingOcrText = missingOcrText || (IsPdfFormat && !EmbedSearchableText);

            Result = new FinalizeResult(exportPath, missingOcrText && IsPdfFormat);
            _refreshTimer?.Stop();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException ex) when (ex.Message == "Images are still being processed.")
        {
            StatusText = "Images are still processing — wait for thumbnails to show Processed, then export.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        _refreshTimer?.Stop();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
