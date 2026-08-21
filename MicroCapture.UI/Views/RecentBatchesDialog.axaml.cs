using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

/// <summary>Modal "Recent Batches" picker — lists every batch across all projects, most recent
/// first, and returns whichever one the operator picks (or null if they closed without picking).
/// Modeled on <see cref="ConfirmDialog"/>'s window-as-dialog + static async factory pattern, the
/// only prior dialog precedent in this codebase.</summary>
public partial class RecentBatchesDialog : Window
{
    public RecentBatchesDialog()
    {
        InitializeComponent();
    }

    public static async System.Threading.Tasks.Task<Batch?> PickAsync(Window owner, AppDbContext dbContext)
    {
        var viewModel = new RecentBatchesViewModel(dbContext);
        var dialog = new RecentBatchesDialog { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => dialog.Close();
        await viewModel.LoadAsync();
        await dialog.ShowDialog(owner);
        return viewModel.SelectedBatch;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
