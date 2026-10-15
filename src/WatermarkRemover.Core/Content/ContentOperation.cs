namespace WatermarkRemover.Core.Content;

/// <summary>A single content-stream operation: its operand tokens plus the operator keyword.</summary>
public sealed class ContentOperation
{
    public ContentOperation(IReadOnlyList<ContentToken> operands, ContentToken operatorToken)
    {
        Operands = operands;
        OperatorToken = operatorToken;
    }

    public IReadOnlyList<ContentToken> Operands { get; }

    public ContentToken OperatorToken { get; }

    public string Operator => OperatorToken.Text;

    /// <summary>Source span covering operands and the operator keyword.</summary>
    public int Start => Operands.Count > 0 ? Operands[0].Start : OperatorToken.Start;

    public int End => OperatorToken.End;

    /// <summary>The single name operand, when the operation takes exactly one name.</summary>
    public bool TryGetName(out string name)
    {
        if (Operands.Count == 1 && Operands[0].Kind == ContentTokenKind.Name)
        {
            name = Operands[0].Text;
            return true;
        }

        name = string.Empty;
        return false;
    }

    public override string ToString() => $"{Operator} ({Operands.Count} operands)";
}
