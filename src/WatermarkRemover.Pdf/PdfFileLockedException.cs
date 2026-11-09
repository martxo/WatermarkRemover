using System.IO;

namespace WatermarkRemover.Pdf;

/// <summary>
/// A write could not land because another program (WPS, a viewer, a scanner's watcher) holds the
/// PDF. Carries the holder's name so the preview can tell the user exactly what to close instead
/// of failing with a bare IOException — the original file is untouched.
/// </summary>
public sealed class PdfFileLockedException : IOException
{
    public PdfFileLockedException(string message, string path, string holders, Exception? inner = null)
        : base(message, inner)
    {
        Path = path;
        Holders = holders;
    }

    /// <summary>The PDF that could not be written.</summary>
    public string Path { get; }

    /// <summary>Human-readable holders, e.g. "WPS Office（wps.exe，pid 1234）".</summary>
    public string Holders { get; }
}
