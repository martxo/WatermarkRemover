using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Content;

/// <summary>
/// Walks a parsed content stream tracking the graphics state so that each painted XObject can be
/// reported with its page-space bounds. Unknown operators are ignored, which keeps the interpreter
/// robust against arbitrary real-world content streams.
/// </summary>
public sealed class ContentInterpreter
{
    private readonly Stack<AffineTransform> _savedStates = new();
    private AffineTransform _current = AffineTransform.Identity;

    /// <summary>
    /// Returns every XObject painted by the stream, with its bounds in page user space.
    /// <paramref name="pageIndex"/> is carried through to the produced elements.
    /// </summary>
    public IReadOnlyList<DrawnElement> Interpret(ParsedContentStream stream, int pageIndex = 0)
    {
        var results = new List<DrawnElement>();
        _savedStates.Clear();
        _current = AffineTransform.Identity;

        foreach (var op in stream.Operations)
        {
            switch (op.Operator)
            {
                case "q":
                    _savedStates.Push(_current);
                    break;

                case "Q":
                    if (_savedStates.Count > 0) _current = _savedStates.Pop();
                    break;

                case "cm":
                    if (TryReadMatrix(op, out var matrix))
                        _current = _current.Concat(matrix);
                    break;

                case "Do":
                    if (op.TryGetName(out string name))
                    {
                        var bounds = _current.TransformUnitSquare();
                        results.Add(new DrawnElement(pageIndex, name, bounds, op));
                    }
                    break;
            }
        }

        return results;
    }

    private static bool TryReadMatrix(ContentOperation op, out AffineTransform matrix)
    {
        matrix = AffineTransform.Identity;
        if (op.Operands.Count != 6) return false;

        Span<double> v = stackalloc double[6];
        for (int i = 0; i < 6; i++)
        {
            if (op.Operands[i].Kind != ContentTokenKind.Number) return false;
            v[i] = op.Operands[i].AsNumber();
        }

        matrix = new AffineTransform(v[0], v[1], v[2], v[3], v[4], v[5]);
        return true;
    }
}
