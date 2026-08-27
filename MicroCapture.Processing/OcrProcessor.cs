using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Tesseract;

namespace MicroCapture.Processing;

/// <summary>One recognized word and its pixel-space bounding box in the source image
/// (Tesseract's TSV <c>left/top/width/height</c> columns), used to draw the invisible PDF
/// text layer directly on top of each word instead of as a single tiny, unclickable blob —
/// see <see cref="BatchExportService"/>'s DrawSearchText.</summary>
public readonly record struct OcrWordBox(string Text, int Left, int Top, int Width, int Height);

public class OcrProcessor
{
    private readonly string _tessDataPath;

    // The CLI's availability/path and the --list-langs preflight result don't change between
    // calls within a single run of the app, but ProcessImage used to re-check both on every
    // single invocation (a `which`/`where` spawn plus a --list-langs spawn, ~7s combined) —
    // cache both process-wide instead of paying that cost per page.
    private static readonly object PreflightSync = new();
    private static bool? _cliAvailableCache;
    private static string? _cliPathCache;
    private static bool? _listLangsOkCache;

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

        string txtFileName = ProcessedFilePaths.OcrSidecarPath(imagePath, ".txt");

        // If the tesseract CLI is available, use it. This avoids loading native libs in-process which
        // have been observed to crash some runtimes.
        // Decide whether the managed wrapper may be used if CLI fails
        var allowManaged = string.Equals(Environment.GetEnvironmentVariable("MICROCAPTURE_ALLOW_MANAGED_TESS"), "1");

        try
        {
            if (IsTesseractCliAvailable(out var tesseractPath))
            {
                // Preflight: ensure the 'eng' language data is available via --list-langs.
                // The CLI/tessdata don't change mid-run, so this only actually spawns the
                // subprocess once per app session — later calls reuse the cached verdict.
                lock (PreflightSync)
                {
                    if (_listLangsOkCache == null)
                    {
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
                                // Prefer pointing TESSDATA_PREFIX at the actual tessdata directory itself.
                                string prefix;
                                if (_tessDataPath.EndsWith("tessdata", StringComparison.OrdinalIgnoreCase))
                                {
                                    prefix = _tessDataPath;
                                }
                                else if (Directory.Exists(Path.Combine(_tessDataPath, "tessdata")))
                                {
                                    prefix = Path.Combine(_tessDataPath, "tessdata");
                                }
                                else if (Directory.Exists(Path.Combine(Path.GetDirectoryName(_tessDataPath) ?? string.Empty, "tessdata")))
                                {
                                    prefix = Path.Combine(Path.GetDirectoryName(_tessDataPath) ?? string.Empty, "tessdata");
                                }
                                else
                                {
                                    prefix = _tessDataPath;
                                }

                                listInfo.Environment["TESSDATA_PREFIX"] = prefix;
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
                                _listLangsOkCache = false;
                                if (!allowManaged)
                                    throw new InvalidOperationException($"Tesseract --list-langs failed: {listErr}");
                                // else we'll fall back to managed wrapper later
                            }
                            else
                            {
                                _listLangsOkCache = true;
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
                            _listLangsOkCache = false;
                            if (!allowManaged)
                                throw;
                        }
                    }
                    else if (_listLangsOkCache == false && !allowManaged)
                    {
                        throw new InvalidOperationException("Tesseract --list-langs failed on a previous check (cached).");
                    }
                }

                // Run tesseract writing output to a temp base name to avoid write-permission issues.
                // Requesting txt+tsv output via "-c tessedit_create_X=1" variables, NOT the named
                // configfiles ("... txt tsv"): those configfiles live under
                // $TESSDATA_PREFIX/configs/, and this app's bundled tessdata folder ships only
                // eng.traineddata with no configs/ subdirectory — passing them as positional
                // configfile names made tesseract log "read_params_file: Can't open txt/tsv" and
                // silently fall back to its plain-txt-only default, so no tsv was ever produced
                // (confirmed via the debug log). The -c variable form needs no config file at
                // all. The tsv is what lets DrawSearchText draw each word's invisible PDF text
                // directly on top of its real position/size, instead of one 1pt-font blob
                // crammed in the page's top-left corner (searchable via Ctrl+F, but not
                // selectable/clickable — confirmed the actual reported bug).
                var tempBase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                var startInfo = new ProcessStartInfo
                {
                    FileName = tesseractPath,
                    Arguments = $"\"{imagePath}\" \"{tempBase}\" -l eng -c tessedit_create_txt=1 -c tessedit_create_tsv=1",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                if (!string.IsNullOrWhiteSpace(_tessDataPath) && Directory.Exists(_tessDataPath))
                {
                    // Prefer pointing TESSDATA_PREFIX at the actual tessdata directory itself.
                    string prefix;
                    if (_tessDataPath.EndsWith("tessdata", StringComparison.OrdinalIgnoreCase))
                    {
                        prefix = _tessDataPath;
                    }
                    else if (Directory.Exists(Path.Combine(_tessDataPath, "tessdata")))
                    {
                        prefix = Path.Combine(_tessDataPath, "tessdata");
                    }
                    else if (Directory.Exists(Path.Combine(Path.GetDirectoryName(_tessDataPath) ?? string.Empty, "tessdata")))
                    {
                        prefix = Path.Combine(Path.GetDirectoryName(_tessDataPath) ?? string.Empty, "tessdata");
                    }
                    else
                    {
                        prefix = _tessDataPath;
                    }

                    startInfo.Environment["TESSDATA_PREFIX"] = prefix;
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
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "microcapture_tess_debug.log"), dbg.ToString());
                }
                catch { /* non-fatal logging failure */ }

