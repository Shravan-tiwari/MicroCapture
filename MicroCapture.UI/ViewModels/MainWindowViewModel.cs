using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MicroCapture.Core.Data;
using MicroCapture.Core.Interfaces;
using MicroCapture.Core.Models;
using MicroCapture.Core.Services;

namespace MicroCapture.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICameraService _cameraService;
    private readonly AppDbContext _dbContext;
    private readonly CaptureQueueService _queueService;

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

    private string? _currentProjectId;
    private string? _currentBatchId;
    private string _outputDirectory = string.Empty;

    // Thumbnail items for recent captures
    public ObservableCollection<ThumbnailItem> RecentCaptures { get; } = new();

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

        _cameraService.StateChanged += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsConnected = e.IsConnected;
                ConnectionStatus = e.IsConnected ? "CONNECTED" : "DISCONNECTED";
                CameraModel = e.IsConnected ? "Canon EOS R8 (Mock)" : "Not connected";
                StatusText = e.StatusMessage;
                UpdateCaptureReadiness();
            });
        };

        _cameraService.LiveViewFrameReceived += (s, frameBytes) =>
        {
            try
            {
                using var ms = new MemoryStream(frameBytes);
                var bitmap = new Bitmap(ms);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var old = LiveViewImage;
                    LiveViewImage = bitmap;
                    old?.Dispose();
                    // Simulate document detection for mock
                    FocusStatus = "✓ OK";
                    ExposureStatus = "✓ OK";
                    DocumentStatus = "✓ Detected";
                    UpdateCaptureReadiness();
                });
            }
            catch (Exception)
            {
                // Silently skip corrupt frames
            }
        };
    }

    private void UpdateCaptureReadiness()
    {
        if (!IsConnected)
            CaptureReadiness = "NOT READY";
        else if (string.IsNullOrWhiteSpace(ProjectCode) || string.IsNullOrWhiteSpace(BatchCode))
            CaptureReadiness = "SET PROJECT & BATCH";
        else
            CaptureReadiness = "READY TO CAPTURE";
    }

    // ---------- Commands ----------

    [RelayCommand]
    private async Task ConnectAsync()
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
        if (first != null)
        {
            await _cameraService.ConnectAsync(first.Id);
            await _cameraService.StartLiveViewAsync();
        }
        else
        {
            StatusText = "No cameras found";
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
            Operator = Environment.UserName
        };
        _dbContext.Batches.Add(batch);
        await _dbContext.SaveChangesAsync();
        _currentBatchId = batch.Id;

        PageCount = 0;
        RecentCaptures.Clear();
        StatusText = $"Batch '{BatchCode}' started for project '{ProjectCode}'";
        UpdateCaptureReadiness();
    }

    [RelayCommand]
    private async Task CaptureAsync()
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
            await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount);

            // Add thumbnail
            AddThumbnail(filePath, PageCount);

            StatusText = $"Page {pageStr} captured — {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Capture failed: {ex.Message}";
            PageCount--; // Revert count
        }
    }

    [RelayCommand]
    private async Task RecaptureAsync()
    {
        if (!IsConnected || _currentBatchId == null || PageCount == 0) return;

        var pageStr = PageCount.ToString("D6");
        var prefix = $"{ProjectCode}_{BatchCode}_{pageStr}_R";

        StatusText = $"Recapturing page {pageStr}...";
        try
        {
            var filePath = await _cameraService.CaptureAsync(_outputDirectory, prefix);
            await _queueService.EnqueueCaptureAsync(_currentBatchId, filePath, PageCount);

            // Update thumbnail for the recaptured page
            var existing = RecentCaptures.FirstOrDefault(t => t.PageNumber == PageCount);
            if (existing != null)
            {
                existing.Status = "Recaptured";
            }
            AddThumbnail(filePath, PageCount, isRecapture: true);

            StatusText = $"Page {pageStr} recaptured";
        }
        catch (Exception ex)
        {
            StatusText = $"Recapture failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleAutoCapture()
    {
        IsAutoCapture = !IsAutoCapture;
        StatusText = IsAutoCapture ? "AUTO CAPTURE: ON" : "AUTO CAPTURE: OFF";
    }

    // ---------- Helpers ----------

    private void AddThumbnail(string filePath, int pageNumber, bool isRecapture = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                var thumb = Bitmap.DecodeToWidth(stream, 120);
                RecentCaptures.Insert(0, new ThumbnailItem
                {
                    PageNumber = pageNumber,
                    Thumbnail = thumb,
                    Status = isRecapture ? "Recaptured" : "Captured",
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
            catch (Exception)
            {
                // Non-critical: skip thumbnail if file can't be read
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
}

/// <summary>
/// Represents one item in the thumbnail strip.
/// </summary>
public partial class ThumbnailItem : ObservableObject
{
    [ObservableProperty] private int _pageNumber;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private string _status = "Captured";
    [ObservableProperty] private string _filePath = "";
}
