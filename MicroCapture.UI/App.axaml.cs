using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MicroCapture.UI.ViewModels;
using MicroCapture.UI.Views;

namespace MicroCapture.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
}