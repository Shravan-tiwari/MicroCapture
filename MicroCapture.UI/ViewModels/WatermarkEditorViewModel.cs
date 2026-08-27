using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Processing;
using MicroCapture.UI.Controls;

namespace MicroCapture.UI.ViewModels;

/// <summary>Drives the watermark preset editor dialog: choose text or logo content, drag/resize/
/// rotate its placement on a live preview of a sample page, set opacity, and save it as a named,
/// reusable preset. Preview compositing reuses <see cref="WatermarkPreviewRenderer"/> — the same
/// drawing logic <see cref="BatchExportService"/> uses for the real PDF export — so what the
/// operator sees here is what actually gets burned into the exported page.</summary>
public partial class WatermarkEditorViewModel : ViewModelBase
{
    private readonly AppDbContext _dbContext;
    private readonly string _presetId;
    private readonly bool _isNewPreset;
    private readonly string _samplePageImagePath;
    private readonly DispatcherTimer _previewTimer;

    public string[] WatermarkTypes { get; } = { "Text", "Logo" };

    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private string _watermarkType = "Text";
    [ObservableProperty] private string _textContent = string.Empty;
    [ObservableProperty] private double _fontSize = 48.0;
    [ObservableProperty] private string _textColor = "#808080";
    [ObservableProperty] private string? _logoImagePath;
    [ObservableProperty] private Bitmap? _logoPreviewBitmap;
    [ObservableProperty] private double _opacity = 0.5;
    [ObservableProperty] private WatermarkTransform _transform = new(0.7, 0.85, 0.2, 0.1, 0);
    [ObservableProperty] private Bitmap? _pageImage;
    [ObservableProperty] private Avalonia.Size _pageImageSize;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string _statusText = string.Empty;

