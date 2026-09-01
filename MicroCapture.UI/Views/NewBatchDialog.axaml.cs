using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

/// <summary>Modal New Batch dialog. Follows the window-as-dialog + static async factory pattern
/// established by <see cref="ConfirmDialog"/> and <see cref="RecentBatchesDialog"/>.</summary>
public partial class NewBatchDialog : Window
{
    public NewBatchDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows the dialog and returns the operator's settings, or null if they cancelled.
    /// <paramref name="defaultLocation"/> seeds the location box so the common case is one click.
    /// <paramref name="resolveExistingProjectLocation"/> maps a sanitized project code to the
    /// folder that project's batches already live under (or null if it's a new project), so
    /// picking a known project snaps the location to it.</summary>
    public static async Task<NewBatchViewModel?> ShowAsync(
        Window owner, string? defaultLocation, string? projectCode,
        Func<string, string?>? resolveExistingProjectLocation = null)
    {
        var viewModel = new NewBatchViewModel
        {
            ResolveExistingProjectLocation = resolveExistingProjectLocation
        };
        // Seed the project code first so the location snap below sees it, then seed the location
        // as the fallback default without it counting as an operator choice.
        viewModel.ProjectCode = projectCode ?? string.Empty;
        viewModel.SeedDefaultLocation(defaultLocation ?? string.Empty);

        var dialog = new NewBatchDialog { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => dialog.Close();
        viewModel.BrowseRequested += async (_, _) => await dialog.PickFolderAsync(viewModel);
        await dialog.ShowDialog(owner);
        return viewModel.Confirmed ? viewModel : null;
    }

    /// <summary>Folder picking lives in the view because the storage-provider API is reached
    /// through the window, not the ViewModel.</summary>
    private async Task PickFolderAsync(NewBatchViewModel viewModel)
    {
        try
        {
            var start = Directory.Exists(viewModel.BatchLocation)
                ? await StorageProvider.TryGetFolderFromPathAsync(viewModel.BatchLocation)
                : null;

            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Where should this batch be saved?",
                AllowMultiple = false,
                SuggestedStartLocation = start
            });

            var folder = picked.FirstOrDefault();
            var path = folder?.TryGetLocalPath();
            // A folder with no local path is a virtual/cloud location the rest of the pipeline
            // can't write to with plain file IO — better to leave the box untouched than to fill
            // it with something that fails on the first capture.
            if (!string.IsNullOrWhiteSpace(path)) viewModel.BatchLocation = path!;
        }
        catch (Exception)
        {
            // A picker failure must not take the dialog down — the operator can still type a path.
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
