using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>Calls `page_dewarp.py` (Matt Zucker's "cubic sheet" model,
/// https://mzucker.github.io/2016/08/15/page-dewarping.html) as a subprocess for book-curve
/// correction, rather than maintaining a hand-ported reimplementation in C#. A C# port
/// (PageDewarpPipeline.cs) was built first and worked on the underlying geometry, but a
/// side-by-side comparison against the original script on a real photo surfaced a real quality
/// gap in binarized output (the port's Sauvola threshold vs. the script's plain
/// cv2.adaptiveThreshold) and a since-fixed performance bug — the trust cost of chasing parity
/// bugs against a script that already works was judged not worth paying. The script itself is
/// vendored at MicroCapture.Processing/vendor/page_dewarp/page_dewarp.py with ONE deliberate
/// change from upstream (REMAP_DECIMATE, see that file's header comment) that fixes real blur
/// confirmed in the upstream script's own output on real photos — everything else about it is
/// unmodified. This mirrors OcrProcessor.cs's own external-CLI pattern: synchronous
/// Process.Start + WaitForExit(timeout) + Kill() on timeout, never throws past its own
/// boundary.</summary>
internal static class PythonDewarpRunner
{
    // page_dewarp.py exits 0 even when it silently skips an image (too few text spans found —
    // see its own `if len(spans) < 1: print('skipping...'); continue`), so a clean exit code is
    // NOT sufficient evidence of success — the actual signal is whether the expected output file
    // landed on disk. This mirrors TryApplyDewarp's own "no confident curvature, return null"
    // contract, just sourced from a different place (file existence instead of a null model).
    private const int TimeoutMs = 60_000;

    /// <summary>Runs the real page_dewarp.py against <paramref name="page"/> and returns the
    /// flattened result, or null (with a warning already added to <paramref name="result"/>) on
    /// any failure, timeout, or "no confident curvature" skip. Never throws.</summary>
    public static Mat? RunPythonDewarp(Mat page, ProcessingResult result)
    {
        string? workDir = null;
        string? inputPath = null;
        try
        {
            var pythonExe = ResolvePythonExecutable();
            var scriptPath = ResolveScriptPath();
            if (pythonExe is null || scriptPath is null)
            {
                result.Warnings.Add("Book curve correction unavailable — bundled Python runtime not found.");
                return null;
            }

            // page_dewarp.py always writes its output into its own current working directory
            // (there is no output-path argument) — so a dedicated, empty temp directory used as
            // the process's cwd IS the output-directory mechanism.
            workDir = Path.Combine(Path.GetTempPath(), "microcapture_pagedewarp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            var baseName = Guid.NewGuid().ToString("N");
            inputPath = Path.Combine(workDir, baseName + ".png");
            if (!Cv2.ImWrite(inputPath, page))
            {
                result.Warnings.Add("Book curve correction failed: could not write temp input image.");
                return null;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\" --color \"{inputPath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(startInfo)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();

            var exited = proc.WaitForExit(TimeoutMs);
            if (!exited)
            {
                try { proc.Kill(); } catch { /* best effort */ }
                WriteDebugLog(startInfo, -1, stdout, stderr, timedOut: true);
                result.Warnings.Add("Book curve correction timed out.");
                return null;
            }

            WriteDebugLog(startInfo, proc.ExitCode, stdout, stderr, timedOut: false);

            var outputPath = Path.Combine(workDir, baseName + "_color.png");
            if (!File.Exists(outputPath))
            {
                // Either a real failure (non-zero exit, script exception) or the script's own
                // "too few text spans, skipping" case (which still exits 0) — both mean no
                // confident curvature was found, same semantic TryApplyDewarp already expects.
                result.Warnings.Add("No confident book curvature detected — skipping dewarp.");
                return null;
            }

            using var flattened = Cv2.ImRead(outputPath, ImreadModes.Color);
            if (flattened.Empty())
            {
                result.Warnings.Add("Book curve correction failed: output image could not be decoded.");
                return null;
            }
            return flattened.Clone();
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Book curve correction failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (inputPath is not null && File.Exists(inputPath)) File.Delete(inputPath); } catch { }
            try { if (workDir is not null && Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static void WriteDebugLog(ProcessStartInfo startInfo, int exitCode, string stdout, string stderr, bool timedOut)
    {
        try
        {
            var dbg = new StringBuilder();
            dbg.AppendLine($"--- PAGE_DEWARP DEBUG {DateTime.UtcNow:O} ---");
            dbg.AppendLine($"Command: {startInfo.FileName} {startInfo.Arguments}");
            dbg.AppendLine($"WorkingDirectory: {startInfo.WorkingDirectory}");
            dbg.AppendLine(timedOut ? "TIMED OUT" : $"ExitCode: {exitCode}");
            dbg.AppendLine("--- STDOUT ---");
            dbg.AppendLine(stdout);
            dbg.AppendLine("--- STDERR ---");
            dbg.AppendLine(stderr);
            dbg.AppendLine("--- END DEBUG ---\n");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "microcapture_pagedewarp_debug.log"), dbg.ToString());
        }
        catch { /* non-fatal logging failure */ }
    }

    /// <summary>Locates the Python interpreter to invoke. Production (Windows) resolves the
    /// bundled embeddable runtime at "&lt;app-dir&gt;/python/python.exe" relative to the app's
    /// own executable directory — nothing pre-installed on the operator's machine is required.
    /// Local development can override via the MICROCAPTURE_PYTHON_EXE environment variable
    /// (e.g. pointed at this repo's own .venv's python3 on macOS, where no embeddable-Windows
    /// runtime exists to bundle) — never consulted in a properly-packaged install where the
    /// bundled runtime is present.</summary>
    private static string? ResolvePythonExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("MICROCAPTURE_PYTHON_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var bundled = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        if (File.Exists(bundled))
            return bundled;

        // Non-Windows dev fallback: a bundled Windows embeddable runtime never exists here, so
        // fall back to whatever python3 is on PATH (e.g. this repo's own .venv, if activated, or
        // a system install) rather than failing outright — matches OcrProcessor's own
        // PATH-lookup fallback shape for the tesseract CLI.
        if (!OperatingSystem.IsWindows())
        {
            var pathPython = ResolveFromPath("python3") ?? ResolveFromPath("python");
            if (pathPython is not null) return pathPython;
        }

        return null;
    }

    /// <summary>Locates the bundled page_dewarp.py script, or (dev override) an arbitrary
    /// script path via MICROCAPTURE_PAGE_DEWARP_SCRIPT.</summary>
    private static string? ResolveScriptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("MICROCAPTURE_PAGE_DEWARP_SCRIPT");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var bundled = Path.Combine(AppContext.BaseDirectory, "python", "page_dewarp", "page_dewarp.py");
        if (File.Exists(bundled))
            return bundled;

        return null;
    }

    private static string? ResolveFromPath(string command)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = command,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(2000);
            var outp = p.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(outp) ? null : outp;
        }
        catch
        {
            return null;
        }
    }
}
