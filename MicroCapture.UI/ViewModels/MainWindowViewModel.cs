using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Interfaces;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;
using MicroCapture.UI.Views;

namespace MicroCapture.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICameraService _cameraService;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;
    private readonly MicroCapture.Processing.BackgroundProcessingWorker? _worker;

    // --- State ---
    [ObservableProperty] private string _statusText = "Ready — Connect camera to begin";
    [ObservableProperty] private Bitmap? _liveViewImage;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isAutoCapture;
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private string _projectCode = "";
    [ObservableProperty] private string _batchCode = "";
    [ObservableProperty] private string _cameraModel = "Not connected";
    [ObservableProperty] private string _connectionStatus = "DISCONNECTED";
    [ObservableProperty] private string _focusStatus = "—";
    [ObservableProperty] private string _exposureStatus = "—";
    [ObservableProperty] private string _documentStatus = "—";
    [ObservableProperty] private string _captureReadiness = "NOT READY";
    [ObservableProperty] private bool _splitBookPages = false;
    [ObservableProperty] private string _exportFormat = "PDF";

    private string? _currentProjectId;
    private string? _currentBatchId;
    private string _outputDirectory = string.Empty;
    private string _connectedCameraModel = "Not connected";
    private int _liveViewFramePending;
    private int _captureInProgress;
    private DateTime _lastAutoCaptureUtc = DateTime.MinValue;
    private DateTime _lastDocumentCheckUtc = DateTime.MinValue;

    // Thumbnail items for recent captures
    public ObservableCollection<ThumbnailItem> RecentCaptures { get; } = new();
    public ObservableCollection<CameraControlItem> CameraControls { get; } = new();

    public MainWindowViewModel()
    {
        // Design-time constructor
        _cameraService = null!;
        _dbContext = null!;
        _queueService = null!;
    }

    public MainWindowViewModel(ICameraService cameraService)
    {
        _cameraService = cameraService;
        _dbContext = new AppDbContext();
        _queueService = new CaptureQueueService(_dbContext);
        
        _worker = new MicroCapture.Processing.BackgroundProcessingWorker();
        _worker.StatusChanged += (s, msg) => {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = $"Background: {msg}");
        };
        _worker.JobCompleted += (s, result) => {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var thumbnail = RecentCaptures.FirstOrDefault(t => t.FilePath == result.OriginalFilePath);
                if (thumbnail != null)
                    thumbnail.Status = !result.Success ? "Processing failed"
                        : result.OcrStatus == "Failed" ? "Processed — OCR failed"
                        : result.QcVerdict == "FAIL" ? "Processed — QC warning"
                        : "Processed";
            });
        };
        _worker.Start();

        _cameraService.StateChanged += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsConnected = e.IsConnected;
                ConnectionStatus = e.IsConnected ? "CONNECTED" : "DISCONNECTED";
                CameraModel = e.IsConnected ? _connectedCameraModel : "Not connected";
                StatusText = e.StatusMessage;
                UpdateCaptureReadiness();
            });
        };

        _cameraService.LiveViewFrameReceived += (s, frameBytes) =>
        {
            // Drop stale frames while the UI is rendering. This keeps Live View from
            // building an unbounded dispatcher queue when camera or processing work is busy.
            if (Interlocked.Exchange(ref _liveViewFramePending, 1) != 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new MemoryStream(frameBytes);
                    var bitmap = new Bitmap(ms);
                    var old = LiveViewImage;
                    LiveViewImage = bitmap;
                    old?.Dispose();
                    // Boundary detection is throttled to keep the live-view path
                    // responsive while still providing a meaningful auto-capture gate.
                    if (DateTime.UtcNow - _lastDocumentCheckUtc >= TimeSpan.FromMilliseconds(500))
                    {
                        _lastDocumentCheckUtc = DateTime.UtcNow;
                        var detected = MicroCapture.Processing.ImageProcessor.IsDocumentDetected(frameBytes);
                        DocumentStatus = detected ? "✓ Boundary detected" : "Waiting for boundary";
                    }
                    FocusStatus = "Camera-controlled";
                    ExposureStatus = "Camera-controlled";
                    UpdateCaptureReadiness();
                    if (IsAutoCapture && DocumentStatus.StartsWith("✓") && _currentBatchId != null && DateTime.UtcNow - _lastAutoCaptureUtc >= TimeSpan.FromSeconds(1.5))
                    {
                        _lastAutoCaptureUtc = DateTime.UtcNow;
                        _ = CaptureAsync();
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"Live View frame decode failed: {ex}"); }
                finally { Volatile.Write(ref _liveViewFramePending, 0); }
            });
        };
    }

    private void UpdateCaptureReadiness()
    {
        if (!IsConnected)
            CaptureReadiness = "NOT READY";
        else if (string.IsNullOrWhiteSpace(ProjectCode) || string.IsNullOrWhiteSpace(BatchCode))
            CaptureReadiness = "SET PROJECT & BATCH";
        else if (!DocumentStatus.StartsWith("✓"))
            CaptureReadiness = "WAITING FOR DOCUMENT";
        else
            CaptureReadiness = "READY TO CAPTURE";
    }

    // ---------- Commands ----------

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            if (IsConnected)
            {
                await _cameraService.StopLiveViewAsync();
                await _cameraService.DisconnectAsync();
                return;
            }
            StatusText = "Connecting to camera...";
            var cameras = await _cameraService.GetConnectedCamerasAsync();
            var first = cameras.FirstOrDefault();
            if (first == null)
            {
                StatusText = "No cameras found";
                return;
            }
            if (!await _cameraService.ConnectAsync(first.Id))
            {
                StatusText = "Camera connection failed — see diagnostic log";
                return;
            }
            _connectedCameraModel = first.Model;
            CameraModel = first.Model;
            await _cameraService.StartLiveViewAsync();
            await LoadCameraSettingsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Camera error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StartBatchAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectCode) || string.IsNullOrWhiteSpace(BatchCode))
        {
            StatusText = "Enter Project Code and Batch Code first";
            return;
        }

        try
        {
            // Ensure project exists
            var project = _dbContext.Projects.FirstOrDefault(p => p.Name == ProjectCode);
            if (project == null)
            {
                project = new Project
                {
                    Name = ProjectCode,
                    Customer = "",
                    Description = "Auto-created from scanning session",
                    CreatedBy = Environment.UserName,
                    OutputDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        "MicroCapture", ProjectCode)
                };
                _dbContext.Projects.Add(project);
                await _dbContext.SaveChangesAsync();
            }
            _currentProjectId = project.Id;
            _outputDirectory = project.OutputDirectory;

            // Create batch
            var batch = new Batch
            {
                ProjectId = project.Id,
                Name = BatchCode,
                BatchCode = BatchCode,
                Operator = Environment.UserName,
                SplitBookPages = SplitBookPages
            };
            _dbContext.Batches.Add(batch);
            await _dbContext.SaveChangesAsync();

            _currentBatchId = batch.Id;
            PageCount = 0;
            RecentCaptures.Clear();
            StatusText = $"Batch '{BatchCode}' started for project '{ProjectCode}'";
            UpdateCaptureReadiness();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not start batch: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CaptureAsync()
    {
        if (Interlocked.Exchange(ref _captureInProgress, 1) != 0) return;
        try
        {
        if (!IsConnected) { StatusText = "Camera not connected"; return; }
        if (_currentBatchId == null) { StatusText = "Start a batch first"; return; }

        PageCount++;
        var pageStr = PageCount.ToString("D6");
        var prefix = $"{ProjectCode}_{BatchCode}_{pageStr}";

        StatusText = $"Capturing page {pageStr}...";
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            var filePath = await _cameraService.CaptureAsync(_outputDirectory, prefix);

            // Record in durable queue
            var job = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount);

            // Add thumbnail
            AddThumbnail(job.Id, filePath, PageCount);

            StatusText = $"Page {pageStr} captured — {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Capture failed: {ex.Message}";
            PageCount--; // Revert count
        }
        }
        finally { Volatile.Write(ref _captureInProgress, 0); }
    }

    [RelayCommand]
    private async Task RecaptureAsync()
    {
        if (Interlocked.Exchange(ref _captureInProgress, 1) != 0) return;
        try
        {
        if (!IsConnected || _currentBatchId == null || PageCount == 0) return;

        var pageStr = PageCount.ToString("D6");
        var prefix = $"{ProjectCode}_{BatchCode}_{pageStr}_R";

        StatusText = $"Recapturing page {pageStr}...";
        try
        {
            await _queueService.SupersedePageAsync(_currentBatchId, PageCount);
            var filePath = await _cameraService.CaptureAsync(_outputDirectory, prefix);
            var job = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount);

            // Update thumbnail for the recaptured page
            var existing = RecentCaptures.Where(t => t.PageNumber == PageCount).ToList();
            foreach (var thumbnail in existing)
            {
                thumbnail.Thumbnail?.Dispose();
                RecentCaptures.Remove(thumbnail);
            }
            AddThumbnail(job.Id, filePath, PageCount, isRecapture: true);

            StatusText = $"Page {pageStr} recaptured";
        }
        catch (Exception ex)
        {
            StatusText = $"Recapture failed: {ex.Message}";
        }
        }
        finally { Volatile.Write(ref _captureInProgress, 0); }
    }

    [RelayCommand]
    private void ToggleAutoCapture()
    {
        IsAutoCapture = !IsAutoCapture;
        StatusText = IsAutoCapture ? "AUTO CAPTURE: ON" : "AUTO CAPTURE: OFF";
    }

    private async Task LoadCameraSettingsAsync()
    {
        CameraControls.Clear();
        try
        {
            var settings = await _cameraService.GetCameraSettingsAsync();
            foreach (var setting in settings)
                CameraControls.Add(new CameraControlItem(setting, _cameraService, message => StatusText = message));
            if (CameraControls.Count == 0)
                StatusText = "Camera connected. This body did not expose configurable capture properties.";
        }
        catch (Exception ex)
        {
            StatusText = $"Camera connected, but settings could not be read: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportBatchAsync()
    {
        if (_currentBatchId == null)
        {
            StatusText = "Start a batch first before exporting.";
            return;
        }

        StatusText = $"Exporting batch {BatchCode} to {ExportFormat}...";
        try
        {
            var exportService = new MicroCapture.Processing.BatchExportService(_dbContext);
            var exportPath = await exportService.ExportBatchAsync(_currentBatchId, _outputDirectory, ExportFormat);
            StatusText = $"Exported successfully: {Path.GetFileName(exportPath)}";
        }
        catch (InvalidOperationException ex) when (ex.Message == "Images are still being processed.")
        {
            StatusText = "Images are still processing — wait for thumbnails to show Processed, then export.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    // ---------- Helpers ----------

    [RelayCommand]
    private void ReviewCrop(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) { Console.WriteLine($"[ReviewCrop] JobId is empty"); return; }
        Console.WriteLine($"[ReviewCrop] Opening crop review for {jobId}");
        var cropWindow = new CropReviewWindow();
        Console.WriteLine($"[ReviewCrop] CropReviewWindow created");
        cropWindow.DataContext = new CropReviewViewModel(jobId, _dbContext, _queueService);
        Console.WriteLine($"[ReviewCrop] CropReviewViewModel set as DataContext");
        
        // Show as a top-level window (since we don't have a direct reference to MainWindow here easily without injection, 
        // we'll just show it non-modal, or we can use Avalonia's Application.Current)
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow != null)
        {
            Console.WriteLine("[ReviewCrop] Calling Show");

            // Don't use modal dialog for this test
            cropWindow.WindowStartupLocation =
                Avalonia.Controls.WindowStartupLocation.CenterScreen;

            // Remove Topmost for now
            cropWindow.Show();

            Console.WriteLine("[ReviewCrop] Show returned");
        }
        else
        {
            Console.WriteLine($"[ReviewCrop] Calling Show (desktop context not available)");
            cropWindow.WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;
            cropWindow.Topmost = true;
            cropWindow.Show();
            Console.WriteLine($"[ReviewCrop] Show called");
        }
    }

    private void AddThumbnail(string jobId, string filePath, int pageNumber, bool isRecapture = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                var thumb = Bitmap.DecodeToWidth(stream, 120);
                RecentCaptures.Insert(0, new ThumbnailItem
                {
                    JobId = jobId,
                    PageNumber = pageNumber,
                    Thumbnail = thumb,
                    Status = isRecapture ? "Recapturing" : "Processing",
                    FilePath = filePath
                });

                // Keep last 20 thumbnails to avoid memory buildup
                while (RecentCaptures.Count > 20)
                {
                    var old = RecentCaptures[^1];
                    old.Thumbnail?.Dispose();
                    RecentCaptures.RemoveAt(RecentCaptures.Count - 1);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Thumbnail generation failed for '{filePath}': {ex}");
            }
        });
    }

    /// <summary>
    /// Called from MainWindow.axaml.cs when keyboard shortcuts are pressed.
    /// </summary>
    public void HandleKeyShortcut(string key)
    {
        switch (key)
        {
            case "Space":
                if (CaptureCommand.CanExecute(null)) CaptureCommand.Execute(null);
                break;
            case "R":
                if (RecaptureCommand.CanExecute(null)) RecaptureCommand.Execute(null);
                break;
            case "A":
                ToggleAutoCapture();
                break;
        }
    }

    public async Task ShutdownAsync()
    {
        _worker?.Stop();
        try
        {
            await _cameraService.StopLiveViewAsync();
            await _cameraService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Shutdown warning: {ex.Message}";
        }
        finally
        {
            _cameraService.Dispose();
            _dbContext.Dispose();
        }
    }
}

