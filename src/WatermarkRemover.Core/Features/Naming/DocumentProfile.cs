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

/// <summary>How the customer's name is located for this document type.</summary>
public enum NameExtractionStrategy
{
    /// <summary>Label-anchored text shapes over the OCR line stream (default).</summary>
    LabelShapes,

    /// <summary>
    /// Fixed-layout document: the name sits at a fixed position relative to a label anchor.
    /// Extraction is geometric, from the page image (e.g. the credit report's「被查询者姓名」
    /// header cell) — never guessed from OCR text order, which watermark-pressed and multi-up
    /// sheets scramble.
    /// </summary>
    FixedRegion,

    /// <summary>
    /// The named party is an organisation, not a person: the value after the name label is a
    /// long company name (10+ chars, brackets, digits) that the 2-4 char person-name shapes
    /// would truncate or reject. The whole labelled value is taken verbatim instead (e.g.
    /// 企业信用报告's「企业名称」, a bank statement's corporate 客户名称).
    /// </summary>
    CompanyLabel,
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
    /// Fixed person-part to name the file with, bypassing all extraction, e.g.「谁的」with
    /// TypeShortName 企业征信 yields stem 谁的企业征信. For types whose subject is a PERSON the
    /// OCR cannot reliably find (the enterprise credit report names its subject in table cells
    /// that may sit on an unscanned page), so the file gets an obviously-placeholder stem the
    /// user renames by hand instead of a confidently wrong guess.
    /// </summary>
    public string? PlaceholderName { get; init; }

    /// <summary>
    /// Labels that introduce the person's name, longest first (e.g. 法定代表人, 经营者, 户主).
    /// The extractor tolerates OCR truncation of these labels.
    /// </summary>
    public required IReadOnlyList<string> NameLabels { get; init; }

    /// <summary>How the name is extracted; drives which pipeline stage owns naming.</summary>
    public NameExtractionStrategy NameStrategy { get; init; } = NameExtractionStrategy.LabelShapes;
}
