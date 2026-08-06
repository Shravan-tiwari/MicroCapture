using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Models;
using MicroCapture.Processing;

namespace MicroCapture.UI.ViewModels;

/// <summary>Which part of a fixed-frame rectangle a drag is resizing, or <see cref="Move"/> to
/// translate the whole rectangle. Frames are always axis-aligned (no perspective correction
/// needed for a stationary, straight-down copy-stand shot), so resizing is plain
/// min/max/clamp arithmetic — no convexity checks like CropReviewViewModel's quads need.</summary>
public enum FrameHandleKind { Move, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }

/// <summary>Lets the operator define one or more fixed crop rectangles once (at Start Batch, or
/// later via "Calibrate Frames"), reused for every capture in the batch instead of per-shot
/// auto-crop detection. Used for both first-time calibration and recalibration — seeded from
/// the batch's existing frames when it already has some.</summary>
public partial class FrameCalibrationViewModel : ViewModelBase
{
    private const double MinFrameSize = 20.0;

    private readonly Batch _batch;
    private readonly AppDbContext _dbContext;

    /// <summary>Raised after a successful Save, before the window closes.</summary>
    public event EventHandler? Saved;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private int _selectedFrameIndex = -1;

    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    public ObservableCollection<FixedFrameRect> Frames { get; } = new();

    public FrameCalibrationViewModel()
    {
        // Design-time constructor.
        _batch = null!;
        _dbContext = null!;
    }

