using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MicroCapture.Core.Interfaces;

public class CameraDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
}

public class CameraStateEventArgs : EventArgs
{
    public bool IsConnected { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

public interface ICameraService : IDisposable
{
    // Connection
    Task<IEnumerable<CameraDeviceInfo>> GetConnectedCamerasAsync();
    Task<bool> ConnectAsync(string cameraId);
    Task DisconnectAsync();

    // State
    bool IsConnected { get; }
    event EventHandler<CameraStateEventArgs>? StateChanged;

    // Live View
    Task StartLiveViewAsync();
    Task StopLiveViewAsync();
    event EventHandler<byte[]>? LiveViewFrameReceived;

    // Capture
    Task<string> CaptureAsync(string outputDirectory, string fileNamePrefix);
}
