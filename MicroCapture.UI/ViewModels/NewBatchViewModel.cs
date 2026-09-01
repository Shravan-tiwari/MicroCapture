using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Models;

namespace MicroCapture.UI.ViewModels;

/// <summary>Backs the New Batch dialog — everything that defines a batch, gathered before the
/// first capture rather than adjusted from the main window mid-batch.
///
/// <para>These settings are deliberately fixed once the batch is created. Every page in a batch
/// should be produced the same way; DPI and format changing partway through a batch yields a
/// batch whose pages don't match each other, which is exactly what the operator then has to
/// discover at export time. <see cref="CaptureJob.Dpi"/> already snapshotted DPI per job for the
/// same reason, so locking these is consistent with how capture already behaved.</para></summary>
public partial class NewBatchViewModel : ObservableObject
{
    [ObservableProperty] private string _projectCode = string.Empty;
    [ObservableProperty] private string _batchCode = string.Empty;
    [ObservableProperty] private string _batchLocation = string.Empty;

    [ObservableProperty] private int _selectedDpi = 300;
    [ObservableProperty] private string _selectedCaptureFormat = "TIFF";
    [ObservableProperty] private string _selectedExportFormat = "PDF";

    [ObservableProperty] private bool _dewarpEnabled;
    [ObservableProperty] private bool _splitBookPages;
    [ObservableProperty] private bool _binarizeEnabled;
    [ObservableProperty] private bool _bleedthroughEnabled;

    [ObservableProperty] private string _validationMessage = string.Empty;
    [ObservableProperty] private bool _hasValidationMessage;

    /// <summary>One known project the operator can pick from the project-code box's suggestion
    /// list. <see cref="Location"/> is the folder that project's batches live under — picking the
    /// suggestion snaps <see cref="NewBatchViewModel.BatchLocation"/> to it.</summary>
    public sealed record KnownProject(string Code, string Location)
    {
        // AutoCompleteBox shows this in the drop-down and, on selection, writes it back into the
        // bound Text — so it has to be exactly the project code, nothing decorative.
        public override string ToString() => Code;
    }

    /// <summary>Every existing project, for the project-code box's suggestions. Set by the caller,
    /// which owns the database — the VM stays free of a DbContext dependency. Empty is fine (no
    /// suggestions, plain free text). Codes are matched case-insensitively and de-duplicated by
    /// the caller.</summary>
    public IReadOnlyList<KnownProject> KnownProjects { get; set; } = System.Array.Empty<KnownProject>();