    public FrameCalibrationViewModel(Batch batch, AppDbContext dbContext, string imagePath)
    {
        _batch = batch;
        _dbContext = dbContext;

        Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(imagePath);
                var bmp = new Bitmap(stream);
                var width = (int)bmp.Size.Width;
                var height = (int)bmp.Size.Height;

                FixedFrameRect[] seed;
                if (batch.UseFixedFrames && !string.IsNullOrWhiteSpace(batch.FixedFrames))
                {
                    // Recalibration: re-project the batch's existing frames onto this new
                    // calibration image, in case its resolution differs from the original.
                    var existing = ImageProcessor.ParseFixedFrames(batch.FixedFrames!);
                    var sx = batch.FixedFrameImageWidth > 0 ? (double)width / batch.FixedFrameImageWidth : 1.0;
                    var sy = batch.FixedFrameImageHeight > 0 ? (double)height / batch.FixedFrameImageHeight : 1.0;
                    seed = existing.Select(f => new FixedFrameRect(f.X * sx, f.Y * sy, f.Width * sx, f.Height * sy)).ToArray();
                }
                else
                {
                    seed = new[] { DefaultFrame(width, height) };
                }

                Dispatcher.UIThread.Post(() =>
                {
                    Image = bmp;
                    ImageWidth = width;
                    ImageHeight = height;
                    Frames.Clear();
                    foreach (var f in seed) Frames.Add(f);
                    SelectedFrameIndex = Frames.Count > 0 ? 0 : -1;
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FrameCalibrationViewModel] Failed to load '{imagePath}': {ex}");
            }
        });
    }

    private static FixedFrameRect DefaultFrame(int imageWidth, int imageHeight)
    {
        var w = imageWidth * 0.4;
        var h = imageHeight * 0.4;
        return new FixedFrameRect((imageWidth - w) / 2, (imageHeight - h) / 2, w, h);
    }

    [RelayCommand]
    private void AddFrame()
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return;

        var w = ImageWidth * 0.3;
        var h = ImageHeight * 0.3;
        // Stagger each new default frame so repeated Add clicks don't stack exactly on top
        // of each other and become unreachable.
        var offset = (Frames.Count % 5) * 24;
        var x = Math.Clamp(ImageWidth * 0.08 + offset, 0, Math.Max(0, ImageWidth - w));
        var y = Math.Clamp(ImageHeight * 0.08 + offset, 0, Math.Max(0, ImageHeight - h));

        Frames.Add(new FixedFrameRect(x, y, w, h));
        SelectedFrameIndex = Frames.Count - 1;
    }

    [RelayCommand]
    private void RemoveFrame()
    {
        if (SelectedFrameIndex < 0 || SelectedFrameIndex >= Frames.Count) return;
        Frames.RemoveAt(SelectedFrameIndex);
        SelectedFrameIndex = Frames.Count > 0 ? Math.Min(SelectedFrameIndex, Frames.Count - 1) : -1;
    }

    [RelayCommand]
    private void Reset()
    {
        if (ImageWidth <= 0 || ImageHeight <= 0) return;
        Frames.Clear();
        Frames.Add(DefaultFrame(ImageWidth, ImageHeight));
        SelectedFrameIndex = 0;
    }

    [RelayCommand]
    private void Cancel(Window window) => window?.Close();

    [RelayCommand]
    private async Task Save(Window window)
    {
        if (Frames.Count > 0 && ImageWidth > 0 && ImageHeight > 0)
        {
            _batch.UseFixedFrames = true;
            _batch.FixedFrames = ImageProcessor.FormatFixedFrames(Frames);
            _batch.FixedFrameImageWidth = ImageWidth;
            _batch.FixedFrameImageHeight = ImageHeight;
            await _dbContext.SaveChangesAsync();
            Saved?.Invoke(this, EventArgs.Empty);
        }

        window?.Close();
    }

    /// <summary>Resizes <paramref name="current"/> from the given handle to the current pointer
    /// position (absolute, not delta-based — matches CropReviewWindow's own drag model). Clamps
    /// to image bounds and a minimum size so a frame can never collapse to zero or escape the
    /// image entirely.</summary>
    public FixedFrameRect ResolveFrameResize(FixedFrameRect current, FrameHandleKind handle, double pointerX, double pointerY)
    {
        double x = current.X, y = current.Y;
        double right = current.X + current.Width, bottom = current.Y + current.Height;

        switch (handle)
        {
            case FrameHandleKind.TopLeft:
                x = Math.Min(pointerX, right - MinFrameSize);
                y = Math.Min(pointerY, bottom - MinFrameSize);
                break;
            case FrameHandleKind.Top:
                y = Math.Min(pointerY, bottom - MinFrameSize);
                break;
            case FrameHandleKind.TopRight:
                right = Math.Max(pointerX, x + MinFrameSize);
                y = Math.Min(pointerY, bottom - MinFrameSize);
                break;
            case FrameHandleKind.Left:
                x = Math.Min(pointerX, right - MinFrameSize);
                break;
            case FrameHandleKind.Right:
                right = Math.Max(pointerX, x + MinFrameSize);
                break;
            case FrameHandleKind.BottomLeft:
                x = Math.Min(pointerX, right - MinFrameSize);
                bottom = Math.Max(pointerY, y + MinFrameSize);
                break;
            case FrameHandleKind.Bottom:
                bottom = Math.Max(pointerY, y + MinFrameSize);
                break;
            case FrameHandleKind.BottomRight:
                right = Math.Max(pointerX, x + MinFrameSize);
                bottom = Math.Max(pointerY, y + MinFrameSize);
                break;
        }

        x = Math.Clamp(x, 0, Math.Max(0, ImageWidth - MinFrameSize));
        y = Math.Clamp(y, 0, Math.Max(0, ImageHeight - MinFrameSize));
        right = Math.Clamp(right, x + MinFrameSize, ImageWidth);
        bottom = Math.Clamp(bottom, y + MinFrameSize, ImageHeight);

        return new FixedFrameRect(x, y, right - x, bottom - y);
    }

    /// <summary>Translates <paramref name="current"/> by the given pointer-space delta, clamped
    /// so the frame stays fully within the image.</summary>
    public FixedFrameRect MoveFrame(FixedFrameRect current, double deltaX, double deltaY)
    {
        var x = Math.Clamp(current.X + deltaX, 0, Math.Max(0, ImageWidth - current.Width));
        var y = Math.Clamp(current.Y + deltaY, 0, Math.Max(0, ImageHeight - current.Height));
        return new FixedFrameRect(x, y, current.Width, current.Height);
    }
}
