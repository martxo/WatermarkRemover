using System.Text;
using WatermarkRemover.Core.Content;

namespace WatermarkRemover.Tests.Core;

public class ContentLexerTests
{
    private static List<ContentToken> Lex(string content) =>
        ContentLexer.Tokenize(Encoding.Latin1.GetBytes(content));

    [Fact]
    public void Tokenizes_simple_draw_command()
    {
        var tokens = Lex("q 104 0 0 30 723 10 cm /Im2 Do Q");

        Assert.Equal(
            new[] { "q", "104", "0", "0", "30", "723", "10", "cm", "Im2", "Do", "Q" },
            tokens.Select(t => t.Text).ToArray());
    }

    [Fact]
    public void Preserves_exact_source_spans()
    {
        const string content = "q 104 0 0 30 723 10 cm /Im2 Do Q";
        var tokens = Lex(content);

        foreach (var token in tokens)
            Assert.Equal(token.Text, content.Substring(token.Start, token.Length).TrimStart('/'));
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("-12.25", -12.25)]
    [InlineData("+3", 3.0)]
    [InlineData(".5", 0.5)]
    [InlineData("1e2", 100.0)]
    public void Parses_numbers_invariantly(string text, double expected)
    {
        var tokens = Lex(text);
        Assert.Equal(ContentTokenKind.Number, tokens.Single().Kind);
        Assert.Equal(expected, tokens.Single().AsNumber());
    }

    [Fact]
    public void Skips_comments()
    {
        var tokens = Lex("q % this is a comment\nQ");

        Assert.Equal(new[] { "q", "% this is a comment", "Q" }, tokens.Select(t => t.Text).ToArray());
        Assert.Equal(ContentTokenKind.Comment, tokens[1].Kind);
    }

    [Fact]
    public void Reads_names_with_hex_escapes()
    {
        var tokens = Lex("/A#20B Do");

        Assert.Equal("A B", tokens[0].Text);
    }

    [Fact]
    public void Handles_nested_literal_strings()
    {
        var tokens = Lex("(outer (inner) tail) Tj");

        Assert.Equal(ContentTokenKind.String, tokens[0].Kind);
        Assert.Equal(ContentTokenKind.Keyword, tokens[1].Kind);
    }

    [Fact]
    public void Handles_hex_strings()
    {
        var tokens = Lex("<48656C6C6F> Tj");

        Assert.Equal(ContentTokenKind.String, tokens[0].Kind);
    }

    [Fact]
    public void Handles_nested_arrays_as_operands()
    {
        var tokens = Lex("q [1 2 3] cm Q");

        Assert.Equal(
            new[] { "q", "[", "1", "2", "3", "]", "cm", "Q" },
            tokens.Select(t => t.Text).ToArray());
    }

    [Fact]
    public void Handles_dictionaries()
    {
        var tokens = Lex("/GS << /LW 2 >> gs");

        // /GS  <<  /LW  2  >>  gs
        Assert.Equal(ContentTokenKind.DictStart, tokens[1].Kind);
        Assert.Equal(ContentTokenKind.DictEnd, tokens[4].Kind);
    }

    [Fact]
    public void Lexes_inline_image_as_one_token()
    {
        // Inline image with a one-byte payload between ID and EI.
        string content = "q BI /W 1 /H 1 /CS /G ID \u0001 EI Q";
        var tokens = Lex(content);

        Assert.Contains(tokens, t => t.Kind == ContentTokenKind.Keyword && t.Text == "BI");

        // BI must swallow the payload including the EI marker.
        var bi = tokens.Single(t => t.Text == "BI");
        Assert.Equal(content.IndexOf("EI", StringComparison.Ordinal) + 2, bi.End);
    }

    [Fact]
    public void Throws_on_unterminated_string()
    {
        Assert.Throws<FormatException>(() => Lex("(unterminated"));
    }

    [Fact]
    public void Round_trips_source_spans_exactly()
    {
        const string content = "q\n720.4683 0 0 543 60.76585 52 cm /Im1 Do\nQ\n";
        var tokens = Lex(content);

        foreach (var token in tokens)
            Assert.Equal(token.Text.TrimStart('/'), content.Substring(token.Start, token.Length).TrimStart('/'));
    }
}
