using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EDSDKLib;
using MicroCapture.Core.Interfaces;

namespace MicroCapture.Camera.Canon;

public class CanonCameraService : ICameraService, IDisposable
{
    private IntPtr _camera = IntPtr.Zero;
    private bool _isConnected;
    
    public bool IsConnected => _isConnected;
    
    // We must keep references to delegates so they aren't garbage collected
    private EDSDK.EdsObjectEventHandler? _objectEventHandler;
    private EDSDK.EdsPropertyEventHandler? _propertyEventHandler;
    private EDSDK.EdsStateEventHandler? _stateEventHandler;

    private string _currentSaveDirectory = string.Empty;
    private string _currentFilePrefix = string.Empty;

    public event EventHandler<CameraStateEventArgs>? StateChanged;
    public event EventHandler<byte[]>? LiveViewFrameReceived;

    public CanonCameraService()
    {
        try
        {
            var err = EDSDK.EdsInitializeSDK();
            if (err != EDSDK.EDS_ERR_OK)
            {
                Console.WriteLine($"EDSDK Initialization failed with error {err}");
            }
        }
        catch (DllNotFoundException)
        {
            // Expected on non-Windows platforms
            Console.WriteLine("EDSDK.dll not found. CanonCameraService will not function.");
        }
    }

    ~CanonCameraService()
    {
        try { EDSDK.EdsTerminateSDK(); } catch { }
    }

    public Task<IEnumerable<CameraDeviceInfo>> GetConnectedCamerasAsync()
    {
        var cameras = new List<CameraDeviceInfo>();
        if (!IsWindows()) return Task.FromResult<IEnumerable<CameraDeviceInfo>>(cameras);

        IntPtr cameraList = IntPtr.Zero;
        var err = EDSDK.EdsGetCameraList(out cameraList);
        
        int count = 0;
        if (err == EDSDK.EDS_ERR_OK)
        {
            err = EDSDK.EdsGetChildCount(cameraList, out count);
            for (int i = 0; i < count; i++)
            {
                IntPtr cam = IntPtr.Zero;
                EDSDK.EdsGetChildAtIndex(cameraList, i, out cam);
                
                EDSDK.EdsDeviceInfo deviceInfo;
                EDSDK.EdsGetDeviceInfo(cam, out deviceInfo);
                
                cameras.Add(new CameraDeviceInfo
                {
                    Id = deviceInfo.szPortName, // Use port name as ID
                    Model = deviceInfo.szDeviceDescription
                });
                
                EDSDK.EdsRelease(cam);
            }
            EDSDK.EdsRelease(cameraList);
        }
        return Task.FromResult<IEnumerable<CameraDeviceInfo>>(cameras);
    }

    public Task<bool> ConnectAsync(string cameraId)
    {
        if (!IsWindows()) return Task.FromResult(false);

        IntPtr cameraList = IntPtr.Zero;
        EDSDK.EdsGetCameraList(out cameraList);
        EDSDK.EdsGetChildAtIndex(cameraList, 0, out _camera); // Simply grab first for now
        EDSDK.EdsRelease(cameraList);

        if (_camera == IntPtr.Zero)
            return Task.FromResult(false);

        var err = EDSDK.EdsOpenSession(_camera);
        if (err != EDSDK.EDS_ERR_OK)
            return Task.FromResult(false);

        // Set up events
        _objectEventHandler = new EDSDK.EdsObjectEventHandler(ObjectEventHandler);
        EDSDK.EdsSetObjectEventHandler(_camera, EDSDK.ObjectEvent_All, _objectEventHandler, IntPtr.Zero);

        _propertyEventHandler = new EDSDK.EdsPropertyEventHandler(PropertyEventHandler);
        EDSDK.EdsSetPropertyEventHandler(_camera, EDSDK.PropertyEvent_All, _propertyEventHandler, IntPtr.Zero);

        _stateEventHandler = new EDSDK.EdsStateEventHandler(StateEventHandler);
        EDSDK.EdsSetCameraStateEventHandler(_camera, EDSDK.StateEvent_All, _stateEventHandler, IntPtr.Zero);

        // Tell camera to save images to Host (PC)
        uint saveTo = (uint)EDSDK.EdsSaveTo.Host;
        EDSDK.EdsSetPropertyData(_camera, EDSDK.PropID_SaveTo, 0, Marshal.SizeOf(typeof(uint)), saveTo);

        // Tell camera how much capacity the host has
        var capacity = new EDSDK.EdsCapacity { NumberOfFreeClusters = 0x7FFFFFFF, BytesPerSector = 512, Reset = 1 };
        EDSDK.EdsSetCapacity(_camera, capacity);

        _isConnected = true;
        StateChanged?.Invoke(this, new CameraStateEventArgs { IsConnected = true, StatusMessage = "Canon camera connected" });

        return Task.FromResult(true);
    }

