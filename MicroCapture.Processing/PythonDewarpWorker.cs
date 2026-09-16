using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using OpenCvSharp;

namespace MicroCapture.Processing;

/// <summary>A long-lived page_dewarp.py subprocess (started with --worker — see that mode's own
/// docstring in the vendored script), reused across every page instead of spawning a fresh
/// Python process per page. Confirmed on a real Windows machine (see the debug log's
/// SubprocessWallTime vs. the script's own "optimization took" line) that Python interpreter +
/// numpy/scipy/cv2/PIL import startup costs 6-11 seconds PER PAGE when spawned fresh every
/// time — dwarfing the optimizer itself, which is down to 1-3s after the L-BFGS-B + analytic
/// gradient fix. Paying that interpreter/import cost exactly once per app session (or once after
/// a crash) instead of once per page is what actually fixes the operator-visible slowness.
///
/// Protocol: one absolute image path per line on stdin, one response line per request on
/// stdout — "OK &lt;outfile&gt;", "SKIP" (too few text spans, not an error), or "ERROR &lt;message&gt;".
/// stdout is reserved exclusively for these responses; the worker redirects its own progress
/// prints to stderr for the duration of each request (see run_worker_loop in the vendored
/// script) so nothing else can land on the response channel.
///
/// Never the single point of failure for book curve correction: if the worker process is
/// unavailable, dies mid-batch, or a single request misbehaves (bad response line, timeout),
/// this class kills it and falls back to PythonDewarpRunner's original spawn-per-call path for
/// that page (and every page until the worker is next successfully restarted) rather than
/// letting a broken persistent process take the whole feature down.</summary>
public static class PythonDewarpWorker
{
    private const int StartupTimeoutMs = 30_000; // first WORKER_READY line — pays the interpreter+import cost once
    private const int RequestTimeoutMs = 60_000; // per-page dewarp, same ceiling PythonDewarpRunner already used

    private static readonly object Lock = new();
    private static Process? _proc;
    private static bool _unavailable; // true once we've given up trying to (re)start the worker this session