    /// <summary>Looks up a known project by (sanitized) code, case-insensitively; null if it isn't
    /// one. Used to snap the location when the operator types or picks an existing project — see
    /// <see cref="OnProjectCodeChanged"/>.</summary>
    private KnownProject? FindKnownProject(string code)
    {
        var sanitized = MicroCapture.Core.FileNaming.Sanitize(code);
        return KnownProjects.FirstOrDefault(p =>
            string.Equals(MicroCapture.Core.FileNaming.Sanitize(p.Code), sanitized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Full DPI range from the requirements. 150 is the rig's native captured size —
    /// below that the image is downsampled, above it upsampled — see Batch.Dpi.</summary>
    public IReadOnlyList<int> AvailableDpiOptions { get; } = new[] { 50, 100, 150, 200, 300, 400, 600, 800, 1000, 1200 };

    public IReadOnlyList<string> AvailableCaptureFormats { get; } = new[] { "TIFF", "TIFF LZW", "JPEG", "PNG", "JPEG 2000", "BMP" };

    // Taken from the exporter so the batch's preferred format can be any format Finalize can
    // actually produce. This used to list only the PDF-style outputs, so TIFF, JPEG, PNG,
    // JPEG 2000 and BMP could never be a batch's default.
    public IReadOnlyList<string> AvailableExportFormats { get; } =
        MicroCapture.Processing.ExportFormat.SelectableNames;

    /// <summary>The folder the batch will be created in: the location the operator picked, then
    /// the project code, then the batch code. Nesting every batch under its project folder keeps
    /// one project's batches together on disk and makes the project/batch split visible in the
    /// directory tree rather than only in this dialog.</summary>
    public string ResolvedBatchFolder =>
        string.IsNullOrWhiteSpace(BatchLocation) || string.IsNullOrWhiteSpace(ProjectCode) || string.IsNullOrWhiteSpace(BatchCode)
            ? string.Empty
            : Path.Combine(
                BatchLocation,
                MicroCapture.Core.FileNaming.Sanitize(ProjectCode),
                MicroCapture.Core.FileNaming.Sanitize(BatchCode));

    public bool Confirmed { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the operator asks to pick a folder — the view owns the picker, since
    /// the storage-provider API needs the window.</summary>
    public event EventHandler? BrowseRequested;

    /// <summary>Set true once the operator has typed in or picked the location box by hand. From
    /// then on, re-typing a project code no longer snaps the location, so it can't yank the batch
    /// out from under a folder the operator deliberately chose. Suppressed while
    /// <see cref="OnProjectCodeChanged"/> is doing the snap itself.</summary>
    private bool _locationChosenByOperator;
    private bool _snappingLocation;

    partial void OnBatchLocationChanged(string value)
    {
        ClearValidation();
        if (!_snappingLocation) _locationChosenByOperator = true;
        OnPropertyChanged(nameof(ResolvedBatchFolder));
    }

    partial void OnBatchCodeChanged(string value)
    {
        ClearValidation();
        OnPropertyChanged(nameof(ResolvedBatchFolder));
    }

    partial void OnProjectCodeChanged(string value)
    {
        ClearValidation();
        SnapLocationToKnownProject(value);
        OnPropertyChanged(nameof(ResolvedBatchFolder));
    }

    /// <summary>If <paramref name="code"/> names an existing project, point the location box at
    /// that project's folder so the new batch lands beside its siblings — the whole reason a
    /// project code exists. Skipped once the operator has chosen a location by hand, so it can't
    /// yank the batch out from under a folder they picked deliberately.</summary>
    private void SnapLocationToKnownProject(string code)
    {
        if (_locationChosenByOperator || string.IsNullOrWhiteSpace(code)) return;

        var known = FindKnownProject(code);
        if (known == null || string.IsNullOrWhiteSpace(known.Location)) return;
        if (string.Equals(known.Location, BatchLocation, StringComparison.OrdinalIgnoreCase)) return;

        _snappingLocation = true;
        try { BatchLocation = known.Location; }
        finally { _snappingLocation = false; }
    }

    /// <summary>Seeds the location box with the caller's fallback default without it counting as
    /// an operator choice, so a later known-project pick can still snap the location. If the
    /// project code seeded before this call already names an existing project, that project's
    /// folder wins over the fallback.</summary>
    public void SeedDefaultLocation(string fallback)
    {
        var known = string.IsNullOrWhiteSpace(ProjectCode) ? null : FindKnownProject(ProjectCode);
        var seed = !string.IsNullOrWhiteSpace(known?.Location) ? known!.Location : fallback;

        _snappingLocation = true;
        try { BatchLocation = seed; }
        finally { _snappingLocation = false; }
    }

    private void ClearValidation()
    {
        HasValidationMessage = false;
        ValidationMessage = string.Empty;
    }

    [RelayCommand]
    private void Browse() => BrowseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Create()
    {
        if (string.IsNullOrWhiteSpace(ProjectCode)) { Fail("Enter a project code."); return; }
        if (string.IsNullOrWhiteSpace(BatchCode)) { Fail("Enter a batch code."); return; }
        if (string.IsNullOrWhiteSpace(BatchLocation)) { Fail("Choose where the batch should be saved."); return; }

        var folder = ResolvedBatchFolder;

        // Refuse to reuse a folder that already holds a batch. Creating "on top of" one would
        // leave two batches sharing a manifest and an output folder, and the second would appear
        // to swallow the first's pages.
        if (BatchFolder.LooksLikeBatch(folder))
        {
            Fail($"There is already a batch in {folder}. Use Open Batch to continue it, or pick a different batch code.");
            return;
        }

        // A non-empty folder that isn't a batch is more likely a mistake (the operator picked
        // their whole Pictures folder) than an intent to scan into it, and writing a batch
        // structure into it would scatter files among theirs.
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Fail($"{folder} already exists and isn't empty. Pick a different batch code or location.");
            return;
        }

        try
        {
            // Surface a bad path or a disconnected drive now, while the operator is still looking
            // at the dialog, rather than on the first capture.
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Fail($"Can't create {folder}: {ex.Message}");
            return;
        }

        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Fail(string message)
    {
        ValidationMessage = message;
        HasValidationMessage = true;
    }

    /// <summary>Builds the manifest describing this batch. The caller owns writing it, since the
    /// batch id has to match the database row created alongside it.</summary>
    public BatchManifest ToManifest(string batchId, string projectId) => new()
    {
        BatchId = batchId,
        ProjectId = projectId,
        BatchCode = MicroCapture.Core.FileNaming.Sanitize(BatchCode),
        ProjectCode = MicroCapture.Core.FileNaming.Sanitize(ProjectCode),
        Settings = new BatchManifestSettings
        {
            Dpi = SelectedDpi,
            CaptureFormat = SelectedCaptureFormat,
            PreferredExportFormat = SelectedExportFormat,
            DewarpEnabled = DewarpEnabled,
            SplitBookPages = SplitBookPages,
            BinarizeEnabled = BinarizeEnabled,
            BleedthroughEnabled = BleedthroughEnabled
        }
    };
}