    public Task DisconnectAsync()
    {
        if (_camera != IntPtr.Zero)
        {
            EDSDK.EdsCloseSession(_camera);
            EDSDK.EdsRelease(_camera);
            _camera = IntPtr.Zero;
        }
        _isConnected = false;
        StateChanged?.Invoke(this, new CameraStateEventArgs { IsConnected = false, StatusMessage = "Disconnected" });
        return Task.CompletedTask;
    }

    public Task StartLiveViewAsync()
    {
        // Setup LiveView (Set property kEdsPropID_Evf_OutputDevice to PC)
        if (_camera != IntPtr.Zero)
        {
            uint device = EDSDK.EvfOutputDevice_PC;
            EDSDK.EdsSetPropertyData(_camera, EDSDK.PropID_Evf_OutputDevice, 0, Marshal.SizeOf(typeof(uint)), device);
            // In a real app, we'd start a background thread to continually call EdsDownloadEvfImage
        }
        return Task.CompletedTask;
    }

    public Task StopLiveViewAsync()
    {
        if (_camera != IntPtr.Zero)
        {
            uint device = EDSDK.EvfOutputDevice_TFT;
            EDSDK.EdsSetPropertyData(_camera, EDSDK.PropID_Evf_OutputDevice, 0, Marshal.SizeOf(typeof(uint)), device);
        }
        return Task.CompletedTask;
    }

    private TaskCompletionSource<string>? _captureTcs;

    public Task<string> CaptureAsync(string outputDirectory, string filePrefix)
    {
        if (!_isConnected) throw new InvalidOperationException("Camera not connected");

        _currentSaveDirectory = outputDirectory;
        _currentFilePrefix = filePrefix;
        _captureTcs = new TaskCompletionSource<string>();

        var err = EDSDK.EdsSendCommand(_camera, EDSDK.CameraCommand_TakePicture, 0);
        if (err != EDSDK.EDS_ERR_OK)
        {
            _captureTcs.SetException(new Exception($"Failed to trigger capture: {err}"));
        }

        return _captureTcs.Task;
    }

    // --- EDSDK Event Handlers ---

    private uint ObjectEventHandler(uint inEvent, IntPtr inRef, IntPtr inContext)
    {
        if (inEvent == EDSDK.ObjectEvent_DirItemRequestTransfer)
        {
            Task.Run(() => DownloadImage(inRef));
        }
        else
        {
            EDSDK.EdsRelease(inRef);
        }
        return EDSDK.EDS_ERR_OK;
    }

    private void DownloadImage(IntPtr dirItem)
    {
        try
        {
            EDSDK.EdsDirectoryItemInfo dirItemInfo;
            EDSDK.EdsGetDirectoryItemInfo(dirItem, out dirItemInfo);

            var extension = Path.GetExtension(dirItemInfo.szFileName);
            var destPath = Path.Combine(_currentSaveDirectory, $"{_currentFilePrefix}{extension}");

            IntPtr stream;
            EDSDK.EdsCreateFileStream(destPath, EDSDK.EdsFileCreateDisposition.CreateAlways, EDSDK.EdsAccess.ReadWrite, out stream);
            EDSDK.EdsDownload(dirItem, dirItemInfo.Size, stream);
            EDSDK.EdsDownloadComplete(dirItem);
            EDSDK.EdsRelease(stream);
            EDSDK.EdsRelease(dirItem);

            _captureTcs?.TrySetResult(destPath);
        }
        catch (Exception ex)
        {
            _captureTcs?.TrySetException(ex);
        }
    }

    private uint PropertyEventHandler(uint inEvent, uint inPropertyID, uint inParam, IntPtr inContext)
    {
        return EDSDK.EDS_ERR_OK;
    }

    private uint StateEventHandler(uint inEvent, uint inParameter, IntPtr inContext)
    {
        if (inEvent == EDSDK.StateEvent_Shutdown)
        {
            _isConnected = false;
            StateChanged?.Invoke(this, new CameraStateEventArgs { IsConnected = false, StatusMessage = "Camera shutdown by user or battery" });
        }
        return EDSDK.EDS_ERR_OK;
    }

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public void Dispose()
    {
        DisconnectAsync().Wait();
    }
}