                if (proc.ExitCode != 0)
                {
                    var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    Console.Error.WriteLine($"Tesseract CLI failed (exit {proc.ExitCode}): {err}");
                    if (!allowManaged)
                        throw new InvalidOperationException($"Tesseract CLI failed (exit {proc.ExitCode}): {err}");
                }

                // Persist the tsv sidecar (per-word boxes) next to the txt one, at the same
                // {imagePath-without-extension}.tsv naming BatchExportService.GetWordBoxesPath
                // expects — best-effort: a missing/unparseable tsv just means DrawSearchText
                // falls back to its old single-blob behavior for this page, not a hard failure.
                var tempTsv = tempBase + ".tsv";
                var tsvFileName = ProcessedFilePaths.OcrSidecarPath(imagePath, ".tsv");
                if (File.Exists(tempTsv))
                {
                    try
                    {
                        File.Copy(tempTsv, tsvFileName, true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Could not persist OCR word-box tsv for '{imagePath}': {ex.Message}");
                    }
                    try { File.Delete(tempTsv); } catch { }
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
                var alt = ProcessedFilePaths.OcrSidecarPath(imagePath, ".txt");
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

    /// <summary>Parses a tsv sidecar written by <see cref="ProcessImage"/> into per-word boxes.
    /// Tesseract's tsv has one row per hierarchy level (page/block/paragraph/line/word); only
    /// level 5 rows (individual words) carry real text, so every other level is skipped. Returns
    /// an empty list (never throws) if the file is missing or malformed — the caller falls back
    /// to the plain-text single-blob behavior in that case.</summary>
    public static List<OcrWordBox> ReadWordBoxes(string tsvPath)
    {
        var boxes = new List<OcrWordBox>();
        if (!File.Exists(tsvPath)) return boxes;

        try
        {
            var lines = File.ReadAllLines(tsvPath);
            for (var i = 1; i < lines.Length; i++) // row 0 is the header
            {
                var cols = lines[i].Split('\t');
                // level, page_num, block_num, par_num, line_num, word_num, left, top, width, height, conf, text
                if (cols.Length < 12) continue;
                if (cols[0] != "5") continue; // only word-level rows have real recognized text
                var text = cols[11];
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!int.TryParse(cols[6], out var left) || !int.TryParse(cols[7], out var top)
                    || !int.TryParse(cols[8], out var width) || !int.TryParse(cols[9], out var height))
                    continue;
                boxes.Add(new OcrWordBox(text, left, top, width, height));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not parse OCR word-box tsv '{tsvPath}': {ex.Message}");
        }

        return boxes;
    }

    private static bool IsTesseractCliAvailable(out string path)
    {
        lock (PreflightSync)
        {
            if (_cliAvailableCache.HasValue)
            {
                path = _cliPathCache ?? "tesseract";
                return _cliAvailableCache.Value;
            }

            var result = ResolveTesseractCli(out var resolvedPath);
            _cliAvailableCache = result;
            _cliPathCache = resolvedPath;
            path = resolvedPath;
            return result;
        }
    }

    private static bool ResolveTesseractCli(out string path)
    {
        path = "tesseract"; // rely on PATH by default
        var isWindows = OperatingSystem.IsWindows();
        var lookupCommand = isWindows ? "where" : "which";
        try
        {
            var p = Process.Start(new ProcessStartInfo { FileName = lookupCommand, Arguments = "tesseract", RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit(2000);
            // `where` can print multiple matches, one per line; the first is PATH's preferred pick.
            var outp = p.StandardOutput.ReadToEnd().Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
            if (!string.IsNullOrWhiteSpace(outp))
            {
                path = outp;
                return true;
            }
        }
        catch { }

        // As a last resort, check common install locations for each platform.
        var candidates = isWindows
            ? new[]
              {
                  Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Tesseract-OCR\tesseract.exe"),
                  Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Tesseract-OCR\tesseract.exe")
              }
            : new[] { "/opt/homebrew/bin/tesseract", "/usr/local/bin/tesseract" };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) { path = c; return true; }
        }

        return false;
    }
}
