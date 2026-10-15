using System.Globalization;
using System.Text;

namespace WatermarkRemover.Core.Content;

/// <summary>
/// Lexes a PDF content stream. Every token carries its exact byte span so callers can rewrite
/// the stream while leaving untouched regions byte-identical.
/// </summary>
/// <remarks>
/// Input is interpreted as PDFDocEncoding/ASCII; bytes are handled through <see cref="byte"/> values
/// so no transcoding ever occurs. Inline images (<c>BI … ID … EI</c>) are lexed as a single keyword
/// token because their payload is raw binary.
/// </remarks>
public static class ContentLexer
{
    private static readonly HashSet<byte> Whitespace = new() { 0x00, 0x09, 0x0A, 0x0C, 0x0D, 0x20 };

    private static readonly HashSet<byte> Delimiters = new()
    {
        (byte)'(', (byte)')', (byte)'<', (byte)'>', (byte)'[', (byte)']',
        (byte)'{', (byte)'}', (byte)'/', (byte)'%',
    };

    public static List<ContentToken> Tokenize(ReadOnlySpan<byte> source)
    {
        var tokens = new List<ContentToken>();
        int i = 0;
        while (i < source.Length)
        {
            byte b = source[i];
            if (IsWhitespace(b)) { i++; continue; }

            if (b == (byte)'%')
            {
                int start = i;
                while (i < source.Length && source[i] != 0x0A && source[i] != 0x0D) i++;
                tokens.Add(new ContentToken(ContentTokenKind.Comment, Decode(source, start, i - start), start, i - start));
                continue;
            }

            if (b == (byte)'/')
            {
                int start = i++;
                while (i < source.Length && !IsWhitespace(source[i]) && !Delimiters.Contains(source[i])) i++;
                tokens.Add(new ContentToken(ContentTokenKind.Name, DecodeName(source, start, i - start), start, i - start));
                continue;
            }

            if (b == (byte)'(')
            {
                int start = i;
                i = ScanLiteralString(source, i);
                tokens.Add(new ContentToken(ContentTokenKind.String, Decode(source, start, i - start), start, i - start));
                continue;
            }

            if (b == (byte)'<')
            {
                if (i + 1 < source.Length && source[i + 1] == (byte)'<')
                {
                    tokens.Add(new ContentToken(ContentTokenKind.DictStart, "<<", i, 2));
                    i += 2;
                    continue;
                }

                int start = i;
                i = ScanHexString(source, i);
                tokens.Add(new ContentToken(ContentTokenKind.String, Decode(source, start, i - start), start, i - start));
                continue;
            }

            if (b == (byte)'>')
            {
                if (i + 1 < source.Length && source[i + 1] == (byte)'>')
                {
                    tokens.Add(new ContentToken(ContentTokenKind.DictEnd, ">>", i, 2));
                    i += 2;
                    continue;
                }

                throw new FormatException($"Unexpected '>' at offset {i}.");
            }

            if (b == (byte)'[') { tokens.Add(new ContentToken(ContentTokenKind.ArrayStart, "[", i, 1)); i++; continue; }
            if (b == (byte)']') { tokens.Add(new ContentToken(ContentTokenKind.ArrayEnd, "]", i, 1)); i++; continue; }
            if (b == (byte)'{')
                throw new FormatException($"PostScript function braces are not supported in content streams (offset {i}).");
            if (b == (byte)'}')
                throw new FormatException($"Unexpected '}}' at offset {i}.");

            if (IsNumberStart(b))
            {
                int start = i;
                i++;
                while (i < source.Length && !IsWhitespace(source[i]) && !Delimiters.Contains(source[i])) i++;
                string text = Decode(source, start, i - start);
                if (!IsNumeric(text))
                    throw new FormatException($"Invalid number '{text}' at offset {start}.");
                tokens.Add(new ContentToken(ContentTokenKind.Number, text, start, i - start));
                continue;
            }

            // Operator or literal keyword.
            {
                int start = i;
                while (i < source.Length && !IsWhitespace(source[i]) && !Delimiters.Contains(source[i])) i++;
                string text = Decode(source, start, i - start);
                if (text == "BI")
                {
                    i = ScanInlineImage(source, start);
                    tokens.Add(new ContentToken(ContentTokenKind.Keyword, "BI", start, i - start));
                    continue;
                }

                tokens.Add(new ContentToken(ContentTokenKind.Keyword, text, start, i - start));
            }
        }

        return tokens;
    }

