// Renders the real MainWindow in both palettes to PNG, so day/night mode can be reviewed as
// pictures rather than as a description of pictures. Headless + Skia: no display needed, and it
// builds the actual window with the actual styles, so what comes out is what ships.
//
//   dotnet run --project tools/ThemePreview -- <output folder>
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MicroCapture.UI.Theming;
using MicroCapture.UI.ViewModels;
using MicroCapture.UI.Views;

var outputDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
Directory.CreateDirectory(outputDir);

AppBuilder.Configure<MicroCapture.UI.App>()
    .UseSkia()
    // Headless normally skips drawing entirely; this is the whole point of the tool, so turn
    // real rendering back on.
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var camera = new MicroCapture.Camera.MockCameraService();

foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
{
    AppTheme.Apply(mode);

    var window = new MainWindow
    {
        DataContext = new MainWindowViewModel(camera),
        Width = 1400,
        Height = 800,
    };
    window.Show();

    // Two passes: the first lays out and resolves resources, the second draws what that produced.
    Dispatcher.UIThread.RunJobs();
    var frame = window.CaptureRenderedFrame();
    Dispatcher.UIThread.RunJobs();
    frame = window.CaptureRenderedFrame();

    var path = Path.Combine(outputDir, $"microcapture-{mode.ToString().ToLowerInvariant()}.png");
    frame?.Save(path);
    Console.WriteLine(frame == null ? $"{mode}: nothing rendered" : $"{mode}: {path}");

    window.Close();
}
