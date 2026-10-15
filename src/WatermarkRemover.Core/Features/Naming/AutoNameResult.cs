namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Outcome of content-based naming: the extracted customer name plus the document type short
/// name. When <see cref="CanName"/> is false the caller must fall back to the default naming.
/// </summary>
public sealed class AutoNameResult
{
    private AutoNameResult(string personName, string typeShortName)
    {
        PersonName = personName;
        TypeShortName = typeShortName;
    }

    /// <summary>Customer name extracted from the document, e.g. 林孝明.</summary>
    public string PersonName { get; }

    /// <summary>Short type name used for naming, e.g. 执照 / 户口本.</summary>
    public string TypeShortName { get; }

    /// <summary>File name stem the caller should use, e.g. 林孝明执照.</summary>
    public string SuggestedStem => PersonName + TypeShortName;

    public static AutoNameResult Create(string personName, string typeShortName) =>
        new(personName, typeShortName);
}
