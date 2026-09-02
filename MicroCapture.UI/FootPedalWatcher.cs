using System;
using System.Linq;
using System.Threading;
using HidSharp;

namespace MicroCapture.UI;

/// <summary>Watches a USB foot pedal that enumerates as a HID game controller (a joystick
/// button), rather than as a keyboard, and raises <see cref="Pressed"/> on each press.
///
/// <para>The operator's pedal reports VID 0x07C0 / PID 0x1101, usage page Generic Desktop /
/// usage Joystick — it sends no keystroke at all, so the window's key handler never sees it.
/// A pedal that instead acts as a keyboard needs nothing special: it just presses Space.</para>
///
/// <para>Runs a single background thread that opens the device and blocks on its input
/// reports. A press is the 0-&gt;1 edge of ANY button byte in the report — one physical pedal
/// has one button, and treating "any button down" as the trigger means we do not have to parse
/// the report descriptor to find which bit it is. The first button index seen is remembered so
/// that, if a real multi-button controller is ever attached to the same machine, its other
/// buttons are ignored. Reconnects on its own if the pedal is unplugged and replugged.</para></summary>
public sealed class FootPedalWatcher : IDisposable
{
    // The operator's known pedal. Anything else that presents as a Generic-Desktop joystick is
    // still accepted as a fallback (see MatchesPedal) so a replacement of the same kind works
    // without a code change, but this pair is tried first.
    private const int KnownVendorId = 0x07C0;
    private const int KnownProductId = 0x1101;

    /// <summary>Raised on the pedal's press edge. Marshalled by the subscriber — this fires on
    /// the watcher's own background thread.</summary>
    public event EventHandler? Pressed;

    private readonly Thread _thread;
    private volatile bool _running = true;

    // Debounce: a cheap pedal can chatter a few reports on a single press. Ignore a second
    // edge within this window.
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    private DateTime _lastPressUtc = DateTime.MinValue;

    public FootPedalWatcher()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "FootPedalWatcher",
        };
        _thread.Start();
    }

    private static bool MatchesPedal(HidDevice d)
    {
        if (d.VendorID == KnownVendorId && d.ProductID == KnownProductId) return true;
        try
        {
            // Fallback: any Generic Desktop (0x01) Joystick (0x04) or Gamepad (0x05) collection.
            var report = d.GetReportDescriptor();
            return report.DeviceItems.Any(item => item.Usages.GetAllValues().Any(u =>
                u == 0x0001_0004 || u == 0x0001_0005));
        }
        catch
        {
            return false;
        }
    }

    private void Loop()
    {
        while (_running)
        {
            HidDevice? device = null;
            try
            {
                device = DeviceList.Local.GetHidDevices()
                    .FirstOrDefault(MatchesPedal);
            }
            catch
            {
                // Enumeration can throw transiently while a device is being (un)plugged.
            }

            if (device == null)
            {
                // No pedal attached yet — check again shortly.
                if (WaitOrStop(TimeSpan.FromSeconds(2))) return;
                continue;
            }

            try
            {
                var openOptions = new OpenConfiguration();
                openOptions.SetOption(OpenOption.Exclusive, false);
                using var stream = device.Open(openOptions);
                stream.ReadTimeout = 500; // so the loop can notice _running going false

                var buffer = new byte[Math.Max(device.GetMaxInputReportLength(), 8)];
                byte[]? previousButtons = null;
                int? learnedButtonIndex = null;

                while (_running)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    if (read <= 0) continue;

                    // The button state lives somewhere in the report; without parsing the
                    // descriptor we treat the whole payload (past the report-id byte) as the
                    // "button region" and look for any byte gaining a set bit vs. the previous
                    // report. That is the press edge for a one-button pedal, and — via the
                    // remembered index — stays specific to that one byte afterwards.
                    var start = read > 1 && buffer[0] != 0 ? 1 : 0; // skip a report id if present
                    var current = new byte[read - start];
                    Array.Copy(buffer, start, current, 0, current.Length);

                    if (previousButtons != null && previousButtons.Length == current.Length)
                    {
                        for (var i = 0; i < current.Length; i++)
                        {
                            var gainedBits = (byte)(current[i] & ~previousButtons[i]);
                            if (gainedBits == 0) continue;

                            // Lock onto the first byte that ever shows a press, so other
                            // controls on a real gamepad can't trigger capture.
                            learnedButtonIndex ??= i;
                            if (i != learnedButtonIndex.Value) continue;

                            var now = DateTime.UtcNow;
                            if (now - _lastPressUtc < Debounce) break;
                            _lastPressUtc = now;
                            Pressed?.Invoke(this, EventArgs.Empty);
                            break;
                        }
                    }

                    previousButtons = current;
                }
            }
            catch
            {
                // Device vanished or refused to open — fall back to the outer rescan loop.
                if (WaitOrStop(TimeSpan.FromSeconds(2))) return;
            }
        }
    }

    /// <summary>Sleeps up to <paramref name="span"/>, returning true if the watcher was asked to
    /// stop meanwhile.</summary>
    private bool WaitOrStop(TimeSpan span)
    {
        var deadline = DateTime.UtcNow + span;
        while (DateTime.UtcNow < deadline)
        {
            if (!_running) return true;
            Thread.Sleep(50);
        }
        return !_running;
    }

    public void Dispose()
    {
        _running = false;
        try { _thread.Join(TimeSpan.FromSeconds(1)); } catch { }
    }
}
