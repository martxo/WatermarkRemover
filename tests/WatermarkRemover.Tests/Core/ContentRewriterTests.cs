using System.Text;
using WatermarkRemover.Core.Content;
using WatermarkRemover.Core.Documents;

namespace WatermarkRemover.Tests.Core;

public class ContentRewriterTests
{
    private static ParsedContentStream Parse(string content) =>
        ParsedContentStream.Parse(Encoding.Latin1.GetBytes(content));

    private static string Rewrite(string content, params string[] operators)
    {
        var stream = Parse(content);
        var ops = stream.Operations.Where(o => operators.Contains(o.Operator)).ToList();
        byte[] result = ContentRewriter.DeleteOperations(stream, ops);
        return ContentRewriter.ToText(result);
    }

    [Fact]
    public void Deletes_only_the_targeted_operation()
    {
        const string content = "q 104 0 0 30 723 10 cm /Im2 Do Q\n";
        string result = Rewrite(content, "Do");

        Assert.DoesNotContain("Do", result);
        Assert.Contains("cm", result);
    }

    [Fact]
    public void Removes_the_trailing_newline_of_a_deleted_line()
    {
        const string content = "q\n720 0 0 543 60 52 cm /Im1 Do\nq 104 0 0 30 723 10 cm /Im2 Do\nQ\n";
        var stream = Parse(content);

        // Delete only the second draw line, keeping the first.
        var secondDraw = stream.Operations.Last(o => o.Operator == "Do");
        byte[] result = ContentRewriter.DeleteOperations(stream, new[] { secondDraw });
        string text = ContentRewriter.ToText(result);

        Assert.Contains("/Im1 Do", text);
        Assert.DoesNotContain("/Im2 Do", text);
        // No blank line is left where the watermark line was.
        Assert.DoesNotContain("\n\n", text);
    }

    [Fact]
    public void Preserves_untouched_bytes_exactly()
    {
        const string content = "q\nq 104 0 0 30 723 10 cm /Im2 Do\nQ\n";
        var stream = Parse(content);
        var op = stream.Operations.Single(o => o.Operator == "Do");

        byte[] result = ContentRewriter.DeleteOperations(stream, new[] { op });
        string text = ContentRewriter.ToText(result);

        // The q/Q wrappers and everything around the deleted span are byte-identical.
        Assert.StartsWith("q\n", text);
        Assert.EndsWith("Q\n", text);
    }

    [Fact]
    public void Deleting_nothing_returns_the_original_bytes()
    {
        const string content = "q Q\n";
        var stream = Parse(content);

        byte[] result = ContentRewriter.DeleteOperations(stream, Array.Empty<ContentOperation>());

        Assert.Equal(stream.Source, result);
    }

    [Fact]
    public void Merges_overlapping_deletions()
    {
        const string content = "q 1 0 0 1 0 0 cm /Im2 Do Q\n";
        var stream = Parse(content);
        var ops = stream.Operations.ToList(); // q, cm, Do, Q

        byte[] result = ContentRewriter.DeleteOperations(stream, ops);

        Assert.Empty(ContentRewriter.ToText(result).Trim());
    }

    [Fact]
    public void Handles_multiple_deletions_across_the_stream()
    {
        const string content =
            "q 1 0 0 1 0 0 cm /A Do\n" +
            "q 2 0 0 2 0 0 cm /B Do\n" +
            "q 3 0 0 3 0 0 cm /C Do\n";
        var stream = Parse(content);
        var ops = stream.Operations.Where(o => o.Operator == "Do").Take(2).ToList();

        byte[] result = ContentRewriter.DeleteOperations(stream, ops);
        string text = ContentRewriter.ToText(result);

        Assert.DoesNotContain("/A Do", text);
        Assert.DoesNotContain("/B Do", text);
        Assert.Contains("/C Do", text);
    }
}
