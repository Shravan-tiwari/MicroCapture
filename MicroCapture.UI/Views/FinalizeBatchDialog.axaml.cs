using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

/// <summary>Modal "Finalize Batch" dialog — review/reorder/delete pages, pick export
/// format/filename/destination and a searchable-text toggle, then export. Modeled on
/// <see cref="ConfirmDialog"/>'s window-as-dialog + static async factory pattern. Replaces the
/// old standalone Export Batch button/format dropdown.</summary>
public partial class FinalizeBatchDialog : Window
{
    public FinalizeBatchDialog()
    {
        InitializeComponent();
    }

    // Per-row buttons dispatch through these code-behind handlers, not a compiled
    // "$parent[ItemsControl].((vm:FinalizeBatchViewModel)DataContext).XCommand" binding — that
    // pattern threw "unable to resolve type ... from any of the following locations" at runtime
    // on the actual target machine, which is what made Finalize "not open anything at all": the
    // dialog's own XAML failed to load/render before ShowDialog could even display it. See the
    // identical fix and explanation in RecentBatchesDialog.axaml.cs.
    private void OnMoveUpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FinalizePageRow row } && DataContext is FinalizeBatchViewModel vm)
            vm.MoveUpCommand.Execute(row);
    }

    private void OnMoveDownClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FinalizePageRow row } && DataContext is FinalizeBatchViewModel vm)
            vm.MoveDownCommand.Execute(row);
    }

    private void OnDeletePageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FinalizePageRow row } && DataContext is FinalizeBatchViewModel vm)
            vm.DeletePageCommand.Execute(row);
    }

    public static async Task<FinalizeResult?> RunAsync(Window owner, AppDbContext dbContext, Batch batch, string outputDirectory)
    {
        var viewModel = new FinalizeBatchViewModel(dbContext, batch, outputDirectory);
        var dialog = new FinalizeBatchDialog { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => dialog.Close();
        viewModel.LoadPages();
        await dialog.ShowDialog(owner);
        return viewModel.Result;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
