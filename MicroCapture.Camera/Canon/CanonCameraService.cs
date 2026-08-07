using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EDSDKLib;
using MicroCapture.Core.Interfaces;

namespace MicroCapture.Camera.Canon;

/// <summary>EDSDK-backed camera service. All native calls are traced and native references are released deterministically.</summary>
public sealed class CanonCameraService : ICameraService, IDisposable
{
    private sealed record CameraPropertyDefinition(string Key, string DisplayName, uint PropertyId);

    // These are the capture controls supported by Canon EDSDK. The actual values
    // are discovered from the connected camera, which prevents offering a lens or
    // body setting that cannot be applied.
    private static readonly CameraPropertyDefinition[] OperatorProperties =
    {
        // PropID_AEMode (0x400) is the legacy read-only "current mode" property. Modern EOS
        // bodies (R-series) need PropID_AEModeSelect (0x436) to actually change it — using the
        // old one is likely why this control didn't even appear for the R8 (GetCameraSettingsAsync
        // silently skips any property EdsGetPropertyDesc can't describe).
        new("AEMode", "Exposure mode", EDSDK.PropID_AEModeSelect),
        new("Tv", "Shutter speed", EDSDK.PropID_Tv),
        new("Av", "Aperture", EDSDK.PropID_Av),
        new("ISO", "ISO", EDSDK.PropID_ISOSpeed),
        new("ExposureCompensation", "Exposure compensation", EDSDK.PropID_ExposureCompensation),
        new("WhiteBalance", "White balance", EDSDK.PropID_WhiteBalance),
        new("ImageQuality", "Image quality", EDSDK.PropID_ImageQuality),
        new("DriveMode", "Drive mode", EDSDK.PropID_DriveMode),
        new("AFMode", "Focus mode", EDSDK.PropID_AFMode)
    };
    private static readonly object LogSync = new();
    private static DateTime _lastLiveSuccessLogUtc = DateTime.MinValue;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    // Serializes the Connect/Disconnect/Dispose lifecycle as a whole. _sync (a plain Monitor)
    // can't be held across an await, so without this, EdsTerminateSDK()/EdsCloseSession() in
    // Dispose()/DisconnectAsync() could run concurrently with an in-flight ConnectAsync's own
    // native calls — the SDK gets torn down mid-use, which is an unrecoverable native crash no
    // managed try/catch can stop. SemaphoreSlim has no thread affinity, so it's safe to hold
    // across the ConfigureAwait(false) continuations this lifecycle code already relies on.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private IntPtr _camera;
    private bool _isConnected;
    private bool _sdkInitialized;
    private bool _disposed;
    private bool _isDownloading;
    private CancellationTokenSource? _liveViewCts;
    private Task? _liveViewTask;
    private TaskCompletionSource<string>? _captureTcs;
    private bool _captureExpectsRawOnly;
    private string _currentSaveDirectory = string.Empty;
    private string _currentFilePrefix = string.Empty;
    private uint _previousEvfOutputDevice;
    private bool _hasPreviousEvfOutputDevice;

    // Delegates must remain rooted for as long as the EDSDK session is open.
    private EDSDK.EdsObjectEventHandler? _objectEventHandler;
    private EDSDK.EdsPropertyEventHandler? _propertyEventHandler;
    private EDSDK.EdsStateEventHandler? _stateEventHandler;

    public bool IsConnected { get { lock (_sync) return _isConnected; } }
    public event EventHandler<CameraStateEventArgs>? StateChanged;
    public event EventHandler<byte[]>? LiveViewFrameReceived;

    public Task<IReadOnlyList<CameraSetting>> GetCameraSettingsAsync()
    {
        var settings = new List<CameraSetting>();
        lock (_sync)
        {
            ThrowIfDisconnected();
            foreach (var definition in OperatorProperties)
            {
                try
                {
                    uint current = 0;
                    if (!Succeeded(Call("EdsGetPropertyData", () => EDSDK.EdsGetPropertyData(_camera, definition.PropertyId, 0, out current), definition.Key)))
                        continue;
                    var description = default(EDSDK.EdsPropertyDesc);
                    if (!Succeeded(Call("EdsGetPropertyDesc", () => EDSDK.EdsGetPropertyDesc(_camera, definition.PropertyId, out description), definition.Key)) ||
                        description.NumElements <= 0 || description.PropDesc == null)
                        continue;

                    var options = description.PropDesc
                        .Take(Math.Min(description.NumElements, description.PropDesc.Length))
                        .Select(value => unchecked((uint)value))
                        .Distinct()
                        .Select(value => new CameraSettingOption { Value = value, DisplayName = FormatCameraValue(definition.Key, value) })
                        .ToArray();
                    if (options.Length > 0)
                        settings.Add(new CameraSetting { Key = definition.Key, DisplayName = definition.DisplayName, Value = current, Options = options });
                }
                catch (Exception ex)
                {
                    Log("GetCameraSettingsAsync", $"Skipped {definition.Key}: {ex.Message}");
                }
            }
        }
        return Task.FromResult<IReadOnlyList<CameraSetting>>(settings);
    }

    public Task SetCameraSettingAsync(string settingKey, uint value)
    {
        var definition = OperatorProperties.FirstOrDefault(property => property.Key == settingKey)
            ?? throw new ArgumentException($"Unsupported camera setting: {settingKey}", nameof(settingKey));
        lock (_sync)
        {
            ThrowIfDisconnected();
            EnsureSuccess($"EdsSetPropertyData({definition.Key})", Call($"EdsSetPropertyData({definition.Key})", () =>
                EDSDK.EdsSetPropertyData(_camera, definition.PropertyId, 0, sizeof(uint), value), $"value=0x{value:X8}"));
        }
        return Task.CompletedTask;
    }

    public CanonCameraService()
    {
        if (!IsWindows())
        {
            Log("EDSDK", "CanonCameraService is unavailable outside Windows.");
            return;
        }

        try
        {
            var error = Call("EdsInitializeSDK", EDSDK.EdsInitializeSDK);
            _sdkInitialized = error == EDSDK.EDS_ERR_OK;
        }
        catch (DllNotFoundException ex)
        {
            Log("EDSDK", $"EDSDK.dll could not be loaded: {ex}");
        }
        catch (Exception ex)
        {
            Log("EDSDK", $"SDK initialization threw: {ex}");
        }
    }

    public Task<IEnumerable<CameraDeviceInfo>> GetConnectedCamerasAsync()
    {
        var cameras = new List<CameraDeviceInfo>();
        if (!_sdkInitialized) return Task.FromResult<IEnumerable<CameraDeviceInfo>>(cameras);

        lock (_sync)
        {
            IntPtr cameraList = IntPtr.Zero;
            try
            {
                if (!Succeeded(Call("EdsGetCameraList", () => EDSDK.EdsGetCameraList(out cameraList))) || cameraList == IntPtr.Zero)
                    return Task.FromResult<IEnumerable<CameraDeviceInfo>>(cameras);

                var count = 0;
                if (!Succeeded(Call("EdsGetChildCount", () => EDSDK.EdsGetChildCount(cameraList, out count), $"cameraList=0x{cameraList.ToInt64():X}")))
                    return Task.FromResult<IEnumerable<CameraDeviceInfo>>(cameras);

                for (var i = 0; i < count; i++)
                {
                    IntPtr camera = IntPtr.Zero;
                    try
                    {
                        if (!Succeeded(Call("EdsGetChildAtIndex", () => EDSDK.EdsGetChildAtIndex(cameraList, i, out camera), $"index={i}")) || camera == IntPtr.Zero)
                            continue;
                        var info = default(EDSDK.EdsDeviceInfo);
                        if (Succeeded(Call("EdsGetDeviceInfo", () => EDSDK.EdsGetDeviceInfo(camera, out info))))
                            cameras.Add(new CameraDeviceInfo { Id = info.szPortName, Model = info.szDeviceDescription });
                    }
                    finally { Release(camera, "camera discovery item"); }
                }
            }
            catch (Exception ex) { Log("GetConnectedCamerasAsync", ex.ToString()); }
            finally { Release(cameraList, "camera list"); }
        }
        return Task.FromResult<IEnumerable<CameraDeviceInfo>>(cameras);
    }