/// <summary>
/// Represents one item in the thumbnail strip.
/// </summary>
public partial class ThumbnailItem : ObservableObject
{
    [ObservableProperty] private int _pageNumber;
    [ObservableProperty] private string _jobId = "";
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private string _status = "Captured";
    [ObservableProperty] private string _filePath = "";
}

public partial class CameraControlItem : ObservableObject
{
    private readonly ICameraService _cameraService;
    private readonly Action<string> _report;
    public string Key { get; }
    public string DisplayName { get; }
    public IReadOnlyList<CameraSettingOption> Options { get; }
    [ObservableProperty] private CameraSettingOption? _selectedOption;

    public CameraControlItem(CameraSetting setting, ICameraService cameraService, Action<string> report)
    {
        Key = setting.Key;
        DisplayName = setting.DisplayName;
        Options = setting.Options;
        _cameraService = cameraService;
        _report = report;
        _selectedOption = Options.FirstOrDefault(option => option.Value == setting.Value) ?? Options.FirstOrDefault();
    }

    partial void OnSelectedOptionChanged(CameraSettingOption? value)
    {
        if (value == null) return;
        _ = ApplyAsync(value);
    }

    private async Task ApplyAsync(CameraSettingOption option)
    {
        if (IsBusy)
            return;

        await _settingLock.WaitAsync();

        try
        {
            IsBusy = true;

        await _cameraService.StopLiveViewAsync();

        await _cameraService.SetCameraSettingAsync(...);

        await Task.Delay(150);

        await _cameraService.StartLiveViewAsync();   
            _report($"Camera setting updated: {DisplayName} = {option.DisplayName}");
        }
        catch (Exception ex)
        {
            _report($"Could not update {DisplayName}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _settingLock.Release();
        }
    }
}
