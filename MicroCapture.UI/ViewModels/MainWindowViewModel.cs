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

    // Fixed-frame capture: UseFixedFrames is the pre-batch checkbox intent (what the *next*
    // Start Batch will do); IsFixedFrameBatch reflects whether the currently *active* batch
    // actually has calibrated frames — they're deliberately separate so toggling the checkbox
    // mid-batch can never retroactively change how the active batch behaves.
    [ObservableProperty] private bool _useFixedFrames = false;
    [ObservableProperty] private bool _isFixedFrameBatch = false;
    [ObservableProperty] private bool _isCalibrating;
    [ObservableProperty] private FrameCalibrationViewModel? _calibrationViewModel;
    // Run OCR and Export Batch must never overlap — both touch the same jobs' OCR/export
    // status, and a PDF export runs OCR itself first if it isn't already done.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunOcrCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportBatchCommand))]
    private bool _isOcrRunning;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunOcrCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportBatchCommand))]
    private bool _isExporting;
    [ObservableProperty] private string _defaultExportFormat = "PDF";
    public string[] AvailableFormats { get; } = { "PDF", "TIFF", "JPG", "PNG" };

    public bool IsAutoCaptureAvailable => !IsFixedFrameBatch;
    // Visible once the operator has expressed intent (checked the box for the next batch) OR
    // the active batch already uses fixed frames (e.g. resumed without re-checking the box).
    public bool ShowCalibrateButton => IsFixedFrameBatch || UseFixedFrames;
    public string CalibrateButtonLabel => IsFixedFrameBatch ? "Recalibrate Frames" : "Calibrate Frames";

    partial void OnUseFixedFramesChanged(bool value)
    {
        if (value) SplitBookPages = false;
        OnPropertyChanged(nameof(ShowCalibrateButton));
        OnPropertyChanged(nameof(CalibrateButtonLabel));
    }
    partial void OnSplitBookPagesChanged(bool value) { if (value) UseFixedFrames = false; }
    partial void OnIsFixedFrameBatchChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAutoCaptureAvailable));
        OnPropertyChanged(nameof(ShowCalibrateButton));
        OnPropertyChanged(nameof(CalibrateButtonLabel));
        if (value) { IsAutoCapture = false; }
    }

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
    private const double PositionSmoothingFactor = 0.35; // weight toward each new detection when updating the smoothed reference
    private int _stableFrameCount;
    // A smoothed (not raw) reference position: comparing each new detection against this
    // instead of the previous raw frame absorbs small per-frame jitter (hand tremor, minor
    // auto-exposure/focus hunting) without resetting stability progress, while a genuine
    // page swap still diverges from it quickly and resets normally.
    private (double X, double Y, double Width, double Height)? _smoothedRect;
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

    public MainWindowViewModel(ICameraService cameraService) : this(cameraService, null)
    {
    }

    /// <param name="dbPath">Overrides the database file this window and its background worker
    /// use — used by tests so they can exercise this exact class without touching the
    /// operator's real database (AppDbContext's own default path). Null (the real app's
    /// usage, via the single-argument constructor above) keeps existing behavior exactly.</param>
    public MainWindowViewModel(ICameraService cameraService, string? dbPath)
    {
        _cameraService = cameraService;
        _dbContext = dbPath == null ? new AppDbContext() : new AppDbContext(dbPath);
        _queueService = new CaptureQueueService(_dbContext);

        _worker = new MicroCapture.Processing.BackgroundProcessingWorker(dbPath);
        _worker.StatusChanged += (s, msg) => {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = $"Background: {msg}");
        };
        _worker.JobCompleted += (s, result) => {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // For an ordinary (non-fixed-frame) job this matches exactly one thumbnail at
                // FrameIndex 0 — identical to the old single-thumbnail behavior. For a
                // fixed-frame job, AddThumbnail already inserted one placeholder per calibrated
                // frame, sharing this same FilePath; each gets its own slice of OutputFilePaths.
                var thumbnails = RecentCaptures.Where(t => t.FilePath == result.OriginalFilePath);
                foreach (var thumbnail in thumbnails)
                {
                    thumbnail.Status = !result.Success ? "Processing failed"
                        : result.OcrStatus == "Failed" ? "Processed — OCR failed"
                        : result.QcVerdict == "FAIL" ? "Processed — QC fail"
                        : result.QcVerdict == "WARNING" ? "Processed — needs review"
                        : "Processed";

                    if (result.Success && thumbnail.FrameIndex >= 0 && thumbnail.FrameIndex < result.OutputFilePaths.Count)
                    {
                        try
                        {
                            // The processed derivative is a TIFF that Avalonia's Skia-backed
                            // Bitmap decoder can't read directly — bridge through the same
                            // OpenCV-based decode path batch export uses, or the thumbnail
                            // silently never updates past the raw just-captured preview.
                            var bytes = MicroCapture.Processing.ImageDecodeHelper.GetDisplayBytes(result.OutputFilePaths[thumbnail.FrameIndex]);
                            if (bytes != null)
                            {
                                using var stream = new MemoryStream(bytes);
                                var newThumb = Bitmap.DecodeToWidth(stream, 120);
                                var old = thumbnail.Thumbnail;
                                thumbnail.Thumbnail = newThumb;
                                old?.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Thumbnail refresh failed for '{result.OutputFilePaths[thumbnail.FrameIndex]}': {ex}");
                        }
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
                    // Fixed-frame batches don't need (or want) contour detection — the crop
                    // geometry is already calibrated, and running detection here would just
                    // burn CPU and show meaningless boundary/stability messaging.
                    if (IsFixedFrameBatch)
                    {
                        DocumentStatus = "✓ Fixed frames — position paper, press CAPTURE";
                    }
                    // Boundary detection is throttled to keep the live-view path
                    // responsive while still providing a meaningful capture-readiness gate.
                    else if (DateTime.UtcNow - _lastDocumentCheckUtc >= TimeSpan.FromMilliseconds(500))
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
            _smoothedRect = null;
            _lastDetectedSignature = null;
            DocumentStatus = "Waiting for boundary";
            return;
        }

        var rect = ((double)check.X, (double)check.Y, (double)check.Width, (double)check.Height);
        _lastDetectedSignature = check.ContentSignature;

        if (!IsAutoCapture)
        {
            _stableFrameCount = 0;
            _smoothedRect = rect;
            DocumentStatus = "✓ Boundary detected";
            return;
        }

        if (check.Sharpness < LiveSharpnessThreshold)
        {
            _stableFrameCount = 0;
            _smoothedRect = rect;
            DocumentStatus = "✓ Boundary detected — focusing…";
            return;
        }

        // Compare against the smoothed reference from before this update, then blend it
        // toward the new detection — comparing against the raw previous frame instead would
        // make ordinary hand/camera jitter reset stability far too often.
        var previousSmoothed = _smoothedRect;
        var wasStable = previousSmoothed.HasValue && IsRectStable(previousSmoothed.Value, rect, check.ImageWidth, check.ImageHeight);
        _smoothedRect = previousSmoothed.HasValue ? LerpRect(previousSmoothed.Value, rect, PositionSmoothingFactor) : rect;
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

    private static bool IsRectStable((double X, double Y, double Width, double Height) a, (double X, double Y, double Width, double Height) b, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return false;
        var toleranceX = imageWidth * StablePositionToleranceFraction;
        var toleranceY = imageHeight * StablePositionToleranceFraction;
        return Math.Abs(a.X - b.X) <= toleranceX && Math.Abs(a.Width - b.Width) <= toleranceX &&
               Math.Abs(a.Y - b.Y) <= toleranceY && Math.Abs(a.Height - b.Height) <= toleranceY;
    }

    private static (double X, double Y, double Width, double Height) LerpRect(
        (double X, double Y, double Width, double Height) from,
        (double X, double Y, double Width, double Height) to,
        double t) => (
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            from.Width + (to.Width - from.Width) * t,
            from.Height + (to.Height - from.Height) * t);

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
                IsFixedFrameBatch = batch.UseFixedFrames;
                RefreshFixedFrameCache(batch);
                ExportFormat = batch.PreferredExportFormat;
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
                    SplitBookPages = SplitBookPages && !UseFixedFrames,
                    PreferredExportFormat = DefaultExportFormat
                };
                _dbContext.Batches.Add(batch);
                await _dbContext.SaveChangesAsync();

                _currentBatchId = batch.Id;
                PageCount = 0;
                RecentCaptures.Clear();
                IsFixedFrameBatch = false;
                RefreshFixedFrameCache(null);
                ExportFormat = DefaultExportFormat;
                StatusText = $"Batch '{batchCode}' started for project '{projectCode}'";

                if (UseFixedFrames)
                {
                    if (IsConnected)
                    {
                        await CalibrateFramesAsync();
                    }
                    else
                    {
                        StatusText = $"Batch '{batchCode}' started — connect the camera and use \"Calibrate Frames\" to set up fixed frames.";
                    }
                }
            }

            UpdateCaptureReadiness();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not start batch: {ex.Message}";
        }
    }

    /// <summary>Opens the fixed-frame calibration panel for the active batch — used both for
    /// first-time calibration (right after Start Batch, if "Use Fixed Frames" was checked) and
    /// for recalibrating an already-fixed-frame batch later (e.g. the rig shifted). Takes one
    /// throwaway full-res shot as the calibration image (never enqueued as a real capture), then
    /// shows the calibration panel inline in place of live view until it's saved or cancelled.</summary>
    [RelayCommand]
    private async Task CalibrateFramesAsync()
    {
        if (_currentBatchId == null) { StatusText = "Start a batch first."; return; }
        if (!IsConnected) { StatusText = "Connect the camera before calibrating fixed frames."; return; }
        // Shares the same re-entrancy guard as Capture/Recapture: without this, an ordinary
        // Capture click while the calibration shot is in flight would silently queue behind
        // the camera service's own capture lock for up to that shot's full timeout window.
        if (Interlocked.Exchange(ref _captureInProgress, 1) != 0)
        {
            StatusText = "A capture is already in progress.";
            return;
        }

        try
        {
            // A job still mid-flight when frames change could otherwise get cropped with the new
            // geometry instead of the one that was active when it was captured — block rather than
            // risk that race. Nothing heavier (e.g. a per-job geometry snapshot) is needed: once
            // this check passes, every already-Completed job is an immutable historical artifact
            // that nothing reprocesses automatically.
            var pendingCount = await _dbContext.CaptureJobs.CountAsync(j =>
                j.BatchId == _currentBatchId && (j.ProcessingStatus == "Pending" || j.ProcessingStatus == "InProgress"));
            if (pendingCount > 0)
            {
                StatusText = $"{pendingCount} page(s) still processing under the current frames — wait for them to finish before recalibrating.";
                return;
            }

            var batch = await _dbContext.Batches.FindAsync(_currentBatchId);
            if (batch == null) return;

            uint? originalQuality = null;
            var qualityChanged = false;

            try
            {
                // The calibration shot always uses plain JPEG, regardless of whatever quality
                // the operator has selected for real page captures — it's the only format the
                // calibration panel can load as a Bitmap, and keeps calibration fast even when
                // the batch itself is shooting RAW.
                try
                {
                    var settings = await _cameraService.GetCameraSettingsAsync();
                    var qualitySetting = settings.FirstOrDefault(s => s.Key == "ImageQuality");
                    if (qualitySetting != null)
                    {
                        originalQuality = qualitySetting.Value;
                        var jpegOption = qualitySetting.Options.FirstOrDefault(o => o.DisplayName == "Jpeg Large Fine")
                            ?? qualitySetting.Options.FirstOrDefault(o => o.DisplayName.StartsWith("Jpeg ") && !o.DisplayName.Contains('+'));
                        if (jpegOption != null && jpegOption.Value != originalQuality.Value)
                        {
                            await _cameraService.SetCameraSettingAsync("ImageQuality", jpegOption.Value);
                            qualityChanged = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CalibrateFramesAsync] Could not force JPEG quality for calibration shot: {ex}");
                }

                var calibrationDir = Path.Combine(_outputDirectory, "Calibration");
                Directory.CreateDirectory(calibrationDir);
                StatusText = "Capturing calibration frame...";
                var calibrationPath = await _cameraService.CaptureAsync(calibrationDir, $"calibration_{DateTime.Now:yyyyMMdd_HHmmss}");

                var calibrationViewModel = new FrameCalibrationViewModel(batch, _dbContext, calibrationPath);
                var tcs = new TaskCompletionSource<bool>();
                calibrationViewModel.Saved += (_, _) => tcs.TrySetResult(true);
                calibrationViewModel.Cancelled += (_, _) => tcs.TrySetResult(false);

                CalibrationViewModel = calibrationViewModel;
                IsCalibrating = true;

                var saved = await tcs.Task;

                IsCalibrating = false;
                CalibrationViewModel = null;

                IsFixedFrameBatch = batch.UseFixedFrames;
                RefreshFixedFrameCache(batch);
                StatusText = saved ? "Fixed frames calibrated." : "Calibration cancelled.";
            }
            catch (Exception ex)
            {
                IsCalibrating = false;
                CalibrationViewModel = null;
                StatusText = $"Calibration failed: {ex.Message}";
            }
            finally
            {
                if (qualityChanged && originalQuality.HasValue)
                {
                    try { await _cameraService.SetCameraSettingAsync("ImageQuality", originalQuality.Value); }
                    catch (Exception restoreEx)
                    {
                        Console.Error.WriteLine($"[CalibrateFramesAsync] Could not restore original image quality: {restoreEx}");
                        StatusText += " (warning: could not restore original image quality — check camera settings)";
                    }
                }
            }
        }
        finally { Volatile.Write(ref _captureInProgress, 0); }
    }

    /// <summary>Rebuilds the thumbnail strip from a resumed batch's most recent, non-superseded capture per page.</summary>
    private async Task LoadRecentCapturesFromBatchAsync(Batch batch)
    {
        RecentCaptures.Clear();
        var frameCount = batch.UseFixedFrames && !string.IsNullOrWhiteSpace(batch.FixedFrames)
            ? Math.Max(1, MicroCapture.Processing.ImageProcessor.ParseFixedFrames(batch.FixedFrames).Length)
            : 1;

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
                // This reloads the raw capture, not each frame's actual processed crop — an
                // existing limitation shared with ordinary batches (resume never re-fetches
                // per-frame derivatives), not something new here.
                var bytes = await Task.Run(() => File.ReadAllBytes(job.OriginalFilePath));
                var status = job.ProcessingStatus == "Completed" ? "Processed"
                    : job.ProcessingStatus == "Failed" ? "Processing failed"
                    : "Processing";

                for (var i = 0; i < frameCount; i++)
                {
                    using var stream = new MemoryStream(bytes);
                    var thumb = await Task.Run(() => Bitmap.DecodeToWidth(stream, 120));
                    RecentCaptures.Add(new ThumbnailItem
                    {
                        JobId = job.Id,
                        PageNumber = job.PageNumber,
                        FrameIndex = i,
                        Thumbnail = thumb,
                        BorderColor = frameCount > 1 ? new Avalonia.Media.SolidColorBrush(FixedFrameColorPalette.GetColor(i)) : null,
                        Status = status,
                        FilePath = job.OriginalFilePath
                    });
                }
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
        if (IsCalibrating) { StatusText = "Finish or cancel calibration before capturing."; return; }
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
        if (IsCalibrating) { StatusText = "Finish or cancel calibration before capturing."; return; }
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
        _smoothedRect = null;
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

    /// <summary>Manual focus nudge, bound to the Focus panel's Near/Far buttons.
    /// <paramref name="step"/> is a MicroCapture.Core.Interfaces.FocusStep name
    /// (e.g. "NearSmall", "FarLarge") passed as the button's CommandParameter.</summary>
    [RelayCommand]
    private async Task NudgeFocusAsync(string step)
    {
        if (!IsConnected) { StatusText = "Connect the camera before adjusting focus."; return; }
        try
        {
            var parsed = Enum.Parse<MicroCapture.Core.Interfaces.FocusStep>(step);
            await _cameraService.NudgeFocusAsync(parsed);
        }
        catch (Exception ex)
        {
            StatusText = $"Focus adjustment failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task TriggerAutoFocusAsync()
    {
        if (!IsConnected) { StatusText = "Connect the camera before triggering autofocus."; return; }
        try
        {
            await _cameraService.TriggerAutoFocusAsync();
            StatusText = "Autofocus triggered.";
        }
        catch (Exception ex)
        {
            StatusText = $"Autofocus failed: {ex.Message}";
        }
    }

    private bool CanRunOcrOrExport() => !IsOcrRunning && !IsExporting;

    [RelayCommand(CanExecute = nameof(CanRunOcrOrExport))]
    private async Task RunOcrAsync()
    {
        if (_currentBatchId == null) { StatusText = "Start a batch first."; return; }

        IsOcrRunning = true;
        try
        {
            await RunOcrForCurrentBatchAsync();
        }
        finally
        {
            IsOcrRunning = false;
        }
    }

    /// <summary>Runs OCR for the active batch's finalized page set. Shared by the explicit
    /// "Run OCR" button and by PDF export (which needs the text before it can embed it) —
    /// idempotent, since BatchOcrService only touches jobs not already OcrStatus "Completed".</summary>
    private async Task RunOcrForCurrentBatchAsync()
    {
        var ocrService = new MicroCapture.Processing.BatchOcrService(_dbContext);
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            StatusText = p.Total == 0 ? "OCR: nothing to do." : $"OCR: {p.Done}/{p.Total} pages...";
        });
        try
        {
            await ocrService.RunOcrForBatchAsync(_currentBatchId!, progress);
            StatusText = "OCR complete.";
        }
        catch (Exception ex)
        {
            StatusText = $"OCR failed: {ex.Message}";
        }

        // Refresh OcrStatus on whatever thumbnails are currently visible for this batch.
        var refreshed = await _dbContext.CaptureJobs.AsNoTracking()
            .Where(j => j.BatchId == _currentBatchId)
            .ToDictionaryAsync(j => j.Id, j => j.OcrStatus);
        foreach (var thumbnail in RecentCaptures)
        {
            if (refreshed.TryGetValue(thumbnail.JobId, out var ocrStatus))
                thumbnail.OcrStatus = ocrStatus;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunOcrOrExport))]
    private async Task ExportBatchAsync()
    {
        if (_currentBatchId == null)
        {
            StatusText = "Start a batch first before exporting.";
            return;
        }

        IsExporting = true;
        try
        {
            // Only PDF export ever reads OCR text (a near-invisible searchable text layer) —
            // TIFF/JPG/PNG export never touches it, so skip straight to export for those.
            if (string.Equals(ExportFormat, "PDF", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "Preparing searchable text...";
                await RunOcrForCurrentBatchAsync();
            }

            StatusText = $"Exporting batch {BatchCode} to {ExportFormat}...";
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
        finally
        {
            IsExporting = false;
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
        var cropReviewViewModel = new CropReviewViewModel(jobId, _dbContext, _queueService);
        // Give the thumbnail immediate feedback on save instead of leaving it looking
        // unchanged for the ~1s the background worker takes to actually pick the job back up.
        cropReviewViewModel.Saved += (_, _) =>
        {
            var thumbnail = RecentCaptures.FirstOrDefault(t => t.JobId == jobId);
            if (thumbnail != null) thumbnail.Status = "Reprocessing…";
        };
        cropWindow.DataContext = cropReviewViewModel;
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

    /// <summary>Number of fixed frames the active batch is currently calibrated with, or 1 for
    /// an ordinary (non-fixed-frame) batch — used to decide how many placeholder thumbnails one
    /// capture should produce.</summary>
    private int GetCurrentFixedFrameCount()
    {
        if (!IsFixedFrameBatch || _currentBatchId == null) return 1;
        var batch = _dbContext.Batches.Find(_currentBatchId);
        if (batch?.UseFixedFrames != true || string.IsNullOrWhiteSpace(batch.FixedFrames)) return 1;
        var count = MicroCapture.Processing.ImageProcessor.ParseFixedFrames(batch.FixedFrames).Length;
        return count > 0 ? count : 1;
    }

    // Parsed fixed-frame geometry for the active batch, read by MainWindow.axaml.cs to draw a
    // read-only outline over the live view. Kept as a small cache (refreshed only when the
    // active batch's frames actually change) rather than re-parsing FixedFrames on every ~30fps
    // live-view frame.
    public MicroCapture.Processing.FixedFrameRect[] CurrentFixedFrames { get; private set; } = Array.Empty<MicroCapture.Processing.FixedFrameRect>();
    public int CurrentFixedFrameImageWidth { get; private set; }
    public int CurrentFixedFrameImageHeight { get; private set; }

    private void RefreshFixedFrameCache(Batch? batch)
    {
        if (batch != null && batch.UseFixedFrames && !string.IsNullOrWhiteSpace(batch.FixedFrames))
        {
            CurrentFixedFrames = MicroCapture.Processing.ImageProcessor.ParseFixedFrames(batch.FixedFrames);
            CurrentFixedFrameImageWidth = batch.FixedFrameImageWidth;
            CurrentFixedFrameImageHeight = batch.FixedFrameImageHeight;
        }
        else
        {
            CurrentFixedFrames = Array.Empty<MicroCapture.Processing.FixedFrameRect>();
            CurrentFixedFrameImageWidth = 0;
            CurrentFixedFrameImageHeight = 0;
        }
        OnPropertyChanged(nameof(CurrentFixedFrames));
    }

    private void AddThumbnail(string jobId, string filePath, int pageNumber, bool isRecapture = false)
    {
        var frameCount = GetCurrentFixedFrameCount();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);

                // Inserted in reverse so frame 0 ends up first/leftmost in the strip.
                for (var i = frameCount - 1; i >= 0; i--)
                {
                    using var stream = new MemoryStream(bytes);
                    var thumb = Bitmap.DecodeToWidth(stream, 120);
                    RecentCaptures.Insert(0, new ThumbnailItem
                    {
                        JobId = jobId,
                        PageNumber = pageNumber,
                        FrameIndex = i,
                        Thumbnail = thumb,
                        BorderColor = frameCount > 1 ? new Avalonia.Media.SolidColorBrush(FixedFrameColorPalette.GetColor(i)) : null,
                        Status = isRecapture ? "Recapturing" : "Processing",
                        FilePath = filePath
                    });
                }

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
    /// Removes a mistakenly captured page: marks it Superseded (excluded from processing and
    /// export, same as a recapture) and drops it from the thumbnail strip. The original file
    /// is left on disk — consistent with how a recapture already preserves prior attempts —
    /// but any processed derivative is deleted since it would otherwise sit unused forever.
    /// Called from MainWindow.axaml.cs's delete button on each thumbnail.
    /// </summary>
    public async Task DeleteCaptureAsync(ThumbnailItem item)
    {
        await _queueService.DeleteCaptureAsync(item.JobId);

        // Deleting the most recently captured page is effectively "undo that shot" — the next
        // real capture should reuse its page number, not leave a permanent gap. Deleting an
        // earlier page in the batch is different: PageCount must stay put, since decrementing
        // it would make the next capture collide with a page number that's still in use.
        // (A gap from deleting a non-tail page is harmless — export renumbers sequentially.)
        if (item.PageNumber == PageCount)
            PageCount--;

        try
        {
            var processedDir = Path.Combine(Path.GetDirectoryName(item.FilePath) ?? ".", "Processed");
            var baseName = Path.GetFileNameWithoutExtension(item.FilePath);
            if (Directory.Exists(processedDir))
            {
                foreach (var derivative in Directory.EnumerateFiles(processedDir, $"{baseName}*"))
                {
                    try { File.Delete(derivative); }
                    catch (IOException) { /* best-effort cleanup; the DB status is what actually excludes it */ }
                    catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Processed-derivative cleanup failed for '{item.FilePath}': {ex}");
        }

        // A fixed-frame capture has one thumbnail per frame sharing this JobId — deleting the
        // underlying job (above) affects all of them together, so the thumbnail strip must too.
        foreach (var sibling in RecentCaptures.Where(t => t.JobId == item.JobId).ToList())
        {
            sibling.Thumbnail?.Dispose();
            RecentCaptures.Remove(sibling);
        }
        StatusText = $"Page {item.PageNumber:D6} removed.";
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
    // OCR is on-demand (Run OCR / before a PDF export), not automatic on capture, so this is
    // tracked independently of Status rather than folded into it.
    [ObservableProperty] private string _ocrStatus = "Pending";
    [ObservableProperty] private string _filePath = "";

    // Which fixed frame this thumbnail represents within its capture (0 for an ordinary,
    // non-fixed-frame capture — always exactly one thumbnail per job in that case).
    [ObservableProperty] private int _frameIndex;
    // Non-null only for fixed-frame captures — colors the thumbnail's border to match its
    // on-canvas frame. Null for ordinary captures, which keep the default neutral border.
    [ObservableProperty] private Avalonia.Media.IBrush? _borderColor;
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
