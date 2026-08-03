using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MicroCapture.UI.ViewModels;
using MicroCapture.UI.Views;
using System;
using System.IO;
using System.Threading.Tasks;

namespace MicroCapture.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigureUnhandledExceptionLogging();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MicroCapture.Core.Interfaces.ICameraService cameraService;
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                cameraService = new MicroCapture.Camera.Canon.CanonCameraService();
            }
            else
            {
                cameraService = new MicroCapture.Camera.MockCameraService();
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(cameraService),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureUnhandledExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogUnhandled("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogUnhandled("Task", args.Exception);
            args.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            LogUnhandled("UI", args.Exception);
            args.Handled = true;
        };
    }

    private static void LogUnhandled(string source, Exception? exception)
    {
        if (exception == null) return;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicroCapture", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "application-errors.log"), $"[{DateTimeOffset.Now:O}] {source}: {exception}{Environment.NewLine}");
        }
        catch { /* A logging failure must not hide the original exception. */ }
    }
}
