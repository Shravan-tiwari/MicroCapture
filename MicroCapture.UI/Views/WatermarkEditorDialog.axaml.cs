using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.UI.ViewModels;

namespace MicroCapture.UI.Views;

/// <summary>Modal watermark preset editor — pick text or logo content, drag/resize/rotate its
/// placement on a live preview of a sample page, set opacity, and save it as a named, reusable
/// preset. Modeled on <see cref="FinalizeBatchDialog"/>'s window-as-dialog + static async
/// factory pattern.</summary>
public partial class WatermarkEditorDialog : Window
{
    public WatermarkEditorDialog()
    {
        InitializeComponent();
    }

    public static async Task<WatermarkPreset?> RunAsync(Window owner, AppDbContext dbContext, WatermarkPreset? existingPreset, string samplePageImagePath)
    {
        var viewModel = new WatermarkEditorViewModel(dbContext, existingPreset, samplePageImagePath);
        var dialog = new WatermarkEditorDialog { DataContext = viewModel };
        viewModel.Saved += (_, _) => dialog.Close();
        viewModel.Cancelled += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return viewModel.Result;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
