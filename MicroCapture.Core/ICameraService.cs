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

    /// <summary>True only once live view is genuinely streaming — frames actually arriving, not
    /// merely "start was requested". The distinction matters because the EVF focus commands below
    /// are rejected by the camera until the EVF pipe is really up, and StartLiveViewAsync returns
    /// before that happens (the camera's own state machine takes a moment, and CaptureAsync
    /// restarts live view fire-and-forget). Callers gate focus controls on this, not IsConnected.</summary>
    bool IsLiveViewActive { get; }
    event EventHandler<bool>? LiveViewActiveChanged;

    // Camera configuration. Services return only properties supported by the
    // connected body/lens; callers must not assume a fixed set of values.
    Task<IReadOnlyList<CameraSetting>> GetCameraSettingsAsync();
    Task SetCameraSettingAsync(string settingKey, uint value);

    // Manual focus control during live view — operator-driven, since AF mode alone (One-Shot/
    // AI Servo) gives no way to nudge or force a refocus on demand. BOTH require live view to be
    // actively streaming (see IsLiveViewActive) and throw if it isn't: the camera rejects EVF
    // focus commands otherwise, and silently swallowing that produced a long-standing "focus
    // doesn't work" bug where the UI reported success while nothing moved. Implementations that
    // can't drive a real lens (e.g. a body-less test double) must still enforce that precondition
    // rather than no-opping unconditionally, or they mask the failure in dev.
    Task NudgeFocusAsync(FocusStep step);
    Task TriggerAutoFocusAsync();

    // Capture
    Task<string> CaptureAsync(string outputDirectory, string fileNamePrefix);
}
