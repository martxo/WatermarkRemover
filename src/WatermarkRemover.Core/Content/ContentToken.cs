namespace WatermarkRemover.Core.Content;

public enum ContentTokenKind
{
    Number,
    Name,
    String,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
    Keyword,
    Comment,
}

/// <summary>A lexical token of a PDF content stream, keeping its exact source span.</summary>
public readonly record struct ContentToken(
    ContentTokenKind Kind,
    string Text,
    int Start,
    int Length)
{
    public int End => Start + Length;

    /// <summary>True for keywords that represent literal objects rather than operators.</summary>
    public bool IsLiteralKeyword => Kind == ContentTokenKind.Keyword &&
                                    Text is "true" or "false" or "null";

    /// <summary>Numeric value; only meaningful for <see cref="ContentTokenKind.Number"/>.</summary>
    public double AsNumber() => double.Parse(Text, System.Globalization.CultureInfo.InvariantCulture);
}
