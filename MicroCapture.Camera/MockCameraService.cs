using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MicroCapture.Core.Interfaces;
using SkiaSharp;

namespace MicroCapture.Camera;

public class MockCameraService : ICameraService
{
    private bool _isConnected;
    private bool _isLiveViewRunning;
    private CancellationTokenSource? _liveViewCts;
    private int _frameCount = 0;

    public bool IsConnected => _isConnected;

    public event EventHandler<CameraStateEventArgs>? StateChanged;
    public event EventHandler<byte[]>? LiveViewFrameReceived;

    public Task<IEnumerable<CameraDeviceInfo>> GetConnectedCamerasAsync()
    {
        return Task.FromResult<IEnumerable<CameraDeviceInfo>>(new[]
        {
            new CameraDeviceInfo
            {
                Id = "mock-cam-1",
                Model = "Mock EOS R8",
                SerialNumber = "123456789"
            }
        });
    }

    public async Task<bool> ConnectAsync(string cameraId)
    {
        await Task.Delay(500); // Simulate connection delay
        _isConnected = true;
        StateChanged?.Invoke(this, new CameraStateEventArgs { IsConnected = true, StatusMessage = "Connected to Mock Camera" });
        return true;
    }

    public async Task DisconnectAsync()
    {
        if (_isLiveViewRunning)
        {
            await StopLiveViewAsync();
        }
        await Task.Delay(200);
        _isConnected = false;
        StateChanged?.Invoke(this, new CameraStateEventArgs { IsConnected = false, StatusMessage = "Disconnected" });
    }

    public Task StartLiveViewAsync()
    {
        if (!_isConnected) throw new InvalidOperationException("Camera not connected.");
        if (_isLiveViewRunning) return Task.CompletedTask;

        _isLiveViewRunning = true;
        _liveViewCts = new CancellationTokenSource();
        var token = _liveViewCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var frame = GenerateMockFrame("LIVE VIEW\nFrame: " + _frameCount++, SKColors.DarkSlateGray);
                LiveViewFrameReceived?.Invoke(this, frame);
                await Task.Delay(33, token); // ~30fps
            }
        }, token);

        return Task.CompletedTask;
    }

    public Task StopLiveViewAsync()
    {
        _isLiveViewRunning = false;
        _liveViewCts?.Cancel();
        _liveViewCts?.Dispose();
        _liveViewCts = null;
        return Task.CompletedTask;
    }

    public async Task<string> CaptureAsync(string outputDirectory, string fileNamePrefix)
    {
        if (!_isConnected) throw new InvalidOperationException("Camera not connected.");

        // Simulate capture delay
        await Task.Delay(800); 

        var fileName = $"{fileNamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        var filePath = Path.Combine(outputDirectory, fileName);
        
        Directory.CreateDirectory(outputDirectory);

        var imageData = GenerateMockFrame("MOCK CAPTURE\n" + fileName, SKColors.DarkBlue, width: 3840, height: 2160);
        await File.WriteAllBytesAsync(filePath, imageData);

        return filePath;
    }

    private byte[] GenerateMockFrame(string text, SKColor bgColor, int width = 640, int height = 480)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        
        canvas.Clear(bgColor);
        
        using var font = new SKFont();
        font.Size = width / 15f;
        
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        var lines = text.Split('\n');
        float y = height / 2f - (lines.Length * font.Size) / 2f;
        foreach (var line in lines)
        {
            float textWidth = font.MeasureText(line);
            canvas.DrawText(line, (width / 2f) - (textWidth / 2f), y, font, paint);
            y += font.Size + 10;
        }

        // Draw a simulated document rectangle
        using var rectPaint = new SKPaint
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5,
            IsAntialias = true
        };
        var inset = width / 10f;
        canvas.DrawRect(inset, inset, width - inset * 2, height - inset * 2, rectPaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    public void Dispose()
    {
        _liveViewCts?.Cancel();
        _liveViewCts?.Dispose();
    }
}
