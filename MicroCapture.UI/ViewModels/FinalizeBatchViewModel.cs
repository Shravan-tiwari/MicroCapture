using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.Processing;

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

    public ObservableCollection<FinalizePageRow> Pages { get; } = new();
    public string[] AvailableFormats { get; } = { "PDF", "TIFF", "JPG", "PNG" };

    [ObservableProperty] private string _selectedFormat = "PDF";
    [ObservableProperty] private bool _embedSearchableText = true;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _destinationDirectory = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = string.Empty;

    public bool IsPdfFormat => SelectedFormat == "PDF";
    partial void OnSelectedFormatChanged(string value) => OnPropertyChanged(nameof(IsPdfFormat));

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
        StatusText = Pages.Count == 0 ? "No completed pages to finalize yet." : $"{Pages.Count} page(s) ready.";
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

    private bool CanExport() => !IsBusy && Pages.Count > 0;

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
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
