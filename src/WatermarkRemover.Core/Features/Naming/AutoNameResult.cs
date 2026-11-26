namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Outcome of content-based naming: the extracted customer name plus the document type short
/// name. The caller falls back to the default (timestamp) naming when the pipeline yields no
/// result at all.
/// </summary>
public sealed class AutoNameResult
{
    private AutoNameResult(string personName, string typeShortName, string suggestedStem)
    {
        PersonName = personName;
        TypeShortName = typeShortName;
        SuggestedStem = suggestedStem;
    }

    /// <summary>Customer name extracted from the document, e.g. 林孝明.</summary>
    public string PersonName { get; }

    /// <summary>Short type name used for naming, e.g. 执照 / 户口本.</summary>
    public string TypeShortName { get; }

    /// <summary>
    /// File name stem the caller should use. Document words never ride along: the credit
    /// report's 庞结 + 征信 stems to 庞结 (the old 庞结征信.pdf), while type markers that are
    /// not document words stay (林孝明执照). See <see cref="DocumentWordFilter"/>.
    /// </summary>
    public string SuggestedStem { get; }

    /// <summary>Set when the independent enlarged re-read could not confirm this result; the
    /// caller labels the row / report for a human check instead of trusting the name silently.</summary>
    public bool NeedsManualReview { get; private set; }

    /// <summary>Why the recheck flagged it (shown on the row and in the recognition report).</summary>
    public string? ReviewEvidence { get; private set; }

    /// <summary>Marks the suggestion for a human check and returns the same instance (chaining
    /// at the naming call site does not lose the original fields).</summary>
    public AutoNameResult MarkForReview(string evidence)
    {
        NeedsManualReview = true;
        ReviewEvidence = evidence;
        return this;
    }

    /// <summary>
    /// The stem rule on its own, so the recognition report suggests exactly the file name the
    /// naming pipeline produces — the two used to be hand-rolled concatenations that drifted.
    /// </summary>
    public static string BuildStem(string personName, string typeShortName)
    {
        string name = DocumentWordFilter.StripName(personName) ?? personName;
        return DocumentWordFilter.StripName(name + typeShortName) ?? name;
    }

    public static AutoNameResult Create(string personName, string typeShortName) =>
        new(DocumentWordFilter.StripName(personName) ?? personName, typeShortName,
            BuildStem(personName, typeShortName));

    /// <summary>
    /// Placeholder naming (「谁的」+ type). The stem is a deliberate rename-me marker whose
    /// type hint is the whole point, so document-word trimming must not eat it (谁的企业征信).
    /// </summary>
    public static AutoNameResult CreatePlaceholder(string placeholder, string typeShortName) =>
        new(placeholder, typeShortName, placeholder + typeShortName);
}
