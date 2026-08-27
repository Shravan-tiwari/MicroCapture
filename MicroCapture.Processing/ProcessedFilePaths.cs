using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MicroCapture.Processing;

/// <summary>Where a job's processed derivative(s) are written — the same directory as the
/// original capture, not a separate "Processed" subfolder. Originals are now retained on disk
/// until the batch is exported (see BackgroundProcessingWorker and MainWindowViewModel's
/// ExportBatchAsync), specifically so Crop Review's re-crop-from-original capability keeps
/// working; with the original staying put right alongside its derivative(s), there's no reason
/// for the derivative to live in a separate subfolder anymore. Centralized here so every caller
/// that needs to know where a job's outputs live — the worker that writes them, and the readers/
/// cleanup code in BatchExportService/CropReviewViewModel/MainWindowViewModel — computes the
/// exact same path.</summary>
public static class ProcessedFilePaths
{
    /// <summary>Where a job's processed derivative(s) are written.
    ///
    /// <para>A capture taken into a batch folder lands in that folder's <c>temp/</c>, because the
    /// original is working material that is deleted once the batch is exported. Its processed
    /// derivative is the actual deliverable, so it belongs in the sibling <c>output/</c> — which
    /// is what leaves <c>temp/</c> empty after finalizing and <c>output/</c> holding the finished
    /// pages.</para>
    ///
    /// <para>Anything outside that layout keeps the original behaviour of writing the derivative
    /// beside its source, so batches predating batch folders are unaffected.</para></summary>
    public static string OutputDirectoryFor(string originalFilePath)
    {
        var directory = Path.GetDirectoryName(originalFilePath);
        if (string.IsNullOrEmpty(directory)) return ".";

        if (string.Equals(Path.GetFileName(directory), TempFolderName, StringComparison.OrdinalIgnoreCase))
        {
            var batchFolder = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(batchFolder))
            {
                var output = Path.Combine(batchFolder, OutputFolderName);
                if (Directory.Exists(output)) return output;
            }
        }

        return directory;
    }

    // Kept in step with MicroCapture.Core.Models.BatchFolder, which owns the batch layout.
    // Duplicated as literals rather than referenced because Processing does not depend on Core's
    // model assembly for path naming.
    private const string TempFolderName = "temp";
    private const string OutputFolderName = "output";

    // Every legitimate suffix ImageProcessor can append to a job's own base name for a
    // processed derivative — see SinglePageOutputFileName / the split-page / fixed-frame /
    // OpenCV-fallback naming in ImageProcessor.cs. Deliberately anchored (^...$) rather than a
    // bare "starts with" check: a plain `{baseName}*` glob also matches an unrelated file whose
    // name merely happens to start with this job's base name as a string prefix — most
    // concretely, a recapture's own original ("{baseName}_R_{timestamp}.jpg") is a literal
    // prefix-match of the page it recaptures ("{baseName}.jpg"), which must NEVER be swept up
    // by cleanup/lookup code that's only supposed to touch THIS job's processed output.
    private static readonly Regex DerivativeSuffixPattern = new(
        @"^(_processed|_1_left|_2_right|_frame\d+)?\.[^.]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True if <paramref name="candidateFileName"/> (a bare file name, no directory)
    /// is a processed derivative of the job whose original base name is <paramref name="baseName"/>
    /// — i.e. it starts with that exact base name followed by one of ImageProcessor's known
    /// suffix shapes, not just any continuation. Use this instead of a raw `{baseName}*` glob
    /// anywhere a job's derivatives are looked up or cleaned up.</summary>
    public static bool IsDerivativeOf(string candidateFileName, string baseName)
    {
        if (!candidateFileName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            return false;
        var remainder = candidateFileName.Substring(baseName.Length);
        return DerivativeSuffixPattern.IsMatch(remainder);
    }

    /// <summary>Enumerates only the files in <paramref name="directory"/> that are genuine
    /// processed derivatives of <paramref name="originalFilePath"/>'s job — see
    /// <see cref="IsDerivativeOf"/>. Safe to use for both lookup and deletion, unlike a raw
    /// <c>Directory.EnumerateFiles(dir, $"{baseName}*")</c> glob.</summary>
    public static IEnumerable<string> EnumerateDerivatives(string directory, string originalFilePath)
    {
        if (!Directory.Exists(directory)) yield break;
        var baseName = Path.GetFileNameWithoutExtension(originalFilePath);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (IsDerivativeOf(Path.GetFileName(file), baseName))
                yield return file;
        }
    }
}
