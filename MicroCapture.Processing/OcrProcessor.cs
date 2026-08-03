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
                var startInfo = new ProcessStartInfo
                {
                    FileName = tesseractPath,
                    Arguments = $"\"{imagePath}\" \"{Path.ChangeExtension(imagePath, null)}\" -l eng",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                // Ensure TESSDATA_PREFIX is set if we discovered a path earlier
                if (!string.IsNullOrWhiteSpace(_tessDataPath) && Directory.Exists(_tessDataPath))
                {
                    var parent = Path.GetDirectoryName(_tessDataPath) ?? _tessDataPath;
                    startInfo.Environment["TESSDATA_PREFIX"] = parent;
                }

                using var proc = Process.Start(startInfo)!;
                var stderr = new StringBuilder();
                proc.OutputDataReceived += (_, e) => { /* ignore stdout */ };
                proc.BeginOutputReadLine();
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(30_000))
                {
                    try { proc.Kill(); } catch { }
                    throw new InvalidOperationException("Tesseract CLI timed out");
                }

                if (proc.ExitCode != 0)
                {
                    var err = stderr.ToString();
                    Console.Error.WriteLine($"Tesseract CLI failed (exit {proc.ExitCode}): {err}");
                    // If managed wrapper is not explicitly allowed, do not fallback — surface the CLI failure so worker can skip OCR.
                    if (!allowManaged)
                        throw new InvalidOperationException($"Tesseract CLI failed (exit {proc.ExitCode}): {err}");
                    // otherwise fall through to managed wrapper attempt
                }

                // tesseract writes to <base>.txt
                if (File.Exists(txtFileName))
                    return txtFileName;

                // Fallback: if the default txt isn't present, try a base-name path
                var alt = Path.ChangeExtension(imagePath, ".txt");
                if (File.Exists(alt)) return alt;

                if (!allowManaged)
                    throw new InvalidOperationException("Tesseract CLI succeeded but output file not found and managed wrapper disabled");
                // otherwise fall through
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