    private static bool IsWhitespace(byte b) => Whitespace.Contains(b);

    private static bool IsNumberStart(byte b) =>
        b is (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9';

    private static bool IsNumeric(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static int ScanLiteralString(ReadOnlySpan<byte> source, int start)
    {
        // Start past the opening paren with one level already open, so the matching close
        // paren brings depth back to zero and terminates the scan.
        int depth = 1;
        int i = start + 1;
        while (i < source.Length)
        {
            byte b = source[i];
            if (b == (byte)'\\')
            {
                i += 2;
                continue;
            }

            if (b == (byte)'(') depth++;
            else if (b == (byte)')')
            {
                depth--;
                if (depth == 0) return i + 1;
            }

            i++;
        }

        throw new FormatException($"Unterminated literal string starting at offset {start}.");
    }

    private static int ScanHexString(ReadOnlySpan<byte> source, int start)
    {
        int i = start + 1;
        while (i < source.Length && source[i] != (byte)'>') i++;
        if (i >= source.Length)
            throw new FormatException($"Unterminated hex string starting at offset {start}.");
        return i + 1;
    }

    /// <summary>
    /// Scans <c>BI … ID &lt;data&gt; EI</c>. Returns the offset just past <c>EI</c>.
    /// </summary>
    private static int ScanInlineImage(ReadOnlySpan<byte> source, int start)
    {
        // Locate the ID keyword.
        int i = start + 2;
        while (i < source.Length - 1)
        {
            if (source[i] == (byte)'I' && source[i + 1] == (byte)'D' &&
                (i + 2 >= source.Length || IsWhitespace(source[i + 2]) || Delimiters.Contains(source[i + 2])))
            {
                break;
            }

            i++;
        }

        if (i >= source.Length - 1)
            throw new FormatException($"Inline image at offset {start} has no ID marker.");

        // Binary data starts after "ID" plus exactly one whitespace byte.
        i += 2;
        if (i < source.Length && IsWhitespace(source[i])) i++;

        while (i < source.Length - 1)
        {
            if (source[i] == (byte)'E' && source[i + 1] == (byte)'I')
            {
                bool precededByWhitespace = i == 0 || IsWhitespace(source[i - 1]);
                bool terminated = i + 2 >= source.Length || IsWhitespace(source[i + 2]) || Delimiters.Contains(source[i + 2]);
                if (precededByWhitespace && terminated) return i + 2;
            }

            i++;
        }

        throw new FormatException($"Inline image at offset {start} is missing a terminating EI marker.");
    }

    private static string Decode(ReadOnlySpan<byte> source, int start, int length)
    {
        return Encoding.Latin1.GetString(source.Slice(start, length));
    }

    /// <summary>Normalises a name token to its decoded value (without the leading slash).</summary>
    private static string DecodeName(ReadOnlySpan<byte> source, int start, int length)
    {
        var sb = new StringBuilder(length - 1);
        for (int i = start + 1; i < start + length; i++)
        {
            if (source[i] == (byte)'#' && i + 2 < start + length)
            {
                string hex = Encoding.Latin1.GetString(source.Slice(i + 1, 2));
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                {
                    sb.Append((char)code);
                    i += 2;
                    continue;
                }
            }

            sb.Append((char)source[i]);
        }

        return sb.ToString();
    }
}