    public async Task<bool> ConnectAsync(string cameraId)
    {
        if (!_sdkInitialized) return false;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            return ConnectCore(cameraId);
        }
        finally { _lifecycleLock.Release(); }
    }

    private bool ConnectCore(string cameraId)
    {
        IntPtr list = IntPtr.Zero;
        IntPtr selected = IntPtr.Zero;
        try
        {
            lock (_sync)
            {
                if (!Succeeded(Call("EdsGetCameraList", () => EDSDK.EdsGetCameraList(out list))) || list == IntPtr.Zero) return false;
                var count = 0;
                if (!Succeeded(Call("EdsGetChildCount", () => EDSDK.EdsGetChildCount(list, out count))) || count == 0) return false;
                for (var i = 0; i < count; i++)
                {
                    IntPtr candidate = IntPtr.Zero;
                    if (!Succeeded(Call("EdsGetChildAtIndex", () => EDSDK.EdsGetChildAtIndex(list, i, out candidate), $"index={i}"))) continue;
                    var info = default(EDSDK.EdsDeviceInfo);
                    var matches = Succeeded(Call("EdsGetDeviceInfo", () => EDSDK.EdsGetDeviceInfo(candidate, out info))) &&
                                  (string.IsNullOrEmpty(cameraId) || string.Equals(info.szPortName, cameraId, StringComparison.OrdinalIgnoreCase));
                    if (matches) { selected = candidate; break; }
                    Release(candidate, "unselected camera");
                }
                if (selected == IntPtr.Zero || !Succeeded(Call("EdsOpenSession", () => EDSDK.EdsOpenSession(selected)))) return false;

                _objectEventHandler = ObjectEventHandler;
                _propertyEventHandler = PropertyEventHandler;
                _stateEventHandler = StateEventHandler;
                if (!Succeeded(Call("EdsSetObjectEventHandler", () => EDSDK.EdsSetObjectEventHandler(selected, EDSDK.ObjectEvent_All, _objectEventHandler, IntPtr.Zero))) ||
                    !Succeeded(Call("EdsSetPropertyEventHandler", () => EDSDK.EdsSetPropertyEventHandler(selected, EDSDK.PropertyEvent_All, _propertyEventHandler, IntPtr.Zero))) ||
                    !Succeeded(Call("EdsSetCameraStateEventHandler", () => EDSDK.EdsSetCameraStateEventHandler(selected, EDSDK.StateEvent_All, _stateEventHandler, IntPtr.Zero))))
                {
                    Call("EdsCloseSession", () => EDSDK.EdsCloseSession(selected));
                    return false;
                }

                uint saveTo = (uint)EDSDK.EdsSaveTo.Host;
                var capacity = new EDSDK.EdsCapacity { NumberOfFreeClusters = int.MaxValue, BytesPerSector = 512, Reset = 1 };
                if (!Succeeded(Call("EdsSetPropertyData(SaveTo=Host)", () => EDSDK.EdsSetPropertyData(selected, EDSDK.PropID_SaveTo, 0, sizeof(uint), saveTo))) ||
                    !Succeeded(Call("EdsSetCapacity", () => EDSDK.EdsSetCapacity(selected, capacity), "clusters=2147483647, bytesPerSector=512, reset=1")))
                {
                    Call("EdsCloseSession", () => EDSDK.EdsCloseSession(selected));
                    return false;
                }
                _camera = selected;
                selected = IntPtr.Zero;
                _isConnected = true;
            }
            PublishState(true, "Canon camera connected");
            return true;
        }
        catch (Exception ex) { Log("ConnectAsync", ex.ToString()); return false; }
        finally { Release(list, "camera list"); Release(selected, "failed connection camera"); }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        Console.WriteLine("[Disconnect] Started");

        await StopLiveViewAsync().ConfigureAwait(false);
        Console.WriteLine("[Disconnect] LiveView stopped");

        IntPtr camera;
        lock (_sync)
        {
            camera = _camera;
            _camera = IntPtr.Zero;
            _isConnected = false;
            _captureTcs?.TrySetException(new OperationCanceledException("Camera disconnected before capture completed."));
            _captureTcs = null;
        }

        Console.WriteLine($"[Disconnect] Camera ptr = 0x{camera.ToInt64():X}");

        if (camera != IntPtr.Zero)
        {
            Console.WriteLine("[Disconnect] Closing session");
            Call("EdsCloseSession", () => EDSDK.EdsCloseSession(camera));

            Console.WriteLine("[Disconnect] Releasing camera");
            Release(camera, "camera session");
        }

        Console.WriteLine("[Disconnect] Publishing state");
        PublishState(false, "Disconnected");

        Console.WriteLine("[Disconnect] Finished");
    }

    public Task StartLiveViewAsync()
    {
        CancellationToken token;
        lock (_sync)
        {
            ThrowIfDisconnected();
            if (_liveViewCts != null) return Task.CompletedTask;
            var camera = _camera;
            uint currentDevice = 0;
            var getResult = Call("EdsGetPropertyData(Evf_OutputDevice)", () => EDSDK.EdsGetPropertyData(camera, EDSDK.PropID_Evf_OutputDevice, 0, out currentDevice));
            _hasPreviousEvfOutputDevice = getResult == EDSDK.EDS_ERR_OK;
            _previousEvfOutputDevice = currentDevice;
            // Output-device flags are a bitmask. OR in the PC bit rather than replacing
            // the flags outright, so the camera's own LCD (TFT) stays lit alongside the
            // PC stream. Fall back to TFT|PC if the current flags couldn't be read.
            uint device = _hasPreviousEvfOutputDevice
                ? currentDevice | EDSDK.EvfOutputDevice_PC
                : EDSDK.EvfOutputDevice_TFT | EDSDK.EvfOutputDevice_PC;

            EnsureSuccess(
                "EdsSetPropertyData(Evf_OutputDevice=PC)",
                Call("EdsSetPropertyData(Evf_OutputDevice=PC)",
                    () => EDSDK.EdsSetPropertyData(
                        camera,
                        EDSDK.PropID_Evf_OutputDevice,
                        0,
                        sizeof(uint),
                        device)));
            _liveViewCts = new CancellationTokenSource();
            token = _liveViewCts.Token;
            _liveViewTask = Task.Run(() => LiveViewLoopAsync(token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopLiveViewAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync) { cts = _liveViewCts; task = _liveViewTask; _liveViewCts = null; _liveViewTask = null; }
        if (cts != null) cts.Cancel();
        if (task != null)
        {
            // Cancellation only takes effect between the loop's native-call blocks, which are
            // not themselves cancellable — a stalled camera could otherwise block this (and
            // whatever's waiting on it, e.g. CaptureAsync's pre-shutter sequence) indefinitely.
            // Time-box it: cancellation was already requested, so the loop will still exit on
            // its own once that in-flight native call returns, this just stops us waiting on it.
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { Log("StopLiveViewAsync", "Timed out waiting for the live-view loop to exit; proceeding anyway."); }
            catch (Exception ex) { Log("StopLiveViewAsync", ex.ToString()); }
        }
        cts?.Dispose();
        lock (_sync)
        {
            if (_camera != IntPtr.Zero)
            {
                uint device = _hasPreviousEvfOutputDevice ? _previousEvfOutputDevice : EDSDK.EvfOutputDevice_TFT;
                Call("EdsSetPropertyData(Evf_OutputDevice=restore)", () => EDSDK.EdsSetPropertyData(_camera, EDSDK.PropID_Evf_OutputDevice, 0, sizeof(uint), device));
                _hasPreviousEvfOutputDevice = false;
            }
        }
    }

    /// <summary>Manual lens focus nudge during live view — the same EDSDK mechanism EOS
    /// Utility's remote-focus arrows use. Requires live view to be active; the camera returns
    /// an error otherwise, which surfaces to the caller like any other SDK failure.</summary>
    public Task NudgeFocusAsync(FocusStep step)
    {
        // Never block the calling (UI) thread: a focus-drive command can legitimately take a
        // moment, and holding the caller (and _sync) synchronously would freeze the app for
        // that entire duration instead of just briefly pausing the live-view loop.
        return Task.Run(() =>
        {
            lock (_sync)
            {
                ThrowIfDisconnected();
                var param = step switch
                {
                    FocusStep.NearSmall => EDSDK.EvfDriveLens_Near1,
                    FocusStep.NearMedium => EDSDK.EvfDriveLens_Near2,
                    FocusStep.NearLarge => EDSDK.EvfDriveLens_Near3,
                    FocusStep.FarSmall => EDSDK.EvfDriveLens_Far1,
                    FocusStep.FarMedium => EDSDK.EvfDriveLens_Far2,
                    FocusStep.FarLarge => EDSDK.EvfDriveLens_Far3,
                    _ => EDSDK.EvfDriveLens_Near1
                };
                EnsureSuccess("EdsSendCommand(DriveLensEvf)", Call($"EdsSendCommand(DriveLensEvf, {step})", () => EDSDK.EdsSendCommand(_camera, EDSDK.CameraCommand_DriveLensEvf, unchecked((int)param))));
            }
        });
    }

    /// <summary>Fires one autofocus attempt on demand during live view, independent of capture —
    /// lets the operator force a refocus without it happening automatically as part of shooting.</summary>
    public Task TriggerAutoFocusAsync()
    {
        // See NudgeFocusAsync — AF hunting on a low-contrast page can take real time; this
        // must never run synchronously on the caller's thread.
        return Task.Run(() =>
        {
            lock (_sync)
            {
                ThrowIfDisconnected();
                EnsureSuccess("EdsSendCommand(DoEvfAf)", Call("EdsSendCommand(DoEvfAf)", () => EDSDK.EdsSendCommand(_camera, EDSDK.CameraCommand_DoEvfAf, 0)));
            }
        });
    }

    public async Task<string> CaptureAsync(string outputDirectory, string filePrefix)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(filePrefix)) throw new ArgumentException("An output directory and file prefix are required.");
        Directory.CreateDirectory(outputDirectory);
        await _captureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLiveViewAsync().ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);

            TaskCompletionSource<string> tcs;
            lock (_sync)
            {
                ThrowIfDisconnected();
                if (_captureTcs != null) throw new InvalidOperationException("A capture is already in progress.");
                _isDownloading = true;
                _currentSaveDirectory = outputDirectory;
                _currentFilePrefix = filePrefix;
                uint currentQuality = 0;
                Call("EdsGetPropertyData(ImageQuality)", () => EDSDK.EdsGetPropertyData(_camera, EDSDK.PropID_ImageQuality, 0, out currentQuality));
                _captureExpectsRawOnly = IsRawOnlyQuality(currentQuality);
                tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _captureTcs = tcs;
                var result = Call("EdsSendCommand(CameraCommand_TakePicture)", () => EDSDK.EdsSendCommand(_camera, EDSDK.CameraCommand_TakePicture, 0));
                if (result != EDSDK.EDS_ERR_OK) { _isDownloading = false; tcs.TrySetException(CreateSdkException("EdsSendCommand(CameraCommand_TakePicture)", result)); }
            }
            var resultPath = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            return resultPath;
        }
        finally
        {
            lock (_sync) { _captureTcs = null; _isDownloading = false; _captureExpectsRawOnly = false; }
            _ = StartLiveViewAsync();
            _captureLock.Release();
        }
    }

    private async Task LiveViewLoopAsync(CancellationToken token)
    {
        IntPtr stream = IntPtr.Zero, image = IntPtr.Zero;
        try
        {
            IntPtr initialCamera;
            lock (_sync) initialCamera = _camera;
            if (initialCamera == IntPtr.Zero) return;
            if (!Succeeded(Call("EdsCreateMemoryStream", () => EDSDK.EdsCreateMemoryStream(0, out stream))) ||
                !Succeeded(Call("EdsCreateEvfImageRef", () => EDSDK.EdsCreateEvfImageRef(stream, out image))))
            {
                PublishState(true, "Live View initialization failed — see EDSDK log.");
                return;
            }

            while (!token.IsCancellationRequested)
            {
                IntPtr camera;
                bool isDownloading;
                lock (_sync) { camera = _camera; isDownloading = _isDownloading; }
                if (camera == IntPtr.Zero) return;
                // Canon transfer and EVF download must not compete for the camera stream.
                if (isDownloading) { await Task.Delay(50, token).ConfigureAwait(false); continue; }

                // EDSDK is not safe to call concurrently from two threads — this loop runs on
                // its own background thread continuously, so its native calls must hold _sync
                // for their actual duration (not just the state snapshot above), or a
                // same-time call from elsewhere (e.g. NudgeFocusAsync/TriggerAutoFocusAsync,
                // both invoked while live view keeps running) races it and can hang or crash
                // the SDK. The event is raised after releasing the lock so an external handler
                // can never re-enter this service while it's held.
                uint result;
                byte[]? frameBytes = null;
                lock (_sync)
                {
                    result = CallLive("EdsDownloadEvfImage", () => EDSDK.EdsDownloadEvfImage(camera, image));
                    if (result == EDSDK.EDS_ERR_OK)
                    {
                        ulong length = 0; IntPtr pointer = IntPtr.Zero;
                        if (Succeeded(CallLive("EdsGetLength(EVF stream)", () => EDSDK.EdsGetLength(stream, out length))) && length is > 0 and <= int.MaxValue &&
                            Succeeded(CallLive("EdsGetPointer(EVF stream)", () => EDSDK.EdsGetPointer(stream, out pointer))) && pointer != IntPtr.Zero)
                        {
                            frameBytes = new byte[(int)length];
                            Marshal.Copy(pointer, frameBytes, 0, frameBytes.Length);
                        }
                    }
                }

                if (frameBytes != null) LiveViewFrameReceived?.Invoke(this, frameBytes);
                if (result != EDSDK.EDS_ERR_OK && result != EDSDK.EDS_ERR_OBJECT_NOTREADY)
                {
                    PublishState(true, $"Live View error: {ErrorName(result)} (0x{result:X8})");
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                await Task.Delay(33, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log("LiveViewLoopAsync", ex.ToString()); PublishState(true, "Live View stopped unexpectedly — see EDSDK log."); }
        finally { Release(image, "EVF image"); Release(stream, "EVF stream"); }
    }

    private uint ObjectEventHandler(uint inEvent, IntPtr inRef, IntPtr inContext)
    {
        Log("ObjectEvent", $"event=0x{inEvent:X8}, ref=0x{inRef.ToInt64():X}");
        if (inEvent != EDSDK.ObjectEvent_DirItemRequestTransfer || inRef == IntPtr.Zero) return EDSDK.EDS_ERR_OK;
        // EDSDK transfers ownership of this directory-item reference to the object
        // event handler. Canon's sample queues it directly and releases it after
        // EdsDownloadComplete; attempting EdsRetain here fails on the EOS R8.
        lock (_sync) _isDownloading = true;
        _ = Task.Run(() => DownloadImageAsync(inRef));
        return EDSDK.EDS_ERR_OK;
    }

    private async Task DownloadImageAsync(IntPtr dirItem)
    {
        try
        {
            // A body shooting RAW+JPEG (or CRAW+JPEG) fires one transfer event per file for
            // the same shutter press — two DownloadImageAsync calls launched back-to-back on
            // their own threads (see ObjectEventHandler). EDSDK is not safe to call
            // concurrently from two threads, so without serializing here, two simultaneous
            // EdsDownload calls can race and corrupt/truncate each other's file (this is what
            // produced a half-blank JPEG next to a perfectly fine CR3 for the same capture).
            // Holding _sync for the whole native-call section forces the second file's
            // download to simply wait its turn instead of racing the first.
            lock (_sync)
            {
                var tcs = _captureTcs;
                var directory = _currentSaveDirectory;
                var prefix = _currentFilePrefix;

                var info = default(EDSDK.EdsDirectoryItemInfo);
                EnsureSuccess("EdsGetDirectoryItemInfo", Call("EdsGetDirectoryItemInfo", () => EDSDK.EdsGetDirectoryItemInfo(dirItem, out info)));
                var extension = Path.GetExtension(info.szFileName);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";
                // A body set to RAW+JPEG fires a transfer event for each file from the same
                // shutter press, in no guaranteed order. Nothing downstream (ImageProcessor,
                // Crop Review, export) can read a RAW file — only the JPEG is ever a valid
                // capture result — so a RAW file is always downloaded and saved to disk, but
                // never allowed to resolve the pending capture. If only a RAW arrives (pure RAW
                // mode, no JPEG at all), the capture times out with a clear failure instead of
                // silently handing the rest of the pipeline a file it can't decode.
                var isRawFile = extension.ToLowerInvariant() is ".cr2" or ".cr3" or ".raw";

                string path;
                if (tcs == null)
                {
                    // No capture is waiting for this transfer — e.g. a prior CaptureAsync already
                    // timed out, or the camera's drive mode produced more than one frame for a
                    // single shutter press. The shutter has already fired: cancelling the transfer
                    // here would permanently discard a real photograph, which violates zero-data-loss.
                    // Salvage it to a side folder instead so nothing captured is ever thrown away.
                    var salvageDir = Path.Combine(string.IsNullOrWhiteSpace(directory)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroCapture")
                        : directory, "Unmatched");
                    Directory.CreateDirectory(salvageDir);
                    path = Path.Combine(salvageDir, $"unmatched_{DateTime.Now:yyyyMMdd_HHmmss_fff}{extension}");
                    Log("DownloadImage", $"Transfer received with no pending capture; salvaging to {path}.");
                }
                else
                {
                    path = Path.Combine(directory, prefix + extension);
                }

                IntPtr stream = IntPtr.Zero;
                try
                {
                    EnsureSuccess("EdsCreateFileStream", Call("EdsCreateFileStream", () => EDSDK.EdsCreateFileStream(path, EDSDK.EdsFileCreateDisposition.CreateAlways, EDSDK.EdsAccess.ReadWrite, out stream), $"path={path}"));
                    EnsureSuccess("EdsDownload", Call("EdsDownload", () => EDSDK.EdsDownload(dirItem, info.Size, stream), $"bytes={info.Size}"));
                    EnsureSuccess("EdsDownloadComplete", Call("EdsDownloadComplete", () => EDSDK.EdsDownloadComplete(dirItem)));
                }
                finally { Release(stream, "capture file stream"); }

                // A pure-RAW/CRAW/MRAW/SRAW quality setting (no JPEG/HEIF companion) never
                // produces a non-RAW file to resolve on, so without this the capture would
                // wait out the full 60s timeout in CaptureAsync. _captureExpectsRawOnly is
                // computed once per capture from the camera's actual current quality setting.
                if (!isRawFile || _captureExpectsRawOnly) tcs?.TrySetResult(path);
            }
        }
        catch (Exception ex) { Log("DownloadImageAsync", ex.ToString()); FailCapture(ex); }
        finally
        {
            Release(dirItem, "directory item transferred by event");
            lock (_sync) _isDownloading = false;
        }
        await Task.CompletedTask;
    }

    private uint PropertyEventHandler(uint inEvent, uint propertyId, uint param, IntPtr context)
    { Log("PropertyEvent", $"event=0x{inEvent:X8}, property=0x{propertyId:X8}, param=0x{param:X8}"); return EDSDK.EDS_ERR_OK; }

    private uint StateEventHandler(uint inEvent, uint parameter, IntPtr context)
    {
        Log("StateEvent", $"event=0x{inEvent:X8}, parameter=0x{parameter:X8}");
        if (inEvent == EDSDK.StateEvent_WillSoonShutDown)
        {
            lock (_sync) if (_camera != IntPtr.Zero) Call("EdsSendCommand(ExtendShutDownTimer)", () => EDSDK.EdsSendCommand(_camera, EDSDK.CameraCommand_ExtendShutDownTimer, 0));
        }
        else if (inEvent is EDSDK.StateEvent_Shutdown or EDSDK.StateEvent_InternalError)
        {
            lock (_sync) { _isConnected = false; _captureTcs?.TrySetException(new IOException("Camera disconnected or reported an internal SDK error.")); }
            PublishState(false, "Camera disconnected or shut down.");
        }
        else if (inEvent == EDSDK.StateEvent_CaptureError) FailCapture(new InvalidOperationException($"Camera capture failed: 0x{parameter:X8} ({ErrorName(parameter)})."));
        return EDSDK.EDS_ERR_OK;
    }

    private void FailCapture(Exception exception) { lock (_sync) _captureTcs?.TrySetException(exception); }
    private void ThrowIfDisconnected() { if (!_isConnected || _camera == IntPtr.Zero) throw new InvalidOperationException("Camera is not connected."); }
    private void PublishState(bool connected, string message) => StateChanged?.Invoke(this, new CameraStateEventArgs { IsConnected = connected, StatusMessage = message });
    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static bool Succeeded(uint result) => result == EDSDK.EDS_ERR_OK;
    private static void EnsureSuccess(string operation, uint result) { if (!Succeeded(result)) throw CreateSdkException(operation, result); }
    private static Exception CreateSdkException(string operation, uint result) => new InvalidOperationException($"{operation} failed: 0x{result:X8} ({ErrorName(result)}).");
    private static void Release(IntPtr reference, string purpose) { if (reference != IntPtr.Zero) Call("EdsRelease", () => EDSDK.EdsRelease(reference), purpose); }
    private static uint Call(string operation, Func<uint> call, string? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try { var result = call(); Log(operation, $"{parameters ?? ""} result=0x{result:X8} ({ErrorName(result)}), {sw.ElapsedMilliseconds}ms"); return result; }
        catch (Exception ex) { Log(operation, $"{parameters ?? ""} threw after {sw.ElapsedMilliseconds}ms: {ex}"); throw; }
    }
    private static uint CallLive(string operation, Func<uint> call, string? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = call();
            // A full log entry for every 30fps frame causes console I/O and UI stalls.
            // Keep a one-second success heartbeat while preserving every failure.
            var shouldLogSuccess = false;
            lock (LogSync)
            {
                if (DateTime.UtcNow - _lastLiveSuccessLogUtc >= TimeSpan.FromSeconds(1))
                {
                    _lastLiveSuccessLogUtc = DateTime.UtcNow;
                    shouldLogSuccess = true;
                }
            }
            if (result != EDSDK.EDS_ERR_OK || shouldLogSuccess)
                Log(operation, $"{parameters ?? ""} result=0x{result:X8} ({ErrorName(result)}), {sw.ElapsedMilliseconds}ms");
            return result;
        }
        catch (Exception ex) { Log(operation, $"{parameters ?? ""} threw after {sw.ElapsedMilliseconds}ms: {ex}"); throw; }
    }
    private static void Log(string operation, string message)
    {
        var line = $"[{DateTimeOffset.Now:O}] Canon EDSDK {operation}: {message}";
        Console.WriteLine(line);
        try
        {
            lock (LogSync)
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroCapture", "Logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "edsdk.log"), line + Environment.NewLine);
            }
        }
        catch { /* Logging must never interrupt camera control. */ }
    }
    private static string ErrorName(uint code) => code switch
    {
        EDSDK.EDS_ERR_OK => "OK", EDSDK.EDS_ERR_DEVICE_BUSY => "DEVICE_BUSY", EDSDK.EDS_ERR_DEVICE_NOT_FOUND => "DEVICE_NOT_FOUND",
        EDSDK.EDS_ERR_COMM_DISCONNECTED => "COMM_DISCONNECTED", EDSDK.EDS_ERR_OBJECT_NOTREADY => "OBJECT_NOTREADY",
        EDSDK.EDS_ERR_SESSION_NOT_OPEN => "SESSION_NOT_OPEN", EDSDK.EDS_ERR_INVALID_HANDLE => "INVALID_HANDLE",
        EDSDK.EDS_ERR_INVALID_PARAMETER => "INVALID_PARAMETER", EDSDK.EDS_ERR_NOT_SUPPORTED => "NOT_SUPPORTED",
        EDSDK.EDS_ERR_TAKE_PICTURE_AF_NG => "TAKE_PICTURE_AF_NG", EDSDK.EDS_ERR_LOW_BATTERY => "LOW_BATTERY", _ => "UNKNOWN_EDSDK_ERROR"
    };

    private static string FormatCameraValue(string key, uint value)
    {
        // Sourced from Canon's own EDSDK sample app (lib/EDSDK/Windows/sample/CSharp/CameraControl,
        // Property/*ComboBox.cs) so every value shown here is Canon's own documented meaning, not a
        // guess — these codes are not self-explanatory (e.g. Tv/Av encode 1/3-stop APEX values,
        // ImageQuality packs RAW type + JPEG size + compression into one word) and a wrong
        // hand-transcribed table would be worse than the raw hex it replaces.
        var table = key switch
        {
            "Tv" => TvNames,
            "Av" => AvNames,
            "ISO" => IsoNames,
            "ExposureCompensation" => ExposureCompNames,
            "WhiteBalance" => WhiteBalanceNames,
            "DriveMode" => DriveModeNames,
            "AFMode" => AfModeNames,
            "AEMode" => AeModeNames,
            "ImageQuality" => ImageQualityNames,
            _ => null
        };
        if (table != null && table.TryGetValue(value, out var name)) return name;
        return $"0x{value:X}";
    }

    private static readonly Dictionary<uint, string> TvNames = new()
    {
        { 0x4, "Auto" },
        { 0xC, "Bulb" },
        { 0x10, "30s" },
        { 0x13, "25s" },
        { 0x14, "20s" },
        { 0x15, "20s" },
        { 0x18, "15s" },
        { 0x1B, "13s" },
        { 0x1C, "10s" },
        { 0x1D, "10s" },
        { 0x20, "8s" },
        { 0x23, "6s" },
        { 0x24, "6s" },
        { 0x25, "5s" },
        { 0x28, "4s" },
        { 0x2B, "3.2s" },
        { 0x2C, "3s" },
        { 0x2D, "2.5s" },
        { 0x30, "2s" },
        { 0x33, "1.6s" },
        { 0x34, "1.5s" },
        { 0x35, "1.3s" },
        { 0x38, "1s" },
        { 0x3B, "0.8s" },
        { 0x3C, "0.7s" },
        { 0x3D, "0.6s" },
        { 0x40, "0.5s" },
        { 0x43, "0.4s" },
        { 0x44, "0.3s" },
        { 0x45, "0.3s" },
        { 0x48, "1/4" },
        { 0x4B, "1/5" },
        { 0x4C, "1/6" },
        { 0x4D, "1/6" },
        { 0x50, "1/8" },
        { 0x53, "1/10" },
        { 0x54, "1/10" },
        { 0x55, "1/13" },
        { 0x58, "1/15" },
        { 0x5B, "1/20" },
        { 0x5C, "1/20" },
        { 0x5D, "1/25" },
        { 0x60, "1/30" },
        { 0x63, "1/40" },
        { 0x64, "1/45" },
        { 0x65, "1/50" },
        { 0x68, "1/60" },
        { 0x6B, "1/80" },
        { 0x6C, "1/90" },
        { 0x6D, "1/100" },
        { 0x70, "1/125" },
        { 0x73, "1/160" },
        { 0x74, "1/180" },
        { 0x75, "1/200" },
        { 0x78, "1/250" },
        { 0x7B, "1/320" },
        { 0x7C, "1/350" },
        { 0x7D, "1/400" },
        { 0x80, "1/500" },
        { 0x83, "1/640" },
        { 0x84, "1/750" },
        { 0x85, "1/800" },
        { 0x88, "1/1000" },
        { 0x8B, "1/1250" },
        { 0x8C, "1/1500" },
        { 0x8D, "1/1600" },
        { 0x90, "1/2000" },
        { 0x93, "1/2500" },
        { 0x94, "1/3000" },
        { 0x95, "1/3200" },
        { 0x98, "1/4000" },
        { 0x9B, "1/5000" },
        { 0x9C, "1/6000" },
        { 0x9D, "1/6400" },
        { 0xA0, "1/8000" },
        { 0xA3, "1/10000" },
        { 0xA4, "1/12000" },
        { 0xA5, "1/12800" },
        { 0xA8, "1/16000" },
        { 0xAB, "1/20000" },
        { 0xAC, "1/24000" },
        { 0xAD, "1/25600" },
        { 0xB0, "1/32000" },
        { 0xB3, "1/40000" },
        { 0xB4, "1/48000" },
        { 0xB5, "1/51200" },
        { 0xB8, "1/64000" },
    };

    private static readonly Dictionary<uint, string> AvNames = new()
    {
        { 0x8, "f/1.0" },
        { 0xB, "f/1.1" },
        { 0xC, "f/1.2" },
        { 0xD, "f/1.2" },
        { 0x10, "f/1.4" },
        { 0x13, "f/1.6" },
        { 0x14, "f/1.8" },
        { 0x15, "f/1.8" },
        { 0x18, "f/2.0" },
        { 0x1B, "f/2.2" },
        { 0x1C, "f/2.5" },
        { 0x1D, "f/2.5" },
        { 0x20, "f/2.8" },
        { 0x23, "f/3.2" },
        { 0x24, "f/3.5" },
        { 0x25, "f/3.5" },
        { 0x28, "f/4.0" },
        { 0x2B, "f/4.5" },
        { 0x2C, "f/4.5" },
        { 0x2D, "f/5.0" },
        { 0x30, "f/5.6" },
        { 0x33, "f/6.3" },
        { 0x34, "f/6.7" },
        { 0x35, "f/7.1" },
        { 0x38, "f/8.0" },
        { 0x3B, "f/9.0" },
        { 0x3C, "f/9.5" },
        { 0x3D, "f/10" },
        { 0x40, "f/11" },
        { 0x43, "f/13" },
        { 0x44, "f/13" },
        { 0x45, "f/14" },
        { 0x48, "f/16" },
        { 0x4B, "f/18" },
        { 0x4C, "f/19" },
        { 0x4D, "f/20" },
        { 0x50, "f/22" },
        { 0x53, "f/25" },
        { 0x54, "f/27" },
        { 0x55, "f/29" },
        { 0x58, "f/32" },
        { 0x5B, "f/36" },
        { 0x5C, "f/38" },
        { 0x5D, "f/40" },
        { 0x60, "f/45" },
        { 0x63, "f/51" },
        { 0x64, "f/54" },
        { 0x65, "f/57" },
        { 0x68, "f/64" },
        { 0x6B, "f/72" },
        { 0x6C, "f/76" },
        { 0x6D, "f/80" },
        { 0x70, "f/91" },
        { 0x80, "f/3.3" },
        { 0x85, "f/3.4" },
        { 0xFF, "unknown" },
    };

    private static readonly Dictionary<uint, string> IsoNames = new()
    {
        { 0x0, "Auto" },
        { 0x28, "6" },
        { 0x30, "12" },
        { 0x38, "25" },
        { 0x40, "50" },
        { 0x48, "100" },
        { 0x4B, "125" },
        { 0x4D, "160" },
        { 0x50, "200" },
        { 0x53, "250" },
        { 0x55, "320" },
        { 0x58, "400" },
        { 0x5B, "500" },
        { 0x5D, "640" },
        { 0x60, "800" },
        { 0x63, "1000" },
        { 0x65, "1250" },
        { 0x68, "1600" },
        { 0x6B, "2000" },
        { 0x6D, "2500" },
        { 0x70, "3200" },
        { 0x73, "4000" },
        { 0x75, "5000" },
        { 0x78, "6400" },
        { 0x7B, "8000" },
        { 0x7D, "10000" },
        { 0x80, "12800" },
        { 0x83, "16000" },
        { 0x85, "20000" },
        { 0x88, "25600" },
        { 0x8B, "32000" },
        { 0x8D, "40000" },
        { 0x90, "51200" },
        { 0x93, "64000" },
        { 0x95, "80000" },
        { 0x98, "102400" },
        { 0xA0, "204800" },
        { 0xA8, "409600" },
        { 0xB0, "819200" },
    };

    private static readonly Dictionary<uint, string> ExposureCompNames = new()
    {
        { 0x0, "0" },
        { 0x3, "+1/3" },
        { 0x4, "+1/2" },
        { 0x5, "+2/3" },
        { 0x8, "+1" },
        { 0xB, "+1 1/3" },
        { 0xC, "+1 1/2" },
        { 0xD, "+1 2/3" },
        { 0x10, "+2" },
        { 0x13, "+2 1/3" },
        { 0x14, "+2 1/2" },
        { 0x15, "+2 2/3" },
        { 0x18, "+3" },
        { 0x1B, "+3 1/3" },
        { 0x1C, "+3 1/2" },
        { 0x1D, "+3 2/3" },
        { 0x20, "+4" },
        { 0x23, "+4 1/3" },
        { 0x24, "+4 1/2" },
        { 0x25, "+4 2/3" },
        { 0x28, "+5" },
        { 0xD8, "-5" },
        { 0xDB, "-4 2/3" },
        { 0xDC, "-4 1/2" },
        { 0xDD, "-4 1/3" },
        { 0xE0, "-4" },
        { 0xE3, "-3 2/3" },
        { 0xE4, "-3 1/2" },
        { 0xE5, "-3 1/3" },
        { 0xE8, "-3" },
        { 0xEB, "-2 2/3" },
        { 0xEC, "-2 1/2" },
        { 0xED, "-2 1/3" },
        { 0xF0, "-2" },
        { 0xF3, "-1 2/3" },
        { 0xF4, "-1 1/2" },
        { 0xF5, "-1 1/3" },
        { 0xF8, "-1" },
        { 0xFB, "-2/3" },
        { 0xFC, "-1/2" },
        { 0xFD, "-1/3" },
    };

    private static readonly Dictionary<uint, string> DriveModeNames = new()
    {
        { 0x0, "Single shooting" },
        { 0x1, "Medium speed continuous" },
        { 0x4, "High speed continuous" },
        { 0x5, "Low speed continuous" },
        { 0x6, "Single Silent(Soft) shooting" },
        { 0x7, "Self-timer:Continuous" },
        { 0x10, "Self-timer:10 sec" },
        { 0x11, "Self-timer:2 sec" },
        { 0x12, "High speed continuous +" },
        { 0x13, "Silent single shooting" },
        { 0x14, "Silent(Soft) contin shooting" },
        { 0x15, "Silent HS continuous" },
        { 0x16, "Silent(Soft) LS continuous" },
    };

    private static readonly Dictionary<uint, string> AfModeNames = new()
    {
        { 0x0, "One-Shot AF" },
        { 0x1, "AI Servo AF" },
        { 0x2, "AI Focus AF" },
        { 0x3, "Manual Focus" },
    };

    private static readonly Dictionary<uint, string> WhiteBalanceNames = new()
    {
        { 0x0, "Auto: Ambience priority" },
        { 0x1, "Daylight" },
        { 0x2, "Cloudy" },
        { 0x3, "Tungsten light" },
        { 0x4, "White fluorescent light" },
        { 0x5, "Flash" },
        { 0x6, "Custom1" },
        { 0x8, "Shade" },
        { 0x9, "Color temp." },
        { 0xA, "Custom white balance: PC-1" },
        { 0xB, "Custom white balance: PC-2" },
        { 0xC, "Custom white balance: PC-3" },
        { 0xF, "Custom2" },
        { 0x10, "Custom3" },
        { 0x11, "Underwater" },
        { 0x12, "Custom4" },
        { 0x13, "Custom5" },
        { 0x14, "Custom white balance: PC-4" },
        { 0x15, "Custom white balance: PC-5" },
        { 0x17, "Auto: White priority" },
        { 0x18, "Color temp.2" },
        { 0x19, "Color temp.3" },
        { 0x1A, "Color temp.4" },
    };

    private static readonly Dictionary<uint, string> AeModeNames = new()
    {
        { 0x0, "P" },
        { 0x1, "Tv" },
        { 0x2, "Av" },
        { 0x3, "M" },
        { 0x4, "Bulb" },
        { 0x5, "A-DEP" },
        { 0x6, "DEP" },
        { 0x7, "C1" },
        { 0x8, "Lock" },
        { 0x9, "GreenMode" },
        { 0xA, "Night Portrait" },
        { 0xB, "Sports" },
        { 0xC, "Portrait" },
        { 0xD, "Landscape" },
        { 0xE, "Close-up" },
        { 0xF, "No Strobo" },
        { 0x10, "C2" },
        { 0x11, "C3" },
        { 0x13, "Creative Auto" },
        { 0x14, "Movies" },
        { 0x15, "Photo In Movie" },
        { 0x16, "Scene Intelligent Auto" },
        { 0x17, "Handheld Night Scene" },
        { 0x18, "HDR Backlight Control" },
        { 0x19, "SCN" },
        { 0x1A, "Kids" },
        { 0x1B, "Food" },
        { 0x1C, "Candlelight" },
        { 0x1D, "Creative filters" },
        { 0x1E, "Grainy B/W" },
        { 0x1F, "Soft focus" },
        { 0x20, "Toy camera effect" },
        { 0x21, "Fish-eye effect" },
        { 0x22, "Water painting effect" },
        { 0x23, "Miniature effect" },
        { 0x24, "HDR art standard" },
        { 0x25, "HDR art vivid" },
        { 0x26, "HDR art bold" },
        { 0x27, "HDR art embossed" },
        { 0x28, "Dream" },
        { 0x29, "Old Movies" },
        { 0x2A, "Memory" },
        { 0x2B, "Dramatic B&W" },
        { 0x2C, "Miniature effect movie" },
        { 0x2D, "Panning" },
        { 0x2E, "Group Photo" },
        { 0x31, "HDR Movie" },
        { 0x32, "Self Portrait" },
        { 0x33, "Plus Movie Auto" },
        { 0x34, "Smooth skin" },
        { 0x35, "Panorama" },
        { 0x36, "Silent Mode" },
        { 0x37, "Fv" },
        { 0x38, "Art bold effect" },
        { 0x39, "Fireworks" },
        { 0x3A, "Star portrait" },
        { 0x3B, "Star nightscape" },
        { 0x3C, "Star trails" },
        { 0x3D, "Star time-lapse movie" },
        { 0x3E, "Background blur" },
    };

    private static readonly Dictionary<uint, string> ImageQualityNames = new()
    {
        { 0x0010FF0Fu, "Jpeg Large" },
        { 0x0110FF0Fu, "Jpeg Middle" },
        { 0x0510FF0Fu, "Jpeg Middle1" },
        { 0x0513FF0Fu, "Jpeg Middle1 Fine" },
        { 0x0512FF0Fu, "Jpeg Middle1 Normal" },
        { 0x0610FF0Fu, "Jpeg Middle2" },
        { 0x0613FF0Fu, "Jpeg Middle2 Fine" },
        { 0x0612FF0Fu, "Jpeg Middle2 Normal" },
        { 0x0210FF0Fu, "Jpeg Small" },
        { 0x0E10FF0Fu, "Jpeg Small1" },
        { 0x0F10FF0Fu, "Jpeg Small2" },
        { 0x0013FF0Fu, "Jpeg Large Fine" },
        { 0x0012FF0Fu, "Jpeg Large Normal" },
        { 0x0113FF0Fu, "Jpeg Middle Fine" },
        { 0x0112FF0Fu, "Jpeg Middle Normal" },
        { 0x0213FF0Fu, "Jpeg Small Fine" },
        { 0x0212FF0Fu, "Jpeg Small Normal" },
        { 0x0E13FF0Fu, "Jpeg Small1 Fine" },
        { 0x0E12FF0Fu, "Jpeg Small1 Normal" },
        { 0x0F13FF0Fu, "Jpeg Small2" },
        { 0x1013FF0Fu, "Jpeg Small3" },
        { 0x0064FF0Fu, "RAW" },
        { 0x00640013u, "RAW + Jpeg Large Fine" },
        { 0x00640012u, "RAW + Jpeg Large Normal" },
        { 0x00640113u, "RAW + Jpeg Middle Fine" },
        { 0x00640112u, "RAW + Jpeg Middle Normal" },
        { 0x00640213u, "RAW + Jpeg Small Fine" },
        { 0x00640212u, "RAW + Jpeg Small Normal" },
        { 0x00640E13u, "RAW + Jpeg Small1 Fine" },
        { 0x00640E12u, "RAW + Jpeg Small1 Normal" },
        { 0x00640F13u, "RAW + Jpeg Small2" },
        { 0x00641013u, "RAW + Jpeg Small3" },
        { 0x00640010u, "RAW + Jpeg Large" },
        { 0x00640110u, "RAW + Jpeg Middle" },
        { 0x00640510u, "RAW + Jpeg Middle1" },
        { 0x00640513u, "RAW + Jpeg Middle1 Fine" },
        { 0x00640512u, "RAW + Jpeg Middle1 Normal" },
        { 0x00640610u, "RAW + Jpeg Middle2" },
        { 0x00640613u, "RAW + Jpeg Middle2 Fine" },
        { 0x00640612u, "RAW + Jpeg Middle2 Normal" },
        { 0x00640210u, "RAW + Jpeg Small" },
        { 0x00640E10u, "RAW + Jpeg Small1" },
        { 0x00640F10u, "RAW + Jpeg Small2" },
        { 0x0164FF0Fu, "MRAW(SRAW1)" },
        { 0x01640013u, "MRAW(SRAW1) + Jpeg Large Fine" },
        { 0x01640012u, "MRAW(SRAW1) + Jpeg Large Normal" },
        { 0x01640113u, "MRAW(SRAW1) + Jpeg Middle Fine" },
        { 0x01640112u, "MRAW(SRAW1) + Jpeg Middle Normal" },
        { 0x01640213u, "MRAW(SRAW1) + Jpeg Small Fine" },
        { 0x01640212u, "MRAW(SRAW1) + Jpeg Small Normal" },
        { 0x01640E13u, "MRAW(SRAW1) + Jpeg Small1 Fine" },
        { 0x01640E12u, "MRAW(SRAW1) + Jpeg Small1 Normal" },
        { 0x01640F13u, "MRAW(SRAW1) + Jpeg Small2" },
        { 0x01641013u, "MRAW(SRAW1) + Jpeg Small3" },
        { 0x01640010u, "MRAW(SRAW1) + Jpeg Large" },
        { 0x01640510u, "MRAW(SRAW1) + Jpeg Middle1" },
        { 0x01640513u, "MRAW(SRAW1) + Jpeg Middle1 Fine" },
        { 0x01640512u, "MRAW(SRAW1) + Jpeg Middle1 Normal" },
        { 0x01640610u, "MRAW(SRAW1) + Jpeg Middle2" },
        { 0x01640613u, "MRAW(SRAW1) + Jpeg Middle2 Fine" },
        { 0x01640612u, "MRAW(SRAW1) + Jpeg Middle2 Normal" },
        { 0x01640210u, "MRAW(SRAW1) + Jpeg Small" },
        { 0x0264FF0Fu, "SRAW(SRAW2)" },
        { 0x02640013u, "SRAW(SRAW2) + Jpeg Large Fine" },
        { 0x02640012u, "SRAW(SRAW2) + Jpeg Large Normal" },
        { 0x02640113u, "SRAW(SRAW2) + Jpeg Middle Fine" },
        { 0x02640112u, "SRAW(SRAW2) + Jpeg Middle Normal" },
        { 0x02640213u, "SRAW(SRAW2) + Jpeg Small Fine" },
        { 0x02640212u, "SRAW(SRAW2) + Jpeg Small Normal" },
        { 0x02640E13u, "SRAW(SRAW2) + Jpeg Small1 Fine" },
        { 0x02640E12u, "SRAW(SRAW2) + Jpeg Small1 Normal" },
        { 0x02640F13u, "SRAW(SRAW2) + Jpeg Small2" },
        { 0x02641013u, "SRAW(SRAW2) + Jpeg Small3" },
        { 0x02640010u, "SRAW(SRAW2) + Jpeg Large" },
        { 0x02640510u, "SRAW(SRAW2) + Jpeg Middle1" },
        { 0x02640513u, "SRAW(SRAW2) + Jpeg Middle1 Fine" },
        { 0x02640512u, "SRAW(SRAW2) + Jpeg Middle1 Normal" },
        { 0x02640610u, "SRAW(SRAW2) + Jpeg Middle2" },
        { 0x02640613u, "SRAW(SRAW2) + Jpeg Middle2 Fine" },
        { 0x02640612u, "SRAW(SRAW2) + Jpeg Middle2 Normal" },
        { 0x02640210u, "SRAW(SRAW2) + Jpeg Small" },
        { 0x0063FF0Fu, "CRAW" },
        { 0x00630013u, "CRAW + Jpeg Large Fine" },
        { 0x00630113u, "CRAW + Jpeg Middle Fine" },
        { 0x00630513u, "CRAW + Jpeg Middle1 Fine" },
        { 0x00630613u, "CRAW + Jpeg Middle2 Fine" },
        { 0x00630213u, "CRAW + Jpeg Small Fine" },
        { 0x00630E13u, "CRAW + Jpeg Small1 Fine" },
        { 0x00630F13u, "CRAW + Jpeg Small2 Fine" },
        { 0x00631013u, "CRAW + Jpeg Small3 Fine" },
        { 0x00630012u, "CRAW + Jpeg Large Normal" },
        { 0x00630112u, "CRAW + Jpeg Middle Normal" },
        { 0x00630512u, "CRAW + Jpeg Middle1 Normal" },
        { 0x00630612u, "CRAW + Jpeg Middle2 Normal" },
        { 0x00630212u, "CRAW + Jpeg Small Normal" },
        { 0x00630E12u, "CRAW + Jpeg Small1 Normal" },
        { 0x00630010u, "CRAW + Jpeg Large" },
        { 0x00630110u, "CRAW + Jpeg Middle" },
        { 0x00630510u, "CRAW + Jpeg Middle1" },
        { 0x00630610u, "CRAW + Jpeg Middle2" },
        { 0x00630210u, "CRAW + Jpeg Small" },
        { 0x00630E10u, "CRAW + Jpeg Small1" },
        { 0x00630F10u, "CRAW + Jpeg Small2" },
        { 0x0080FF0Fu, "HEIF Large" },
        { 0x0180FF0Fu, "HEIF Middle" },
        { 0x0580FF0Fu, "HEIF Middle1" },
        { 0x0680FF0Fu, "HEIF Middle2" },
        { 0x0083FF0Fu, "HEIF Large Fine" },
        { 0x0082FF0Fu, "HEIF Large Normal" },
        { 0x0183FF0Fu, "HEIF Middle Fine" },
        { 0x0182FF0Fu, "HEIF Middle Normal" },
        { 0x0E80FF0Fu, "HEIF Small1" },
        { 0x0E83FF0Fu, "HEIF Small1 Fine" },
        { 0x0E82FF0Fu, "HEIF Small1 Normal" },
        { 0x0F80FF0Fu, "HEIF Small2" },
        { 0x0F83FF0Fu, "HEIF Small2 Fine" },
        { 0x00640080u, "RAW  + HEIF Large" },
        { 0x00640083u, "RAW + HEIF Large Fine" },
        { 0x00640082u, "RAW + HEIF Large Normal" },
        { 0x00640180u, "RAW + HEIF Middle" },
        { 0x00640580u, "RAW + HEIF Middle1" },
        { 0x00640680u, "RAW + HEIF Middle2" },
        { 0x00640183u, "RAW + HEIF Middle Fine" },
        { 0x00640182u, "RAW + HEIF Middle Normal" },
        { 0x00640E80u, "RAW + HEIF Small1" },
        { 0x00640E83u, "RAW + HEIF Small1 Fine" },
        { 0x00640E82u, "RAW + HEIF Small1 Normal" },
        { 0x00640F80u, "RAW + HEIF Small2" },
        { 0x00640F83u, "RAW + HEIF Small2 Fine" },
        { 0x00630080u, "CRAW + HEIF Large" },
        { 0x00630083u, "CRAW + HEIF Large Fine" },
        { 0x00630082u, "CRAW + HEIF Large Normal" },
        { 0x00630180u, "CRAW + HEIF Middle" },
        { 0x00630183u, "CRAW + HEIF Middle Fine" },
        { 0x00630182u, "CRAW + HEIF Middle Normal" },
        { 0x00630580u, "CRAW + HEIF Middle1" },
        { 0x00630680u, "CRAW + HEIF Middle2" },
        { 0x00630E80u, "CRAW + HEIF Small1" },
        { 0x00630E83u, "CRAW + HEIF Small1 Fine" },
        { 0x00630E82u, "CRAW + HEIF Small1 Normal" },
        { 0x00630F80u, "CRAW + HEIF Small2" },
        { 0x00630F83u, "CRAW + HEIF Small2 Fine" },
    };

    /// <summary>True for a quality setting that produces no JPEG/HEIF companion file at all
    /// (plain RAW/CRAW/MRAW/SRAW) — the only case where a capture must resolve on the RAW
    /// file itself instead of waiting for a non-RAW file that will never arrive.</summary>
    private static bool IsRawOnlyQuality(uint value) =>
        ImageQualityNames.TryGetValue(value, out var name) &&
        name is "RAW" or "CRAW" or "MRAW(SRAW1)" or "SRAW(SRAW2)";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycleLock.Wait();
        try
        {
            try { DisconnectCoreAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Log("Dispose", ex.ToString()); }
            if (_sdkInitialized) { try { Call("EdsTerminateSDK", EDSDK.EdsTerminateSDK); } catch (Exception ex) { Log("EdsTerminateSDK", ex.ToString()); } }
        }
        finally { _lifecycleLock.Release(); }
        _captureLock.Dispose();
        _lifecycleLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
