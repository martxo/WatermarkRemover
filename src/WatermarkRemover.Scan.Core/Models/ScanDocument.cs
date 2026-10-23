namespace WatermarkRemover.Scan.Core.Models;

/// <summary>
/// The document handed to output targets: ordered pages, the naming decision and the scan context.
/// </summary>
public sealed class ScanDocument
{
    public required IReadOnlyList<ScannedPage> Pages { get; init; }

    public required ScanSettings Settings { get; init; }

    /// <summary>Final base file name (without extension), already resolved from the macro template.</summary>
    public required string BaseName { get; init; }

    /// <summary>Confidence-ordered suggestions from the document-type profiles (differentiator).</summary>
    public IReadOnlyList<NamingSuggestion> NamingSuggestions { get; init; } =
        Array.Empty<NamingSuggestion>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string SourceName { get; init; } = string.Empty;

    /// <summary>Plain OCR text concatenated across pages, used by naming profiles and the text sidecar.</summary>
    public string CombinedText { get; init; } = string.Empty;

    public int PageCount => Pages.Count;
}

/// <summary>
/// A name proposed by a naming profile, with the evidence that produced it so the user can audit
/// (and override) the automation.
/// </summary>
public sealed record NamingSuggestion(string ProfileId, string FileName, double Score, string Reason);
