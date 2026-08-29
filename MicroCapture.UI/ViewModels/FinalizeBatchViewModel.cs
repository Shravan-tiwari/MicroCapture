using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// <summary>Whether this export needs OCR text. Decided entirely by the chosen format now:
    /// "Searchable PDF" and "PDF/A" mean searchable by definition, and "OCR Text" is nothing but
    /// text. A separate checkbox could contradict the format's own name, which left an operator
    /// choosing Searchable PDF and unchecking the very thing that makes it searchable.</summary>
    public bool NeedsOcr =>
        MicroCapture.Processing.ExportFormat.Resolve(SelectedFormat) is { } f
        && (f.EmbedsText || f.Kind == MicroCapture.Processing.ExportKind.TextOnly);
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _destinationDirectory = string.Empty;
    [ObservableProperty] private bool _isBusy;

    // Progress surface for a running export. Kept separate from IsBusy so the dialog can show a
    // real progress panel rather than only disabling its buttons.
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private bool _progressIsIndeterminate;
    [ObservableProperty] private string _elapsedText = string.Empty;

    private CancellationTokenSource? _exportCancellation;
    private DispatcherTimer? _elapsedTimer;
    private DateTime _exportStartedUtc;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _watermarkEnabled;
    [ObservableProperty] private WatermarkPreset? _selectedWatermarkPreset;

    // Every PDF-producing format, not the literal string "PDF" — otherwise "Searchable PDF" and
    // "PDF/A" hid the very OCR option that defines them.
    public bool IsPdfFormat =>
        MicroCapture.Processing.ExportFormat.Resolve(SelectedFormat)?.Kind == MicroCapture.Processing.ExportKind.Pdf;

    /// <summary>Whether a watermark can be burned into this format. Everything that produces an
    /// image or a PDF can carry one; only the text-only export can't.</summary>
    public bool SupportsWatermark =>
        MicroCapture.Processing.ExportFormat.Resolve(SelectedFormat)?.Kind != MicroCapture.Processing.ExportKind.TextOnly;

    partial void OnSelectedFormatChanged(string value)
    {
        OnPropertyChanged(nameof(IsPdfFormat));
        OnPropertyChanged(nameof(SupportsWatermark));
        OnPropertyChanged(nameof(NeedsOcr));
    }

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
                // Thumbnails live inside the batch folder once a batch has one, so they travel
                // with it; only a batch predating batch folders still uses the project layout.
                var thumbPath = !string.IsNullOrWhiteSpace(_batch.FolderPath)
                    ? Path.Combine(MicroCapture.Core.Models.BatchFolder.ThumbnailsPath(_batch.FolderPath!), $"{job.PageNumber:D6}.png")
                    : ThumbnailPaths.FileFor(_defaultOutputDirectory, _batch.BatchCode, job.PageNumber);
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
    private async Task EditWatermarkAsync(Avalonia.Controls.Window? owner)
    {
        try
        {
        // Nullable because the command parameter can resolve to null before the window is fully
        // in the visual tree; a non-nullable parameter turned that into a hard failure with no
        // explanation rather than a no-op.
        if (owner == null)
        {
            StatusText = "Could not open the watermark editor — try again once the window has finished opening.";
            return;
        }

        if (Pages.Count == 0)
        {
            StatusText = "Wait for at least one page to finish processing before editing a watermark.";
            return;
        }

        var job = await _dbContext.CaptureJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == Pages[0].JobId);
        if (job == null)
        {
            // Previously a bare return. The button appeared to do nothing at all, with no message
            // and no dialog — indistinguishable from a dead control.
            StatusText = "Could not load a page to design the watermark against.";
            return;
        }

        var samplePath = BatchExportService.GetProcessedFilesForJob(job).FirstOrDefault();
        if (samplePath == null)
        {
            StatusText = "Could not find a sample page to preview the watermark against.";
            return;
        }

        var saved = await WatermarkEditorDialog.RunAsync(owner, _dbContext, SelectedWatermarkPreset, samplePath);
        if (saved == null) return;

        await LoadWatermarkPresetsAsync(saved.Id);

        // LoadWatermarkPresetsAsync assigns the backing field directly so that RESTORING a saved
        // choice doesn't re-persist it. That is right on load and wrong here: the operator just
        // created or edited a preset, so the batch has to be told which one to use. Without this
        // the batch kept its previous (often null) WatermarkPresetId, export re-queried it, found
        // no preset, and drew nothing — a watermark the operator had just designed and saved
        // simply did not appear.
        PersistWatermarkSetting(b =>
        {
            b.WatermarkPresetId = saved.Id;
            b.WatermarkEnabled = true;
        });

        WatermarkEnabled = true; // opening the editor and saving implies the operator wants it applied
        StatusText = $"Watermark '{saved.Name}' will be applied to this export.";
        }
        catch (Exception ex)
        {
            // An exception escaping here leaves the AsyncRelayCommand permanently disabled for
            // the rest of the session — the button stops responding entirely and nothing says
            // why, which is exactly how a one-off failure becomes "the button doesn't work".
            StatusText = $"Could not open the watermark editor: {ex.Message}";
            Console.Error.WriteLine($"[FinalizeBatchViewModel] EditWatermark failed: {ex}");
        }
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
            HasOcrStatus = true;
            _exportCancellation = new CancellationTokenSource();
            var token = _exportCancellation.Token;
            BeginProgress("Reading text from pages (OCR)");

            // Same reason the export runs off the UI thread: OCR is a subprocess per page and can
            // take seconds each, so awaiting it here froze the dialog for the whole run.
            var ocrProgress = new Progress<(int Done, int Total)>(p =>
                ReportProgress(new ExportProgress("Reading text from pages (OCR)", p.Done, p.Total)));
            var summary = await Task.Run(() =>
                new BatchOcrService(_dbContext).RunOcrForBatchAsync(_batch.Id, ocrProgress, token), token);
            OcrStatusText = summary.CliMissing
                ? "OCR isn't available — Tesseract wasn't found on this machine."
                : summary.Failed > 0
                    ? $"OCR finished with {summary.Failed} page(s) failing — the export will still run."
                    : "OCR complete — the export won't need to wait for it.";
        }
        catch (OperationCanceledException)
        {
            OcrStatusText = "OCR stopped. Pages already read keep their text.";
        }
        catch (Exception ex)
        {
            OcrStatusText = $"OCR failed: {ex.Message}";
        }
        finally
        {
            EndProgress();
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _exportCancellation = new CancellationTokenSource();
        var token = _exportCancellation.Token;
        BeginProgress($"Preparing {SelectedFormat} export");
        try
        {
            var pageCount = Pages.Count;
            var missingOcrText = false;

            if (NeedsOcr)
            {
                // OCR is usually the long half — a page can take seconds — so it reports its own
                // per-page progress rather than sitting behind one static message.
                var ocrProgress = new Progress<(int Done, int Total)>(p =>
                    ReportProgress(new ExportProgress("Reading text from pages (OCR)", p.Done, p.Total)));
                var ocrService = new BatchOcrService(_dbContext);
                var summary = await Task.Run(() => ocrService.RunOcrForBatchAsync(_batch.Id, ocrProgress, token), token);
                missingOcrText = summary is { CliMissing: true } or { Failed: > 0 };
            }

            var exportService = new BatchExportService(_dbContext);
            var orderedJobIds = Pages.Select(p => p.JobId).ToList();
            var exportProgress = new Progress<ExportProgress>(ReportProgress);

            // Task.Run is the whole point. ExportBatchAsync is async in signature only — decoding
            // full-resolution pages, compositing watermarks and writing the document are all
            // synchronous CPU work, so awaiting it directly ran every bit of that on the UI
            // thread. The window stopped repainting for the entire export, which is
            // indistinguishable from a hang; operators killed the app mid-write.
            var exportPath = await Task.Run(() => exportService.ExportBatchAsync(
                _batch.Id, _defaultOutputDirectory, SelectedFormat,
                orderedJobIds: orderedJobIds,
                customFileName: string.IsNullOrWhiteSpace(FileName) ? null : FileName,
                customOutputDirectory: string.IsNullOrWhiteSpace(DestinationDirectory) ? null : DestinationDirectory,
                progress: exportProgress,
                token: token), token);

            // Only a searchable format can be missing its text layer; a plain PDF was never
            // meant to have one.

            Result = new FinalizeResult(exportPath, missingOcrText && NeedsOcr);
            _refreshTimer?.Stop();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // Nothing to undo: the output is written to a .partial file that is only moved into
            // place on success, and no original is deleted before that.
            StatusText = "Export cancelled — the batch is unchanged.";
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
            EndProgress();
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            IsBusy = false;
        }
    }

    /// <summary>Asks the running export to stop at the next page boundary.</summary>
    [RelayCommand]
    private void CancelExport()
    {
        if (_exportCancellation is not { IsCancellationRequested: false }) return;
        StatusText = "Finishing the current page, then stopping…";
        _exportCancellation.Cancel();
    }

    private void BeginProgress(string phase)
    {
        IsExporting = true;
        ProgressIsIndeterminate = true;
        ProgressFraction = 0;
        ProgressText = phase;
        ElapsedText = string.Empty;
        _exportStartedUtc = DateTime.UtcNow;
        _elapsedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick -= OnElapsedTick;
        _elapsedTimer.Tick += OnElapsedTick;
        _elapsedTimer.Start();
    }

    private void EndProgress()
    {
        _elapsedTimer?.Stop();
        IsExporting = false;
        ProgressIsIndeterminate = false;
        ProgressFraction = 0;
        ProgressText = string.Empty;
        ElapsedText = string.Empty;
    }

    /// <summary>Shows how long the export has been running. On a batch of several hundred pages
    /// even a healthy export runs for minutes, and a visibly advancing clock is what separates
    /// "working" from "stuck" when a single page happens to be slow.</summary>
    private void OnElapsedTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _exportStartedUtc;
        ElapsedText = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s elapsed"
            : $"{elapsed.Seconds}s elapsed";
    }

    /// <summary>Progress arrives from a background thread; Progress&lt;T&gt; marshals it back to the
    /// UI thread for us because it captures the synchronization context at construction.</summary>
    private void ReportProgress(ExportProgress progress)
    {
        ProgressText = progress.ToString();
        ProgressIsIndeterminate = progress.IsIndeterminate;
        ProgressFraction = progress.Fraction;
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        _refreshTimer?.Stop();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
