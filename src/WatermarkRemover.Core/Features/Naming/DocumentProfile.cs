using System.Text.RegularExpressions;

namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// One weighted keyword/regex that contributes to a document type's classification score.
/// Each anchor counts at most once per document, so repeating a keyword never inflates the score.
/// </summary>
public sealed class ProfileAnchor
{
    public ProfileAnchor(string pattern, int weight)
    {
        Regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        Weight = weight;
    }

    public Regex Regex { get; }

    /// <summary>Score contribution when the anchor is present (1-5).</summary>
    public int Weight { get; }
}

/// <summary>
/// Declarative description of one document type: what makes it recognisable and which labels
/// introduce the customer's name. Adding a new document type to the system means adding one
/// instance of this class to <see cref="DocumentProfileCatalog"/> — no flow changes.
/// </summary>
public sealed class DocumentProfile
{
    public required string TypeId { get; init; }

    /// <summary>Short name used in the generated file name, e.g. 执照 / 户口本.</summary>
    public required string TypeShortName { get; init; }

    /// <summary>Classification anchors; weights should let distinctive markers dominate.</summary>
    public required IReadOnlyList<ProfileAnchor> Anchors { get; init; }

    /// <summary>
    /// Labels that introduce the person's name, longest first (e.g. 法定代表人, 经营者, 户主).
    /// The extractor tolerates OCR truncation of these labels.
    /// </summary>
    public required IReadOnlyList<string> NameLabels { get; init; }
}
