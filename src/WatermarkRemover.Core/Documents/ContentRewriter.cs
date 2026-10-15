using System.Text;
using WatermarkRemover.Core.Content;

namespace WatermarkRemover.Core.Documents;

/// <summary>Rewrites a content stream by deleting selected operations.</summary>
/// <remarks>
/// Deletion is span-based: only the source bytes of the removed operations are dropped, and if an
/// operation sits on its own line the surrounding line break is collapsed too. Everything else is
/// preserved byte-for-byte.
/// </remarks>
public static class ContentRewriter
{
    public static byte[] DeleteOperations(ParsedContentStream stream, IEnumerable<ContentOperation> operations)
    {
        var spans = operations
            .Select(op => (op.Start, op.End))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();

        if (spans.Count == 0) return stream.Source;

        // Merge overlapping or adjacent spans.
        var merged = new List<(int Start, int End)> { spans[0] };
        for (int i = 1; i < spans.Count; i++)
        {
            var last = merged[^1];
            if (spans[i].Start <= last.End)
                merged[^1] = (last.Start, Math.Max(last.End, spans[i].End));
            else
                merged.Add(spans[i]);
        }

        byte[] source = stream.Source;
        var output = new MemoryStream(source.Length);
        int cursor = 0;

        foreach (var (start, end) in merged)
        {
            int copyEnd = start;
            if (copyEnd > cursor)
                output.Write(source, cursor, copyEnd - cursor);

            // Swallow a single trailing newline so no blank line is left behind.
            cursor = end;
            if (cursor < source.Length && source[cursor] == (byte)'\r') cursor++;
            if (cursor < source.Length && source[cursor] == (byte)'\n') cursor++;
        }

        if (cursor < source.Length)
            output.Write(source, cursor, source.Length - cursor);

        return output.ToArray();
    }

    /// <summary>Text view of a content stream, used by tests and diagnostics.</summary>
    public static string ToText(ReadOnlySpan<byte> content) => Encoding.Latin1.GetString(content);
}
