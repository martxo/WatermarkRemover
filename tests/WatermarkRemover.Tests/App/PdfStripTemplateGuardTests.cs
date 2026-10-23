using System.Xml.Linq;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The duplicated-plus regression (v1.15.16, 2026-10-20): the strip unit's trailing bookend
/// was null for every interior unit, but a ContentControl instantiates its ContentTemplate
/// even for null content — so every pair of adjacent pages carried a second, dead plus slot.
/// That stayed invisible while the old design revealed the plus only through hover opacity,
/// and surfaced the moment the plus went always-visible. The plus visibility itself flipped
/// twice by user request (always-visible 1.15.15 → hover-revealed 1.15.17), so both shapes
/// are guarded here. Source-level guards like ScrollBarTemplateGuardTests:
///  1. every strip unit is exactly one gap plus one tile — the trailing bookend is gone
///     (it widened only the last unit and bumped that page to its own row, the 2026-10-22
///     hole report), and a third slot must never come back;
///  2. the gap plus must be hidden at rest, lit on hover, and while a scan/rotate/orient
///     pass holds the file (IsEnabled=False): hidden at rest, dimmed under hover — declared
///     last so the dim wins.
/// </summary>
public class PdfStripTemplateGuardTests
{
    private static readonly string SolutionRoot = FindSolutionRoot();