    public bool IsTextMode => WatermarkType == "Text";
    public bool IsLogoMode => WatermarkType == "Logo";
    partial void OnWatermarkTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsTextMode));
        OnPropertyChanged(nameof(IsLogoMode));
        SchedulePreviewUpdate();
        SaveCommand.NotifyCanExecuteChanged();
    }

    // CanSave() reads PresetName/TextContent/LogoImagePath, but [RelayCommand]'s generated
    // CanExecute only re-runs automatically for properties it's told to watch — without these,
    // the Save button's enabled state froze at whatever CanSave() returned when the dialog first
    // opened (disabled) and never updated again, even after the operator typed a name or picked
    // a logo file that made CanSave() true.
    partial void OnPresetNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnLogoImagePathChanged(string? value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnTextContentChanged(string value)
    {
        SchedulePreviewUpdate();
        SaveCommand.NotifyCanExecuteChanged();
    }
    partial void OnFontSizeChanged(double value) => SchedulePreviewUpdate();
    partial void OnTextColorChanged(string value) => SchedulePreviewUpdate();
    partial void OnOpacityChanged(double value) => SchedulePreviewUpdate();
    partial void OnTransformChanged(WatermarkTransform value) => SchedulePreviewUpdate();

    public WatermarkPreset? Result { get; private set; }
    public event EventHandler? Saved;
    public event EventHandler? Cancelled;

    public WatermarkEditorViewModel(AppDbContext dbContext, WatermarkPreset? existingPreset, string samplePageImagePath)
    {
        _dbContext = dbContext;
        _samplePageImagePath = samplePageImagePath;
        _isNewPreset = existingPreset == null;
        _presetId = existingPreset?.Id ?? Guid.NewGuid().ToString();

        if (existingPreset != null)
        {
            PresetName = existingPreset.Name;
            WatermarkType = existingPreset.WatermarkType;
            TextContent = existingPreset.TextContent ?? string.Empty;
            FontSize = existingPreset.FontSize;
            TextColor = existingPreset.TextColor ?? "#808080";
            LogoImagePath = existingPreset.LogoImagePath;
            Opacity = existingPreset.Opacity;
            Transform = new WatermarkTransform(existingPreset.X, existingPreset.Y, existingPreset.Width, existingPreset.Height, existingPreset.RotationDegrees);
        }

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            RenderPreview();
        };

        LoadSamplePage();
        LoadLogoPreview();
    }

    /// <summary>Design-time constructor.</summary>
    public WatermarkEditorViewModel()
    {
        _dbContext = null!;
        _presetId = string.Empty;
        _samplePageImagePath = string.Empty;
        _previewTimer = new DispatcherTimer();
    }

    private void LoadSamplePage()
    {
        try
        {
            var bytes = ImageDecodeHelper.GetDisplayBytes(_samplePageImagePath);
            if (bytes == null) return;
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            PageImage = bitmap;
            PageImageSize = new Avalonia.Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            SchedulePreviewUpdate();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load sample page: {ex.Message}";
        }
    }

    private void LoadLogoPreview()
    {
        if (string.IsNullOrEmpty(LogoImagePath) || !File.Exists(LogoImagePath)) return;
        try
        {
            LogoPreviewBitmap = new Bitmap(LogoImagePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WatermarkEditorViewModel] Could not load logo preview: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PickLogoFileAsync(Window owner)
    {
        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel?.StorageProvider is not { } storageProvider) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose logo image",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } } }
        });
        var picked = files.FirstOrDefault()?.TryGetLocalPath();
        if (picked == null) return;

        try
        {
            Directory.CreateDirectory(WatermarkAssetPaths.DirectoryFor());
            var ext = Path.GetExtension(picked);
            var target = WatermarkAssetPaths.FileFor(_presetId, ext);
            File.Copy(picked, target, overwrite: true);
            var bitmap = new Bitmap(target); // decode first — don't commit LogoImagePath/preview if this throws
            LogoImagePath = target;
            LogoPreviewBitmap?.Dispose();
            LogoPreviewBitmap = bitmap;
            StatusText = string.Empty;
            SchedulePreviewUpdate();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load logo image ({Path.GetFileName(picked)}): {ex.Message}";
            Console.Error.WriteLine($"[WatermarkEditorViewModel] Logo load failed for '{picked}': {ex}");
        }
    }

    private void SchedulePreviewUpdate()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RenderPreview()
    {
        if (string.IsNullOrEmpty(_samplePageImagePath)) return;

        var snapshot = BuildTransientPreset();

        Task.Run(() =>
        {
            var bytes = WatermarkPreviewRenderer.RenderPreview(_samplePageImagePath, snapshot);
            if (bytes == null) return;
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new Bitmap(ms);
                    var old = PreviewImage;
                    PreviewImage = bitmap;
                    old?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WatermarkEditorViewModel] Preview decode failed: {ex}");
                }
            });
        });
    }

    private WatermarkPreset BuildTransientPreset() => new()
    {
        Id = _presetId,
        Name = PresetName,
        WatermarkType = WatermarkType,
        TextContent = TextContent,
        FontSize = FontSize,
        TextColor = TextColor,
        LogoImagePath = LogoImagePath,
        X = Transform.X,
        Y = Transform.Y,
        Width = Transform.Width,
        Height = Transform.Height,
        RotationDegrees = Transform.RotationDegrees,
        Opacity = Opacity
    };

    private bool CanSave() =>
        !string.IsNullOrWhiteSpace(PresetName) &&
        (IsTextMode ? !string.IsNullOrWhiteSpace(TextContent) : !string.IsNullOrEmpty(LogoImagePath));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        try
        {
            var preset = _isNewPreset ? null : await _dbContext.WatermarkPresets.FirstOrDefaultAsync(p => p.Id == _presetId);
            if (preset == null)
            {
                preset = new WatermarkPreset { Id = _presetId };
                _dbContext.WatermarkPresets.Add(preset);
            }

            preset.Name = PresetName;
            preset.WatermarkType = WatermarkType;
            preset.TextContent = TextContent;
            preset.FontSize = FontSize;
            preset.TextColor = TextColor;
            preset.LogoImagePath = LogoImagePath;
            preset.X = Transform.X;
            preset.Y = Transform.Y;
            preset.Width = Transform.Width;
            preset.Height = Transform.Height;
            preset.RotationDegrees = Transform.RotationDegrees;
            preset.Opacity = Opacity;

            await _dbContext.SaveChangesAsync();
            Result = preset;
            // The preview holds a downscaled copy of the sample page; drop it rather than
            // keeping it alive for the rest of the session.
            WatermarkPreviewRenderer.ClearSampleCache();
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save watermark preset: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        WatermarkPreviewRenderer.ClearSampleCache();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }
}
