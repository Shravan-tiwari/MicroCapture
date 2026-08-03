using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Tesseract;

namespace MicroCapture.Processing;

public class OcrProcessor
{
    private readonly string _tessDataPath;

    public bool CliAvailable { get; }

    public OcrProcessor(string? tessDataPath = null)
    {
        _tessDataPath = tessDataPath ?? ResolveTessDataPath();
        // Check whether the tesseract CLI is available at construction time so callers
        // can decide whether to attempt OCR or skip it to avoid loading native libs.
        CliAvailable = IsTesseractCliAvailable(out _);
    }

    private static string ResolveTessDataPath()
    {
        string[] candidates = new[]
        {
            Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? string.Empty,
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(AppContext.BaseDirectory, "..", "tessdata"),
            "/opt/homebrew/share/tessdata",
            "/usr/local/share/tessdata"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (Directory.Exists(candidate))
                return candidate;

            var nested = Path.Combine(candidate, "tessdata");
            if (Directory.Exists(nested))
                return nested;
        }

        return Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    /// <summary>
    /// Performs OCR on the specified image and saves the result to a text file.
    /// This implementation prefers the tesseract CLI (safer, isolates native code). If the CLI is not available,
    /// it falls back to the Tesseract .NET wrapper.
    /// </summary>
    /// <param name="imagePath">Path to the processed image</param>
    /// <returns>Path to the generated text file</returns>
    public string ProcessImage(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath}");
        }

        string txtFileName = Path.ChangeExtension(imagePath, ".txt");

        // If the tesseract CLI is available, use it. This avoids loading native libs in-process which
        // have been observed to crash some runtimes.
        // Decide whether the managed wrapper may be used if CLI fails
        var allowManaged = string.Equals(Environment.GetEnvironmentVariable("MICROCAPTURE_ALLOW_MANAGED_TESS"), "1");