    /// <summary>Runs page_dewarp.py against <paramref name="page"/> via the persistent worker
    /// process, starting it on first use. Returns null (with a warning already added to
    /// <paramref name="result"/>) on any failure, timeout, or "no confident curvature" skip —
    /// the caller (TryApplyDewarp) treats that exactly the same as PythonDewarpRunner's own
    /// null-return contract. Never throws.</summary>
    public static Mat? RunPythonDewarp(Mat page, ProcessingResult result)
    {
        if (_unavailable) return PythonDewarpRunner.RunPythonDewarp(page, result);

        string? inputPath = null;
        try
        {
            lock (Lock)
            {
                if (_unavailable) { }
                else if (_proc is null || _proc.HasExited)
                {
                    if (!TryStartWorker())
                    {
                        _unavailable = true;
                    }
                }
            }

            if (_unavailable) return PythonDewarpRunner.RunPythonDewarp(page, result);

            // A fresh, stable-for-the-request temp file — the worker's own cwd never changes
            // across requests (unlike PythonDewarpRunner's per-call fresh cwd), so both the
            // input and the "<basename>_color.png" output this request produces need unique
            // names to avoid colliding with a previous or concurrent request's files.
            var workDir = WorkerCwd!;
            var baseName = Guid.NewGuid().ToString("N");
            inputPath = Path.Combine(workDir, baseName + ".png");
            if (!Cv2.ImWrite(inputPath, page))
            {
                result.Warnings.Add("Book curve correction failed: could not write temp input image.");
                return null;
            }

            string? response;
            var sw = Stopwatch.StartNew();
            lock (Lock)
            {
                if (_proc is null || _proc.HasExited)
                {
                    // Died since the check above (e.g. another thread's request crashed it) —
                    // one restart attempt before giving up on the worker for this session.
                    if (!TryStartWorker())
                    {
                        _unavailable = true;
                        return PythonDewarpRunner.RunPythonDewarp(page, result);
                    }
                }

                response = SendRequestLocked(inputPath, RequestTimeoutMs);
            }
            sw.Stop();
            WriteStageLog($"PythonDewarpWorker request: {sw.Elapsed.TotalSeconds:F2}s response={response ?? "(null)"}");

            if (response is null)
            {
                // No response within the timeout, or the pipe broke — the worker is presumed
                // dead; kill it so the next call starts fresh rather than reusing a hung process.
                KillWorkerLocked();
                result.Warnings.Add("Book curve correction timed out.");
                return null;
            }

            if (response.StartsWith("SKIP", StringComparison.Ordinal))
            {
                result.Warnings.Add("No confident book curvature detected — skipping dewarp.");
                return null;
            }

            if (response.StartsWith("ERROR", StringComparison.Ordinal))
            {
                result.Warnings.Add($"Book curve correction failed: {response}");
                return null;
            }

            if (response.StartsWith("OK ", StringComparison.Ordinal))
            {
                var outFileName = response.Substring(3).Trim();
                var outputPath = Path.Combine(workDir, outFileName);
                if (!File.Exists(outputPath))
                {
                    result.Warnings.Add("Book curve correction failed: worker reported success but output file is missing.");
                    return null;
                }
                using var flattened = Cv2.ImRead(outputPath, ImreadModes.Color);
                try { File.Delete(outputPath); } catch { /* best effort */ }
                if (flattened.Empty())
                {
                    result.Warnings.Add("Book curve correction failed: output image could not be decoded.");
                    return null;
                }
                return flattened.Clone();
            }

            // Unrecognized response line — protocol desync. Treat the worker as broken rather
            // than guessing at what it meant.
            KillWorkerLocked();
            result.Warnings.Add($"Book curve correction failed: unexpected worker response '{response}'.");
            return null;
        }
        catch (Exception ex)
        {
            lock (Lock) { KillWorkerLocked(); }
            result.Warnings.Add($"Book curve correction failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (inputPath is not null && File.Exists(inputPath)) File.Delete(inputPath); } catch { }
        }
    }

    private static string? WorkerCwd;

    /// <summary>Must be called with Lock held. Starts the worker process and blocks (up to
    /// StartupTimeoutMs) for its WORKER_READY line — this is where the one-time interpreter and
    /// numpy/scipy/cv2/PIL import cost actually gets paid. Returns false (never throws) if the
    /// interpreter/script can't be resolved or the process fails to signal ready in time.</summary>
    private static bool TryStartWorker()
    {
        try
        {
            var pythonExe = PythonDewarpRunner.ResolvePythonExecutableForWorker();
            var scriptPath = PythonDewarpRunner.ResolveScriptPathForWorker();
            if (pythonExe is null || scriptPath is null) return false;

            var workDir = Path.Combine(Path.GetTempPath(), "microcapture_pagedewarp_worker_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\" --color --worker",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var proc = Process.Start(startInfo);
            if (proc is null) return false;

            var readyTask = proc.StandardOutput.ReadLineAsync();
            if (!readyTask.Wait(StartupTimeoutMs) || readyTask.Result != "WORKER_READY")
            {
                try { proc.Kill(); } catch { /* best effort */ }
                return false;
            }

            _proc = proc;
            WorkerCwd = workDir;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Must be called with Lock held. Writes one request line and blocks for one
    /// response line, or returns null on timeout/broken pipe. Does not itself kill the process
    /// on failure — the caller decides that, since a timeout here doesn't always mean the
    /// process is dead (a pathological image could just be slow).</summary>
    private static string? SendRequestLocked(string inputPath, int timeoutMs)
    {
        var proc = _proc;
        if (proc is null) return null;
        try
        {
            proc.StandardInput.WriteLine(inputPath);
            proc.StandardInput.Flush();
        }
        catch
        {
            return null; // pipe broken — worker is dead
        }

        var readTask = proc.StandardOutput.ReadLineAsync();
        if (!readTask.Wait(timeoutMs)) return null;
        return readTask.Result;
    }

    /// <summary>Must be called with Lock held.</summary>
    private static void KillWorkerLocked()
    {
        try { _proc?.StandardInput.WriteLine("QUIT"); _proc?.StandardInput.Flush(); } catch { /* best effort */ }
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { /* best effort */ }
        try { _proc?.Dispose(); } catch { /* best effort */ }
        _proc = null;
        try { if (WorkerCwd is not null && Directory.Exists(WorkerCwd)) Directory.Delete(WorkerCwd, recursive: true); } catch { /* best effort */ }
        WorkerCwd = null;
    }

    /// <summary>Called once at app shutdown (see App.axaml.cs / MainWindowViewModel's own
    /// disposal path) so a live Python process is never left behind after the app exits.</summary>
    public static void Shutdown()
    {
        lock (Lock)
        {
            if (_proc is not null) KillWorkerLocked();
        }
    }

    private static void WriteStageLog(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "microcapture_pagedewarp_debug.log"), $"[WORKER TIMING {DateTime.UtcNow:O}] {message}\n");
        }
        catch { /* non-fatal logging failure */ }
    }
}
