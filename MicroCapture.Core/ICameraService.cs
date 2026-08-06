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

/// <summary>One camera property which can safely be surfaced in the operator UI.</summary>
public sealed class CameraSetting
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public uint Value { get; set; }
    public IReadOnlyList<CameraSettingOption> Options { get; init; } = Array.Empty<CameraSettingOption>();
}

public sealed class CameraSettingOption
{
    public uint Value { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>Manual focus-drive step size, mirroring EDSDK's EvfDriveLens near/far increments
/// (used during live view, the same mechanism EOS Utility's remote-focus arrows use).</summary>
public enum FocusStep { NearSmall, NearMedium, NearLarge, FarSmall, FarMedium, FarLarge }

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

    // Camera configuration. Services return only properties supported by the
    // connected body/lens; callers must not assume a fixed set of values.
    Task<IReadOnlyList<CameraSetting>> GetCameraSettingsAsync();
    Task SetCameraSettingAsync(string settingKey, uint value);

    // Manual focus control during live view — operator-driven, since AF mode alone (One-Shot/
    // AI Servo) gives no way to nudge or force a refocus on demand. Implementations that can't
    // support this (e.g. a body-less test double) should treat these as safe no-ops.
    Task NudgeFocusAsync(FocusStep step);
    Task TriggerAutoFocusAsync();

    // Capture
    Task<string> CaptureAsync(string outputDirectory, string fileNamePrefix);
}
