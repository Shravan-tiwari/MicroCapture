using System.IO;
using System.Linq;

namespace MicroCapture.Core;

/// <summary>Shared sanitization for operator-entered text used to build filesystem paths.</summary>
public static class FileNaming
{
    /// <summary>Strips characters that are invalid in a file/directory name (and path separators),
    /// so operator input can never escape the intended output directory.</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var clean = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "untitled" : clean;
    }
}
