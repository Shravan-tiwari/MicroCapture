using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MicroCapture.Core.Services;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

/// <summary>Modal Open Batch picker. Returns the chosen batch FOLDER — the batch's identity is
/// the folder and the manifest inside it, not a database row, so this is what the caller opens.</summary>
public partial class OpenBatchDialog : Window
{
    public OpenBatchDialog()
    {
        InitializeComponent();
    }

    // See RecentBatchesDialog.axaml.cs — per-row buttons use this sender.DataContext idiom rather
    // than a compiled $parent binding, which failed at runtime on the target machine.
    private void OnBatchRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OpenBatchRow row } && DataContext is OpenBatchViewModel vm)
        {
            vm.SelectCommand.Execute(row);
        }
    }

    public static async Task<string?> PickAsync(Window owner, BatchManifestService manifests,
        IEnumerable<string> searchRoots, IEnumerable<string> recentFolders)
    {
        var viewModel = new OpenBatchViewModel(manifests, searchRoots, recentFolders);
        var dialog = new OpenBatchDialog { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => dialog.Close();
        viewModel.BrowseRequested += async (_, _) => await dialog.PickFolderAsync(viewModel);
        // Kicked off without awaiting so the dialog paints immediately and shows its scanning
        // state — a scan of a slow or offline network share must never delay the window opening.
        _ = viewModel.LoadAsync();
        await dialog.ShowDialog(owner);
        return viewModel.SelectedFolder;
    }

    private async Task PickFolderAsync(OpenBatchViewModel viewModel)
    {
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a batch folder",
                AllowMultiple = false
            });

            var path = picked.FirstOrDefault()?.TryGetLocalPath();
            // Routed through Choose so a browsed folder gets the same validation and the same
            // specific error as one picked from the list.
            if (!string.IsNullOrWhiteSpace(path)) viewModel.Choose(path!);
        }
        catch (Exception)
        {
            // A picker failure must not close the dialog out from under the operator.
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