        try
        {
            if (IsTesseractCliAvailable(out var tesseractPath))
            {
                // Preflight: ensure the 'eng' language data is available via --list-langs
                try
                {
                    var listInfo = new ProcessStartInfo
                    {
                        FileName = tesseractPath,
                        Arguments = "--list-langs",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };

                    if (!string.IsNullOrWhiteSpace(_tessDataPath) && Directory.Exists(_tessDataPath))
                    {
                        var parent = Path.GetDirectoryName(_tessDataPath) ?? _tessDataPath;
                        listInfo.Environment["TESSDATA_PREFIX"] = parent;
                    }

                    using var listProc = Process.Start(listInfo)!;
                    var listOut = listProc.StandardOutput.ReadToEnd();
                    var listErr = listProc.StandardError.ReadToEnd();
                    if (!listProc.WaitForExit(5_000))
                    {
                        try { listProc.Kill(); } catch { }
                    }

                    if (listProc.ExitCode != 0)
                    {
                        Console.Error.WriteLine($"Tesseract --list-langs failed: exit {listProc.ExitCode}, out={listOut}, err={listErr}");
                        if (!allowManaged)
                            throw new InvalidOperationException($"Tesseract --list-langs failed: {listErr}");
                        // else we'll fall back to managed wrapper later
                    }
                    else
                    {
                        var combined = string.Join("\n", new[] { listOut, listErr });
                        if (!combined.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Any(s => s.Trim() == "eng"))
                        {
                            Console.Error.WriteLine($"Tesseract --list-langs output did not include 'eng': out={listOut}, err={listErr}");
                            // Do not fail here: the CLI may still work. Only warn and continue.
                        }
                    }
                }
                catch (Exception preEx)
                {
                    Console.Error.WriteLine($"Tesseract preflight check failed: {preEx}");
                    if (!allowManaged)
                        throw;
                }

                // Run tesseract writing output to a temp base name to avoid write-permission issues
                var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                var startInfo = new ProcessStartInfo
                {
                    FileName = tesseractPath,
                    Arguments = $"\"{imagePath}\" \"{tempBase}\" -l eng",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                if (!string.IsNullOrWhiteSpace(_tessDataPath) && Directory.Exists(_tessDataPath))
                {
                    var parent = Path.GetDirectoryName(_tessDataPath) ?? _tessDataPath;
                    startInfo.Environment["TESSDATA_PREFIX"] = parent;
                }

                using var proc = Process.Start(startInfo)!;
                // Capture both stdout and stderr synchronously to avoid missing messages when exit != 0.
                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit(30_000))
                {
                    try { proc.Kill(); } catch { }
                    throw new InvalidOperationException("Tesseract CLI timed out");
                }

                // Write a debug trace so you can inspect what the CLI actually returned when run by the app.
                try
                {
                    var dbg = new StringBuilder();
                    dbg.AppendLine($"--- TESSERACT DEBUG {DateTime.UtcNow:O} ---");
                    dbg.AppendLine($"Command: {startInfo.FileName} {startInfo.Arguments}");
                    if (startInfo.Environment != null && startInfo.Environment.ContainsKey("TESSDATA_PREFIX"))
                        dbg.AppendLine($"TESSDATA_PREFIX={startInfo.Environment["TESSDATA_PREFIX"]}");
                    dbg.AppendLine($"ExitCode: {proc.ExitCode}");
                    dbg.AppendLine("--- STDOUT ---");
                    dbg.AppendLine(stdout);
                    dbg.AppendLine("--- STDERR ---");
                    dbg.AppendLine(stderr);
                    dbg.AppendLine($"TempTxt: {tempBase}.txt");
                    dbg.AppendLine("--- END DEBUG ---\n");
                    File.AppendAllText("/tmp/microcapture_tess_debug.log", dbg.ToString());
                }
                catch { /* non-fatal logging failure */ }

                if (proc.ExitCode != 0)
                {
                    var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    Console.Error.WriteLine($"Tesseract CLI failed (exit {proc.ExitCode}): {err}");
                    if (!allowManaged)
                        throw new InvalidOperationException($"Tesseract CLI failed (exit {proc.ExitCode}): {err}");
                }

                var tempTxt = tempBase + ".txt";
                if (File.Exists(tempTxt))
                {
                    // Move/copy to the expected txtFileName
                    File.Copy(tempTxt, txtFileName, true);
                    try { File.Delete(tempTxt); } catch { }
                    return txtFileName;
                }

                // Fallback: if the default txt isn't present, try a base-name path near the image
                var alt = Path.ChangeExtension(imagePath, ".txt");
                if (File.Exists(alt)) return alt;

                if (!allowManaged)
                    throw new InvalidOperationException("Tesseract CLI succeeded but output file not found and managed wrapper disabled");
                // otherwise fall through to managed wrapper
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tesseract CLI path failed: {ex}");
            // fall through to the managed wrapper attempt if allowed
            if (!allowManaged)
                throw;
        }

        // Fallback: use Tesseract .NET wrapper (may invoke native libraries in-process)
        // To avoid uncatchable native crashes, only allow the managed wrapper when the
        // environment variable MICROCAPTURE_ALLOW_MANAGED_TESS=="1" is set explicitly.
        if (!allowManaged)
        {
            throw new InvalidOperationException("Tesseract CLI not available and managed wrapper is disabled by default. Set MICROCAPTURE_ALLOW_MANAGED_TESS=1 to allow managed wrapper (not recommended).");
        }

        try
        {
            var tessData = _tessDataPath ?? ResolveTessDataPath();
            if (!Directory.Exists(tessData))
                throw new DirectoryNotFoundException($"Tesseract data directory not found. Searched: {tessData}");

            using (var engine = new TesseractEngine(tessData, "eng", EngineMode.Default))
            {
                using (var img = Pix.LoadFromFile(imagePath))
                {
                    using (var page = engine.Process(img))
                    {
                        var text = page.GetText() ?? string.Empty;
                        File.WriteAllText(txtFileName, text);
                        return txtFileName;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tesseract managed wrapper failed: {ex}");
            throw;
        }
    }

    private static bool IsTesseractCliAvailable(out string path)
    {
        path = "tesseract"; // rely on PATH by default
        try
        {
            var p = Process.Start(new ProcessStartInfo { FileName = "which", Arguments = "tesseract", RedirectStandardOutput = true, UseShellExecute = false });
            p.WaitForExit(2000);
            var outp = p.StandardOutput.ReadToEnd().Trim();
            if (!string.IsNullOrWhiteSpace(outp))
            {
                path = outp;
                return true;
            }
        }
        catch { }

        // As a last resort, check common brew locations
        var candidates = new[] { "/opt/homebrew/bin/tesseract", "/usr/local/bin/tesseract" };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) { path = c; return true; }
        }

        return false;
    }
}
