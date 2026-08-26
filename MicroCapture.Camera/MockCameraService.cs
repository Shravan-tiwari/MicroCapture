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
    private readonly Dictionary<string, uint> _settings = new()
    {
        ["AEMode"] = 3, ["Tv"] = 0x60, ["Av"] = 0x38, ["ISO"] = 0x48,
        ["ExposureCompensation"] = 0x00, ["WhiteBalance"] = 0, ["ImageQuality"] = 0x13ff,
        ["DriveMode"] = 0, ["AFMode"] = 0
    };
    private bool _isConnected;
    private bool _isLiveViewRunning;
    private CancellationTokenSource? _liveViewCts;
    private int _frameCount = 0;

    public bool IsConnected => _isConnected;
    public bool IsLiveViewActive => _isLiveViewRunning;

    public event EventHandler<CameraStateEventArgs>? StateChanged;
    public event EventHandler<byte[]>? LiveViewFrameReceived;
    public event EventHandler<bool>? LiveViewActiveChanged;

    public Task<IReadOnlyList<CameraSetting>> GetCameraSettingsAsync() =>
        Task.FromResult<IReadOnlyList<CameraSetting>>(CreateSettings());

    public Task SetCameraSettingAsync(string settingKey, uint value)
    {
        if (!_isConnected) throw new InvalidOperationException("Camera not connected.");
        if (!_settings.ContainsKey(settingKey)) throw new ArgumentException($"Unsupported camera setting: {settingKey}");
        _settings[settingKey] = value;
        return Task.CompletedTask;
    }

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
        LiveViewActiveChanged?.Invoke(this, true);
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
        var wasRunning = _isLiveViewRunning;
        _isLiveViewRunning = false;
        if (wasRunning) LiveViewActiveChanged?.Invoke(this, false);
        _liveViewCts?.Cancel();
        _liveViewCts?.Dispose();
        _liveViewCts = null;
        return Task.CompletedTask;
    }

    // No real lens to move, but the live-view precondition IS modelled deliberately. These used
    // to no-op on connection alone, which meant the focus controls always "worked" in dev against
    // the mock while failing on real hardware — that masked a long-standing bug where the camera
    // rejected EVF focus commands because live view wasn't up. Keep this guard in step with
    // CanonCameraService.ThrowIfLiveViewInactive.
    public Task NudgeFocusAsync(FocusStep step)
    {
        if (!_isConnected) throw new InvalidOperationException("Camera not connected.");
        ThrowIfLiveViewInactive("Focus");
        return Task.CompletedTask;
    }

    public Task TriggerAutoFocusAsync()
    {
        if (!_isConnected) throw new InvalidOperationException("Camera not connected.");
        ThrowIfLiveViewInactive("Autofocus");
        return Task.CompletedTask;
    }

    private void ThrowIfLiveViewInactive(string action)
    {
        if (_isLiveViewRunning) return;
        throw new InvalidOperationException($"{action} needs live view running. Wait for the preview to appear, then try again.");
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
            canvas.DrawText(line, width / 2f, y, SKTextAlign.Center, font, paint);
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

    private IReadOnlyList<CameraSetting> CreateSettings() => new[]
    {
        Setting("AEMode", "Exposure mode", (3, "Manual"), (0, "Program"), (1, "Tv"), (2, "Av")),
        Setting("Tv", "Shutter speed", (0x60, "1/125"), (0x58, "1/60"), (0x68, "1/250")),
        Setting("Av", "Aperture", (0x38, "f/5.6"), (0x30, "f/4"), (0x40, "f/8")),
        Setting("ISO", "ISO", (0, "Auto"), (0x48, "100"), (0x50, "200"), (0x58, "400"), (0x60, "800")),
        Setting("ExposureCompensation", "Exposure compensation", (0, "0"), (0x08, "+1"), (unchecked((uint)-8), "-1")),
        Setting("WhiteBalance", "White balance", (0, "Auto"), (1, "Daylight"), (3, "Cloudy"), (6, "Tungsten")),
        Setting("ImageQuality", "Image quality", (0x13ff, "JPEG Large")),
        Setting("DriveMode", "Drive mode", (0, "Single"), (1, "Continuous")),
        Setting("AFMode", "Focus mode", (0, "One-shot"), (1, "AI Servo"))
    };

    private CameraSetting Setting(string key, string name, params (uint Value, string Name)[] options) => new()
    {
        Key = key, DisplayName = name, Value = _settings[key],
        Options = options.Select(option => new CameraSettingOption { Value = option.Value, DisplayName = option.Name }).ToArray()
    };
}
