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
    private readonly object _sync = new();
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private IntPtr _camera;
    private bool _isConnected;
    private bool _sdkInitialized;
    private bool _disposed;
    private CancellationTokenSource? _liveViewCts;
    private Task? _liveViewTask;
    private TaskCompletionSource<string>? _captureTcs;
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
        await DisconnectAsync().ConfigureAwait(false);

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
        await StopLiveViewAsync().ConfigureAwait(false);
        IntPtr camera;
        lock (_sync)
        {
            camera = _camera;
            _camera = IntPtr.Zero;
            _isConnected = false;
            _captureTcs?.TrySetException(new OperationCanceledException("Camera disconnected before capture completed."));
            _captureTcs = null;
        }
        if (camera != IntPtr.Zero)
        {
            Call("EdsCloseSession", () => EDSDK.EdsCloseSession(camera));
            Release(camera, "camera session");
        }
        PublishState(false, "Disconnected");
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
            // Output-device flags are a bitmask. Retaining TFT keeps the camera LCD
            // active while adding the PC stream used by the application.
            uint device = _hasPreviousEvfOutputDevice
                ? currentDevice | EDSDK.EvfOutputDevice_PC
                : EDSDK.EvfOutputDevice_TFT | EDSDK.EvfOutputDevice_PC;
            EnsureSuccess("EdsSetPropertyData(Evf_OutputDevice=TFT|PC)", Call("EdsSetPropertyData(Evf_OutputDevice=TFT|PC)", () => EDSDK.EdsSetPropertyData(camera, EDSDK.PropID_Evf_OutputDevice, 0, sizeof(uint), device)));
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
        if (task != null) { try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { Log("StopLiveViewAsync", ex.ToString()); } }
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

    public async Task<string> CaptureAsync(string outputDirectory, string filePrefix)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(filePrefix)) throw new ArgumentException("An output directory and file prefix are required.");
        Directory.CreateDirectory(outputDirectory);
        await _captureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            TaskCompletionSource<string> tcs;
            lock (_sync)
            {
                ThrowIfDisconnected();
                if (_captureTcs != null) throw new InvalidOperationException("A capture is already in progress.");
                _currentSaveDirectory = outputDirectory;
                _currentFilePrefix = filePrefix;
                tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _captureTcs = tcs;
                var result = Call("EdsSendCommand(CameraCommand_TakePicture)", () => EDSDK.EdsSendCommand(_camera, EDSDK.CameraCommand_TakePicture, 0));
                if (result != EDSDK.EDS_ERR_OK) tcs.TrySetException(CreateSdkException("EdsSendCommand(CameraCommand_TakePicture)", result));
            }
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) _captureTcs = null;
            _captureLock.Release();
        }
    }

    private async Task LiveViewLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IntPtr stream = IntPtr.Zero, image = IntPtr.Zero;
            try
            {
                IntPtr camera; lock (_sync) { camera = _camera; }
                if (camera == IntPtr.Zero) return;
                if (!Succeeded(Call("EdsCreateMemoryStream", () => EDSDK.EdsCreateMemoryStream(0, out stream))) ||
                    !Succeeded(Call("EdsCreateEvfImageRef", () => EDSDK.EdsCreateEvfImageRef(stream, out image))))
                { await Task.Delay(250, token).ConfigureAwait(false); continue; }
                var result = Call("EdsDownloadEvfImage", () => EDSDK.EdsDownloadEvfImage(camera, image));
                if (result == EDSDK.EDS_ERR_OK)
                {
                    ulong length = 0; IntPtr pointer = IntPtr.Zero;
                    if (Succeeded(Call("EdsGetLength(EVF stream)", () => EDSDK.EdsGetLength(stream, out length))) && length is > 0 and <= int.MaxValue &&
                        Succeeded(Call("EdsGetPointer(EVF stream)", () => EDSDK.EdsGetPointer(stream, out pointer))) && pointer != IntPtr.Zero)
                    {
                        var bytes = new byte[(int)length];
                        Marshal.Copy(pointer, bytes, 0, bytes.Length);
                        LiveViewFrameReceived?.Invoke(this, bytes);
                    }
                }
                else if (result != EDSDK.EDS_ERR_OBJECT_NOTREADY)
                {
                    PublishState(true, $"Live View error: {ErrorName(result)} (0x{result:X8})");
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Log("LiveViewLoopAsync", ex.ToString()); await Task.Delay(250, token).ConfigureAwait(false); }
            finally { Release(image, "EVF image"); Release(stream, "EVF stream"); }
            await Task.Delay(33, token).ConfigureAwait(false);
        }
    }

    private uint ObjectEventHandler(uint inEvent, IntPtr inRef, IntPtr inContext)
    {
        Log("ObjectEvent", $"event=0x{inEvent:X8}, ref=0x{inRef.ToInt64():X}");
        if (inEvent != EDSDK.ObjectEvent_DirItemRequestTransfer || inRef == IntPtr.Zero) return EDSDK.EDS_ERR_OK;
        var retain = Call("EdsRetain(DirItemRequestTransfer)", () => EDSDK.EdsRetain(inRef));
        if (retain != EDSDK.EDS_ERR_OK) { FailCapture(CreateSdkException("EdsRetain(DirItemRequestTransfer)", retain)); return retain; }
        _ = Task.Run(() => DownloadImageAsync(inRef));
        return EDSDK.EDS_ERR_OK;
    }

    private async Task DownloadImageAsync(IntPtr dirItem)
    {
        try
        {
            TaskCompletionSource<string>? tcs; string directory, prefix;
            lock (_sync) { tcs = _captureTcs; directory = _currentSaveDirectory; prefix = _currentFilePrefix; }
            if (tcs == null) { Log("DownloadImage", "Transfer received with no pending capture; releasing item."); return; }
            var info = default(EDSDK.EdsDirectoryItemInfo);
            EnsureSuccess("EdsGetDirectoryItemInfo", Call("EdsGetDirectoryItemInfo", () => EDSDK.EdsGetDirectoryItemInfo(dirItem, out info)));
            var extension = Path.GetExtension(info.szFileName);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";
            var path = Path.Combine(directory, prefix + extension);
            IntPtr stream = IntPtr.Zero;
            try
            {
                EnsureSuccess("EdsCreateFileStream", Call("EdsCreateFileStream", () => EDSDK.EdsCreateFileStream(path, EDSDK.EdsFileCreateDisposition.CreateAlways, EDSDK.EdsAccess.ReadWrite, out stream), $"path={path}"));
                EnsureSuccess("EdsDownload", Call("EdsDownload", () => EDSDK.EdsDownload(dirItem, info.Size, stream), $"bytes={info.Size}"));
                EnsureSuccess("EdsDownloadComplete", Call("EdsDownloadComplete", () => EDSDK.EdsDownloadComplete(dirItem)));
            }
            finally { Release(stream, "capture file stream"); }
            tcs.TrySetResult(path);
        }
        catch (Exception ex) { Log("DownloadImageAsync", ex.ToString()); FailCapture(ex); }
        finally { Release(dirItem, "retained directory item"); }
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
    private static void Log(string operation, string message) => Console.WriteLine($"[{DateTimeOffset.Now:O}] Canon EDSDK {operation}: {message}");
    private static string ErrorName(uint code) => code switch
    {
        EDSDK.EDS_ERR_OK => "OK", EDSDK.EDS_ERR_DEVICE_BUSY => "DEVICE_BUSY", EDSDK.EDS_ERR_DEVICE_NOT_FOUND => "DEVICE_NOT_FOUND",
        EDSDK.EDS_ERR_COMM_DISCONNECTED => "COMM_DISCONNECTED", EDSDK.EDS_ERR_OBJECT_NOTREADY => "OBJECT_NOTREADY",
        EDSDK.EDS_ERR_SESSION_NOT_OPEN => "SESSION_NOT_OPEN", EDSDK.EDS_ERR_INVALID_HANDLE => "INVALID_HANDLE",
        EDSDK.EDS_ERR_INVALID_PARAMETER => "INVALID_PARAMETER", EDSDK.EDS_ERR_NOT_SUPPORTED => "NOT_SUPPORTED",
        EDSDK.EDS_ERR_TAKE_PICTURE_AF_NG => "TAKE_PICTURE_AF_NG", EDSDK.EDS_ERR_LOW_BATTERY => "LOW_BATTERY", _ => "UNKNOWN_EDSDK_ERROR"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisconnectAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Log("Dispose", ex.ToString()); }
        if (_sdkInitialized) { try { Call("EdsTerminateSDK", EDSDK.EdsTerminateSDK); } catch (Exception ex) { Log("EdsTerminateSDK", ex.ToString()); } }
        _captureLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
