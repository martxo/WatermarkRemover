namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Facade for content-based naming: recognised text lines in, an
/// <see cref="AutoNameResult"/> out (or null when the document cannot be named confidently).
/// Pipeline: classify the document type, then extract the customer name via the type's labels.
/// </summary>
public static class SmartNamingService
{
    /// <summary>Returns null when the type is unrecognised or the name cannot be extracted.</summary>
    public static AutoNameResult? TryBuildName(IReadOnlyList<string> ocrLines)
    {
        if (ocrLines is null || ocrLines.Count == 0) return null;

        var profile = DocumentClassifier.Classify(string.Join("\n", ocrLines));
        if (profile is null) return null;

        string? personName = NameExtractor.Extract(ocrLines, profile);
        return personName is null
            ? null
            : AutoNameResult.Create(personName, profile.TypeShortName);
    }
}
