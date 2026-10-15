namespace WatermarkRemover.Core.Documents;

/// <summary>
/// Format-specific document I/O. The only place that touches a PDF library, which keeps Core
/// dependency-free and makes additional formats (or libraries) easy to add.
/// </summary>
public interface IDocumentSource
{
    /// <summary>Readable file extensions, e.g. <c>.pdf</c>.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>Extracts page geometry, content streams and annotations.</summary>
    RawDocument Extract(string inputPath);

    /// <summary>Writes a new document with <paramref name="edits"/> applied.</summary>
    void Apply(string inputPath, string outputPath, IReadOnlyList<PageEdit> edits);
}
