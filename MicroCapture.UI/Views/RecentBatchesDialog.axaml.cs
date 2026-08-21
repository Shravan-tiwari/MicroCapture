using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    // Row buttons dispatch through this code-behind handler, not a compiled
    // "$parent[ItemsControl].((vm:RecentBatchesViewModel)DataContext).SelectCommand" binding —
    // that pattern threw "unable to resolve type vm:RecentBatchesViewModel from any of the
    // following locations" at runtime on the actual target machine (confirmed reproducible
    // after a clean rebuild), even though it built without error. MainWindow.axaml's own
    // per-row buttons (thumbnail click/select/delete) all use this same sender.DataContext +
    // this.DataContext code-behind idiom instead, and that's proven to work.
    private void OnBatchRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RecentBatchRow row } && DataContext is RecentBatchesViewModel vm)
        {
            vm.SelectCommand.Execute(row);
        }
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
