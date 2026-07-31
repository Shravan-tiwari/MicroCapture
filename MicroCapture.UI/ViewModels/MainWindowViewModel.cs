using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Interfaces;

namespace MicroCapture.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICameraService _cameraService;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private Bitmap? _liveViewImage;

    [ObservableProperty]
    private bool _isConnected;

    public MainWindowViewModel()
    {
        // Design-time constructor
    }

    public MainWindowViewModel(ICameraService cameraService)
    {
        _cameraService = cameraService;
        _cameraService.StateChanged += (s, e) =>
        {
            IsConnected = e.IsConnected;
            StatusText = e.StatusMessage;
        };

        _cameraService.LiveViewFrameReceived += (s, frameBytes) =>
        {
            try
            {
                using var ms = new MemoryStream(frameBytes);
                var bitmap = new Bitmap(ms);
                // Assign to UI thread-bound property (Avalonia handles this nicely in v11+)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    LiveViewImage?.Dispose();
                    LiveViewImage = bitmap;
                });
            }
            catch (Exception ex)
            {
                StatusText = "Error decoding Live View frame: " + ex.Message;
            }
        };
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        StatusText = "Connecting...";
        await _cameraService.ConnectAsync("mock-1");
        await _cameraService.StartLiveViewAsync();
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (!IsConnected) return;

        StatusText = "Capturing...";
        var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MicroCapture");
        try
        {
            var file = await _cameraService.CaptureAsync(outDir, "IMG");
            StatusText = $"Captured: {Path.GetFileName(file)}";
        }
        catch (Exception ex)
        {
            StatusText = "Capture failed: " + ex.Message;
        }
    }
}
