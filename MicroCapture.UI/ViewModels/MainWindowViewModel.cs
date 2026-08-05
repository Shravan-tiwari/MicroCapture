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
using Microsoft.EntityFrameworkCore;
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
    // Snapshotted at StartBatchAsync, sanitized so operator-entered text can never
    // escape the intended output directory or produce an invalid filename.
    private string _activeProjectCode = string.Empty;
    private string _activeBatchCode = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _connectedCameraModel = "Not connected";
    private int _liveViewFramePending;
    private int _captureInProgress;
    private DateTime _lastDocumentCheckUtc = DateTime.MinValue;

    // Auto-capture state machine: fires the shutter automatically once a page has been
    // stable, in-focus, and different from whatever was last captured for
    // StableFramesRequired consecutive checks. See UpdateDocumentStatus.
    private const int StableFramesRequired = 3; // ~1.5s at the existing 500ms check interval
    private const double LiveSharpnessThreshold = 40.0; // live-view frames are lower detail than a full capture, so a lower bar than the QC BlurThreshold (100)
    private const double StablePositionToleranceFraction = 0.03; // allowed drift between checks, as a fraction of frame size
    private const double ContentChangeThreshold = 18.0; // mean abs pixel difference (0-255) considered a genuinely different page
    private int _stableFrameCount;
    private (int X, int Y, int Width, int Height)? _lastDetectedRect;
    private byte[]? _lastDetectedSignature;
    private byte[]? _lastCapturedSignature;

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
                {
                    thumbnail.Status = !result.Success ? "Processing failed"
                        : result.OcrStatus == "Failed" ? "Processed — OCR failed"
                        : result.QcVerdict == "FAIL" ? "Processed — QC warning"
                        : "Processed";

                    if (result.Success && result.OutputFilePaths.Count > 0 && File.Exists(result.OutputFilePaths[0]))
                    {
                        try
                        {
                            using var stream = File.OpenRead(result.OutputFilePaths[0]);
                            var newThumb = Bitmap.DecodeToWidth(stream, 120);
                            var old = thumbnail.Thumbnail;
                            thumbnail.Thumbnail = newThumb;
                            old?.Dispose();
                        }
                        catch { }
                    }
                }
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
                    // responsive while still providing a meaningful capture-readiness gate.
                    if (DateTime.UtcNow - _lastDocumentCheckUtc >= TimeSpan.FromMilliseconds(500))
                    {
                        _lastDocumentCheckUtc = DateTime.UtcNow;
                        var check = MicroCapture.Processing.ImageProcessor.CheckLiveFrame(frameBytes);
                        UpdateDocumentStatus(check);
                    }
                    FocusStatus = "Camera-controlled";
                    ExposureStatus = "Camera-controlled";
                    UpdateCaptureReadiness();
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
        else if (IsAutoCapture)
            CaptureReadiness = DocumentStatus.StartsWith("✓") ? "AUTO CAPTURE ACTIVE" : "WAITING FOR DOCUMENT";
        else
            CaptureReadiness = DocumentStatus.StartsWith("✓") ? "READY TO CAPTURE" : "WAITING FOR DOCUMENT";
    }

    /// <summary>Auto-capture state machine. Fires the shutter automatically once a page has
    /// held stable and in focus for <see cref="StableFramesRequired"/> consecutive checks and
    /// its content actually differs from whatever was last captured — content, not just
    /// position, because a fixed copy-stand/page guide places every page in nearly the same
    /// spot, so position alone can't tell a page turn from the same page still sitting there.
    /// When <see cref="IsAutoCapture"/> is off, behavior is unchanged from a simple
    /// boundary-present/absent check with no stability, focus, or auto-firing.</summary>
    private void UpdateDocumentStatus(MicroCapture.Processing.LiveFrameCheck check)
    {
        if (!check.Detected)
        {
            _stableFrameCount = 0;
            _lastDetectedRect = null;
            _lastDetectedSignature = null;
            DocumentStatus = "Waiting for boundary";
            return;
        }

        var rect = (check.X, check.Y, check.Width, check.Height);
        _lastDetectedSignature = check.ContentSignature;

        if (!IsAutoCapture)
        {
            _stableFrameCount = 0;
            _lastDetectedRect = rect;
            DocumentStatus = "✓ Boundary detected";
            return;
        }

        if (check.Sharpness < LiveSharpnessThreshold)
        {
            _stableFrameCount = 0;
            _lastDetectedRect = rect;
            DocumentStatus = "✓ Boundary detected — focusing…";
            return;
        }

        var wasStable = _lastDetectedRect.HasValue && IsRectStable(_lastDetectedRect.Value, rect, check.ImageWidth, check.ImageHeight);
        _lastDetectedRect = rect;
        _stableFrameCount = wasStable ? Math.Min(_stableFrameCount + 1, StableFramesRequired) : 1;

        if (_stableFrameCount < StableFramesRequired)
        {
            DocumentStatus = "✓ Boundary detected — hold still…";
            return;
        }

        var contentDiff = ContentDifference(check.ContentSignature, _lastCapturedSignature);
        var isSameAsLastCapture = _lastCapturedSignature != null && contentDiff < ContentChangeThreshold;
        if (isSameAsLastCapture)
        {
            DocumentStatus = "✓ Captured — swap page to continue";
            return;
        }

        if (Volatile.Read(ref _captureInProgress) != 0)
        {
            DocumentStatus = "✓ Capturing…";
            return;
        }

        DocumentStatus = "✓ Capturing…";
        _lastCapturedSignature = check.ContentSignature;
        _stableFrameCount = 0;
        _ = CaptureAsync();
    }

    /// <summary>Mean absolute difference between two content signatures (0-255 scale).
    /// Missing or mismatched signatures are treated as "definitely different" so a
    /// comparison failure never blocks a real capture.</summary>
    private static double ContentDifference(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0) return double.MaxValue;
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    private static bool IsRectStable((int X, int Y, int Width, int Height) a, (int X, int Y, int Width, int Height) b, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return false;
        var toleranceX = imageWidth * StablePositionToleranceFraction;
        var toleranceY = imageHeight * StablePositionToleranceFraction;
        return Math.Abs(a.X - b.X) <= toleranceX && Math.Abs(a.Width - b.Width) <= toleranceX &&
               Math.Abs(a.Y - b.Y) <= toleranceY && Math.Abs(a.Height - b.Height) <= toleranceY;
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
            var projectCode = MicroCapture.Core.FileNaming.Sanitize(ProjectCode);
            var batchCode = MicroCapture.Core.FileNaming.Sanitize(BatchCode);

            // Ensure project exists
            var project = _dbContext.Projects.FirstOrDefault(p => p.Name == projectCode);
            if (project == null)
            {
                project = new Project
                {
                    Name = projectCode,
                    Customer = "",
                    Description = "Auto-created from scanning session",
                    CreatedBy = Environment.UserName,
                    OutputDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        "MicroCapture", projectCode)
                };
                _dbContext.Projects.Add(project);
                await _dbContext.SaveChangesAsync();
            }
            _currentProjectId = project.Id;
            _activeProjectCode = projectCode;
            _activeBatchCode = batchCode;
            _outputDirectory = project.OutputDirectory;

            // Resume an existing active batch with the same code instead of always
            // creating a new one — otherwise a restart mid-batch (crash, power loss)
            // silently orphans every page captured before it and starts numbering over.
            var batch = await _dbContext.Batches
                .Include(b => b.Captures)
                .FirstOrDefaultAsync(b => b.ProjectId == project.Id && b.BatchCode == batchCode && b.Status == "Active");

            if (batch != null)
            {
                _currentBatchId = batch.Id;
                PageCount = batch.Captures.Count > 0 ? batch.Captures.Max(c => c.PageNumber) : 0;
                await LoadRecentCapturesFromBatchAsync(batch);
                StatusText = $"Resumed batch '{batchCode}' for project '{projectCode}' at page {PageCount}";
            }
            else
            {
                batch = new Batch
                {
                    ProjectId = project.Id,
                    Name = batchCode,
                    BatchCode = batchCode,
                    Operator = Environment.UserName,
                    SplitBookPages = SplitBookPages
                };
                _dbContext.Batches.Add(batch);
                await _dbContext.SaveChangesAsync();

                _currentBatchId = batch.Id;
                PageCount = 0;
                RecentCaptures.Clear();
                StatusText = $"Batch '{batchCode}' started for project '{projectCode}'";
            }

            UpdateCaptureReadiness();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not start batch: {ex.Message}";
        }
    }

    /// <summary>Rebuilds the thumbnail strip from a resumed batch's most recent, non-superseded capture per page.</summary>
    private async Task LoadRecentCapturesFromBatchAsync(Batch batch)
    {
        RecentCaptures.Clear();
        var latestPerPage = batch.Captures
            .Where(job => job.ProcessingStatus != "Superseded")
            .GroupBy(job => job.PageNumber)
            .Select(group => group.OrderByDescending(job => job.Timestamp).First())
            .OrderByDescending(job => job.PageNumber)
            .Take(20);

        foreach (var job in latestPerPage)
        {
            if (!File.Exists(job.OriginalFilePath)) continue;
            try
            {
                using var stream = File.OpenRead(job.OriginalFilePath);
                var thumb = await Task.Run(() => Bitmap.DecodeToWidth(stream, 120));
                RecentCaptures.Add(new ThumbnailItem
                {
                    JobId = job.Id,
                    PageNumber = job.PageNumber,
                    Thumbnail = thumb,
                    Status = job.ProcessingStatus == "Completed" ? "Processed"
                        : job.ProcessingStatus == "Failed" ? "Processing failed"
                        : "Processing",
                    FilePath = job.OriginalFilePath
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Thumbnail load failed for '{job.OriginalFilePath}': {ex}");
            }
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
        var prefix = $"{_activeProjectCode}_{_activeBatchCode}_{pageStr}";

        StatusText = $"Capturing page {pageStr}...";
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            var filePath = await _cameraService.CaptureAsync(_outputDirectory, prefix);

            // Record in durable queue
            var job = await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount);

            // Add thumbnail
            AddThumbnail(job.Id, filePath, PageCount);

            // Require the page's content to actually change before auto-capture (or the
            // readiness indicator) can trigger again for this same physical page.
            _lastCapturedSignature = _lastDetectedSignature;
            _stableFrameCount = 0;

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
        var prefix = $"{_activeProjectCode}_{_activeBatchCode}_{pageStr}_R";

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

            _lastCapturedSignature = _lastDetectedSignature;
            _stableFrameCount = 0;

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
        // Start the stability state fresh so a stale reading from before the toggle can't
        // immediately fire — but deliberately keep _lastCapturedSignature, so toggling AUTO
        // off and back on while the same page is still sitting there doesn't re-fire for it.
        _stableFrameCount = 0;
        _lastDetectedRect = null;
        StatusText = IsAutoCapture
            ? "Auto-capture: ON — captures automatically once a page is stable, in focus, and new."
            : "Auto-capture: OFF";
        UpdateCaptureReadiness();
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
    private readonly SemaphoreSlim _settingLock = new(1, 1);
    [ObservableProperty] private bool _isBusy;
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

        await _cameraService.SetCameraSettingAsync(Key, option.Value);

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
