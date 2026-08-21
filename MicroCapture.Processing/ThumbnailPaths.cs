using System.IO;

namespace MicroCapture.Processing;

/// <summary>Where a batch's persisted page thumbnails live — a small (120px-wide) image per
/// page, written the moment it's captured, independent of the original/processed capture
/// file's own lifetime (the original is deleted once processing succeeds — see
/// BackgroundProcessingWorker — so a thumbnail decoded from it on resume would silently
/// disappear for any already-processed page; a persisted thumbnail file has no such
/// dependency). Scoped under the batch's own code (not flat in the project's output directory,
/// where captures/processed derivatives live) so thumbnails from different batches in the same
/// project never collide.</summary>
public static class ThumbnailPaths
{
    public static string DirectoryFor(string outputDirectory, string batchCode) =>
        Path.Combine(outputDirectory, batchCode, "Thumbnails");

    public static string FileFor(string outputDirectory, string batchCode, int pageNumber) =>
        Path.Combine(DirectoryFor(outputDirectory, batchCode), $"{pageNumber:D6}.png");
}
