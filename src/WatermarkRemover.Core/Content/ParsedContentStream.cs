namespace WatermarkRemover.Core.Content;

/// <summary>
/// A lexed and operation-parsed PDF content stream. Retains the original bytes so that rewrites
/// can preserve every byte outside the regions being changed.
/// </summary>
public sealed class ParsedContentStream
{
    public ParsedContentStream(byte[] source, IReadOnlyList<ContentToken> tokens, IReadOnlyList<ContentOperation> operations)
    {
        Source = source;
        Tokens = tokens;
        Operations = operations;
    }

    public byte[] Source { get; }

    public IReadOnlyList<ContentToken> Tokens { get; }

    public IReadOnlyList<ContentOperation> Operations { get; }

    public static ParsedContentStream Parse(ReadOnlySpan<byte> source)
    {
        var bytes = source.ToArray();
        var tokens = ContentLexer.Tokenize(bytes);
        var operations = GroupIntoOperations(tokens);
        return new ParsedContentStream(bytes, tokens, operations);
    }

    /// <summary>
    /// Groups tokens into operations. Arrays and dictionaries nest and act as single operands,
    /// so bracket depth is tracked while scanning for the operator keyword.
    /// </summary>
    private static IReadOnlyList<ContentOperation> GroupIntoOperations(List<ContentToken> tokens)
    {
        var operations = new List<ContentOperation>();
        var pending = new List<ContentToken>();
        int depth = 0;

        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case ContentTokenKind.ArrayStart:
                case ContentTokenKind.DictStart:
                    depth++;
                    pending.Add(token);
                    break;

                case ContentTokenKind.ArrayEnd:
                case ContentTokenKind.DictEnd:
                    depth--;
                    pending.Add(token);
                    break;

                case ContentTokenKind.Keyword when depth == 0 && !token.IsLiteralKeyword:
                    operations.Add(new ContentOperation(pending.ToList(), token));
                    pending.Clear();
                    break;

                default:
                    pending.Add(token);
                    break;
            }
        }

        if (pending.Count > 0)
            throw new FormatException($"Content stream ends with {pending.Count} dangling operand(s) and no operator.");

        return operations;
    }
}