    private static string FindSolutionRoot()
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(typeof(PdfStripTemplateGuardTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WatermarkRemover.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (XDocument Doc, XNamespace Ns, XNamespace X) LoadControls()
    {
        var doc = XDocument.Load(Path.Combine(SolutionRoot, "src", "WatermarkRemover.App", "Themes", "Controls.xaml"));
        return (doc, doc.Root!.Name.Namespace, "http://schemas.microsoft.com/winfx/2006/xaml");
    }

    private static XElement TemplateWithKey(XDocument doc, XNamespace ns, XNamespace x, string key) =>
        doc.Descendants(ns + "DataTemplate").Single(t => (string?)t.Attribute(x + "Key") == key)
        ?? throw new Xunit.Sdk.XunitException($"Controls.xaml must contain a DataTemplate keyed '{key}'.");

    private static string? GlyphOpacityFrom(XElement triggerContainer, XNamespace ns, string label) =>
        triggerContainer.Descendants(ns + "Setter")
            .Where(s => (string?)s.Attribute("TargetName") == "Glyph" && (string?)s.Attribute("Property") == "Opacity")
            .Select(s => (string?)s.Attribute("Value"))
            .SingleOrDefault()
        ?? throw new Xunit.Sdk.XunitException($"{label} must set the Glyph's Opacity.");

    /// <summary>
    /// The dead-plus regression (v1.15.31, 2026-10-21): the gap buttons' CanExecute reads
    /// IsRotating / IsOrienting / PageCount, but WPF re-queries commands only on some input
    /// events — a mouse click being one. A finished scan or orientation pass that changed
    /// those flags without invalidating command state left the plus slots dead until the
    /// user's next click anywhere, which felt like "hover does nothing until I click".
    /// Guards: every flag the CanExecute reads must nudge
    /// CommandManager.InvalidateRequerySuggested() when it changes.
    /// </summary>
    [Fact]
    public void Flags_read_by_the_gap_buttons_CanExecute_invalidate_command_state_on_change()
    {
        string src = File.ReadAllText(Path.Combine(
            SolutionRoot, "src", "WatermarkRemover.App", "ViewModels", "PdfPreviewViewModel.cs"));

        static string PropertyBody(string src, string declaration)
        {
            int start = src.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{declaration} must exist in PdfPreviewViewModel.cs.");
            int end = src.IndexOf("\n    public ", start + declaration.Length, StringComparison.Ordinal);
            Assert.True(end > start, $"Could not isolate the body of {declaration}.");
            return src[start..end];
        }

        // Each notifying flag feeds CanExecute, so each setter must requery commands.
        Assert.Contains(
            "CommandManager.InvalidateRequerySuggested()",
            PropertyBody(src, "public bool IsOrienting"));
        Assert.Contains(
            "CommandManager.InvalidateRequerySuggested()",
            PropertyBody(src, "public bool IsRotating"));
        Assert.Contains(
            "CommandManager.InvalidateRequerySuggested()",
            PropertyBody(src, "public int PageCount"));

        // The orientation flag must be a notifying property — a bare field write never
        // reaches the command system at all.
        Assert.Contains("SetProperty(ref _isOrienting, value)", src);
        Assert.DoesNotContain("_isOrienting = ", src);
    }

    [Fact]
    public void Every_strip_unit_is_exactly_one_gap_and_one_tile()
    {
        var (doc, ns, x) = LoadControls();
        var unit = TemplateWithKey(doc, ns, x, "PdfStripUnitTemplate");

        // FLOW INVARIANT (the 2026-10-22 hole bug): the wrap panel packs whole units, so every
        // unit must measure the same — one plus slot plus one tile. The old trailing bookend, a
        // third slot only the last unit carried, made that unit 14px wider than its siblings; a
        // row that had room for a tile but not for tile+14 pushed the whole last page to the
        // next row and left a cell-sized hole where it should have been. The append action now
        // lives in the zoom rail, outside the flow. Never add a width-bearing part to one unit.
        var unitStack = unit.Descendants(ns + "StackPanel").Single();
        Assert.Equal(2, unitStack.Elements().Count());
        Assert.Contains(unitStack.Elements(), e => (string?)e.Attribute("Content") == "{Binding Gap}");
        Assert.Contains(unitStack.Elements(), e => (string?)e.Attribute("Content") == "{Binding Tile}");
        Assert.DoesNotContain(unitStack.Elements(), e => (string?)e.Attribute("Content") == "{Binding Tail}");
    }

    [Fact]
    public void Gap_plus_is_hidden_at_rest_lit_on_hover_and_dimmed_instead_of_lit_while_busy()
    {
        var (doc, ns, x) = LoadControls();
        var gap = TemplateWithKey(doc, ns, x, "PdfGapInsertTemplate");

        var glyph = gap.Descendants(ns + "TextBlock")
            .Single(e => (string?)e.Attribute(x + "Name") == "Glyph");
        Assert.Equal("0", (string?)glyph.Attribute("Opacity")); // hidden at rest

        var triggers = gap.Descendants(ns + "ControlTemplate.Triggers").Single();

        // hover reveals the plus
        var hover = triggers.Elements(ns + "Trigger").Single(t => (string?)t.Attribute("Property") == "IsMouseOver");
        Assert.Equal("1", GlyphOpacityFrom(hover, ns, "the hover trigger"));

        // busy hides the plus at rest (平常不显示 holds while a pass holds the file too)
        var disabled = triggers.Elements(ns + "Trigger").Single(t => (string?)t.Attribute("Property") == "IsEnabled");
        Assert.Equal("0", GlyphOpacityFrom(disabled, ns, "the IsEnabled=False trigger"));

        // busy + hover dims instead of lighting — and must be declared LAST so the dim wins
        var multi = triggers.Elements(ns + "MultiTrigger").Single();
        var conditions = multi.Element(ns + "MultiTrigger.Conditions")!.Elements(ns + "Condition").ToList();
        Assert.Contains(conditions, c => (string?)c.Attribute("Property") == "IsEnabled" && (string?)c.Attribute("Value") == "False");
        Assert.Contains(conditions, c => (string?)c.Attribute("Property") == "IsMouseOver" && (string?)c.Attribute("Value") == "True");
        Assert.Equal("0.3", GlyphOpacityFrom(multi, ns, "the busy-hover MultiTrigger"));

        var ordered = triggers.Elements().ToList();
        Assert.True(ordered.IndexOf(disabled) < ordered.IndexOf(multi),
            "The busy-hover MultiTrigger must be declared after the IsEnabled=False trigger so the dim wins.");
    }

    [Fact]
    public void Tiles_show_a_shimmering_skeleton_while_their_page_pixels_have_not_arrived()
    {
        var (doc, ns, x) = LoadControls();
        var template = TemplateWithKey(doc, ns, x, "PdfThumbnailTileTemplate");

        // A tile with a null Image must not sit as a dead white card between plus slots —
        // the skeleton layers key off exactly that null so the strip reads as "generating".
        // The switch lives in DataTemplate.Triggers: a STYLE's trigger storyboard cannot use
        // TargetName (MC4011), and the sweep must reach the named unfrozen transform by name.
        var skeletonTrigger = template.Element(ns + "DataTemplate.Triggers")!.Elements(ns + "DataTrigger")
            .Single(t => ((string?)t.Attribute("Binding"))?.Contains("Image") == true
                         && (string?)t.Attribute("Value") == "{x:Null}");
        var shown = skeletonTrigger.Elements(ns + "Setter")
            .Where(s => (string?)s.Attribute("Property") == "Visibility" && (string?)s.Attribute("Value") == "Visible")
            .Select(s => (string?)s.Attribute("TargetName"))
            .ToList();
        Assert.Contains("SkeletonBase", shown);
        Assert.Contains("ShimmerStripe", shown);
        // single-sourced: exactly one trigger switches the skeleton, keyed on the null Image
        Assert.Equal(1, template.Descendants(ns + "DataTrigger")
            .Count(t => ((string?)t.Attribute("Binding"))?.Contains("Image") == true
                        && (string?)t.Attribute("Value") == "{x:Null}"));

        // The sweep is a repeating transform animation that stops the moment pixels arrive:
        // the ExitActions of the very trigger that starts it must stop the same storyboard.
        // The transform MUST be named: a template-declared Freezable freezes, and animating
        // through a frozen instance is the mid-layout crash that killed the app (1.15.22).
        var sweep = template.Descendants(ns + "DoubleAnimation")
            .Single(a => (string?)a.Attribute("Storyboard.TargetProperty") == "X");
        Assert.Equal("Forever", (string?)sweep.Attribute("RepeatBehavior"));
        Assert.Equal("ShimmerX", (string?)sweep.Attribute("Storyboard.TargetName"));
        Assert.Single(template.Descendants(ns + "TranslateTransform")
            .Where(t => (string?)t.Attribute(x + "Name") == "ShimmerX"));
        var trigger = sweep.Ancestors(ns + "DataTrigger").Single();
        Assert.Contains(trigger.Descendants(ns + "StopStoryboard"),
            s => (string?)s.Attribute("BeginStoryboardName") == "TileShimmer");
    }
}
