using System.Text;
using WatermarkRemover.Core.Content;

namespace WatermarkRemover.Tests.Core;

public class ContentInterpreterTests
{
    private static IReadOnlyList<DrawnElement> Interpret(string content) =>
        new ContentInterpreter().Interpret(ParsedContentStream.Parse(Encoding.Latin1.GetBytes(content)));

    [Fact]
    public void Reports_bounds_of_painted_xobjects()
    {
        var drawn = Interpret("q 104 0 0 30 723 10 cm /Im2 Do Q\n");

        var element = Assert.Single(drawn);
        Assert.Equal("Im2", element.ResourceName);
        Assert.Equal(723, element.Bounds.X);
        Assert.Equal(10, element.Bounds.Y);
        Assert.Equal(104, element.Bounds.Width);
        Assert.Equal(30, element.Bounds.Height);
    }

    [Fact]
    public void Save_restore_reverts_the_transformation()
    {
        var drawn = Interpret(
            "q 2 0 0 2 0 0 cm /A Do\n" +
            "q 10 0 0 10 0 0 cm /B Do Q\n" +
            "/C Do\n");

        Assert.Equal(3, drawn.Count);
        Assert.Equal(2, drawn[0].Bounds.Width);    // inside scale(2): width 2
        Assert.Equal(20, drawn[1].Bounds.Width);   // nested: scale(2) then scale(10) -> 20
        Assert.Equal(2, drawn[2].Bounds.Width);    // restored to scale(2)
    }

    [Fact]
    public void Cumulative_cm_composition_is_applied()
    {
        // Scale x by 100, then scale both by 2: effective scale = (200, 2).
        var drawn = Interpret(
            "100 0 0 1 0 0 cm\n" +
            "2 0 0 2 0 0 cm\n" +
            "/Im Do\n");

        var element = Assert.Single(drawn);
        Assert.Equal(0, element.Bounds.X);        // no translation applied
        Assert.Equal(200, element.Bounds.Width);  // 100 * 2
        Assert.Equal(2, element.Bounds.Height);
    }

    [Fact]
    public void Cumulative_cm_translation_is_scaled_by_preceding_scale()
    {
        // Scale by 2, then translate by 100: the translation lands at 200 in page space.
        var drawn = Interpret(
            "2 0 0 2 0 0 cm\n" +
            "1 0 0 1 100 0 cm\n" +
            "/Im Do\n");

        var element = Assert.Single(drawn);
        Assert.Equal(200, element.Bounds.X);
        Assert.Equal(2, element.Bounds.Width);
    }

    [Fact]
    public void Ignores_unknown_operators_without_failing()
    {
        var drawn = Interpret("q BT /F1 12 Tf (hi) Tj ET Q\n/A Do\n");

        var element = Assert.Single(drawn);
        Assert.Equal("A", element.ResourceName);
    }

    [Fact]
    public void Unbalanced_pop_does_not_throw()
    {
        var drawn = Interpret("Q Q /A Do\n");

        var element = Assert.Single(drawn);
        Assert.Equal(1, element.Bounds.Width);
    }

    [Fact]
    public void Tracks_page_index_through_to_elements()
    {
        var stream = ParsedContentStream.Parse(Encoding.Latin1.GetBytes("/A Do\n"));
        var drawn = new ContentInterpreter().Interpret(stream, pageIndex: 4);

        Assert.Equal(4, Assert.Single(drawn).PageIndex);
    }
}
