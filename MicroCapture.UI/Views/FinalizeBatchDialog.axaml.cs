using System.Threading.Tasks;
using Avalonia.Controls;
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
