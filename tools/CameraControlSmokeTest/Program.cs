using System;
using System.Linq;
using System.Threading.Tasks;
using MicroCapture.Camera;
using MicroCapture.UI.ViewModels;

namespace CameraControlSmokeTest
{
    class Program
    {
        static async Task<int> Main()
        {
            Console.WriteLine("CameraControl smoke test starting...");

            var cam = new MockCameraService();

            var connected = await cam.GetConnectedCamerasAsync();
            var first = connected.FirstOrDefault();
            if (first == null)
            {
                Console.Error.WriteLine("No cameras returned by MockCameraService");
                return 2;
            }

            Console.WriteLine($"Connecting to mock camera '{first.Model}'...");
            await cam.ConnectAsync(first.Id);

            // Manually create CameraControlItem instances from the camera settings (LoadCameraSettingsAsync is private)
            var settings = await cam.GetCameraSettingsAsync();
            var controls = settings.Select(s => new MicroCapture.UI.ViewModels.CameraControlItem(s, cam, msg => Console.WriteLine(msg))).ToList();

            Console.WriteLine($"Loaded {controls.Count} camera controls.");

            foreach (var control in controls)
            {
                Console.WriteLine($"Control: {control.DisplayName} (Key={control.Key}) - Current: {control.SelectedOption?.DisplayName}");
                foreach (var opt in control.Options)
                {
                    Console.WriteLine($"  Setting to: {opt.DisplayName} ({opt.Value})");
                    control.SelectedOption = opt;
                    // Allow async Apply to run
                    await Task.Delay(250);
                }
            }

            Console.WriteLine("Smoke test completed successfully.");
            return 0;
        }
    }
}
