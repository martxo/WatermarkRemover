using System.Xml.Linq;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The invisible-scrollbar regression (v1.15.12, 2026-10-20): the hairline ScrollBar style
/// centred the thumb's visual on BOTH axes. A Border with no content and no explicit size
/// measures to zero along the centred track axis, so every scrollbar in the app kept its
/// layout slot (hit-testing, paging, wheel routing all worked) but painted nothing — the
/// bars simply vanished, and nothing inside them could be grabbed or dragged. The theme
/// style's MinWidth=17/MinHeight=17 also leaks through an implicit style that does not set
/// them, silently fattening the gutters.
///
/// Guards, source-level like StripTemplateResourceScopeTests:
///  1. the implicit ScrollBar style must zero MinWidth/MinHeight;
///  2. each thumb visual must not centre itself along its track's axis (the cross axis is
///     free); centring both axes is the exact zero-size trap;
///  3. the strip wheel-forwarding handler must test headroom in the matched direction
///     (up &gt; 0 / down &lt; ScrollableHeight) and negate the delta for the list step —
///     the previous code had both swapped and froze the page at the strip's top.
/// </summary>
public class ScrollBarTemplateGuardTests
{
    private static readonly string SolutionRoot = FindSolutionRoot();

    private static string FindSolutionRoot()
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(typeof(ScrollBarTemplateGuardTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WatermarkRemover.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Implicit_scrollbar_style_zeros_theme_min_sizes_and_stretches_thumbs_along_the_track()
    {
        string controlsPath = Path.Combine(SolutionRoot, "src", "WatermarkRemover.App", "Themes", "Controls.xaml");
        var doc = XDocument.Load(controlsPath);
        XNamespace ns = doc.Root!.Name.NamespaceName;

        var implicitStyle = doc.Descendants(ns + "Style")
            .SingleOrDefault(s => (string?)s.Attribute("TargetType") == "ScrollBar" && s.Attribute("Key") is null)
            ?? throw new Xunit.Sdk.XunitException("Controls.xaml must contain one implicit (keyless) ScrollBar style.");

        // 1. theme Min floors zeroed — otherwise the theme style's 17px floor leaks through
        var setters = implicitStyle.Elements(ns + "Setter").ToList();
        foreach (string prop in new[] { "MinWidth", "MinHeight" })
        {
            var setter = setters.SingleOrDefault(s => (string?)s.Attribute("Property") == prop)
                ?? throw new Xunit.Sdk.XunitException($"The implicit ScrollBar style must set {prop} (the theme style's 17px floor leaks through otherwise).");
            Assert.Equal("0", (string?)setter.Attribute("Value"));
        }

        // 2. thumb visuals stretch along the track axis. The vertical template lives in the
        // style's Template setter; the horizontal one inside the Orientation=Horizontal trigger.
        var templateSetter = setters.Single(s => (string?)s.Attribute("Property") == "Template");
        var verticalTemplate = templateSetter.Element(ns + "Setter.Value")!.Element(ns + "ControlTemplate")!;
        var horizontalTrigger = implicitStyle.Element(ns + "Style.Triggers")!
            .Elements(ns + "Trigger").Single(t => (string?)t.Attribute("Property") == "Orientation" && (string?)t.Attribute("Value") == "Horizontal");
        var horizontalTemplate = horizontalTrigger.Elements(ns + "Setter")
            .Single(s => (string?)s.Attribute("Property") == "Template")
            .Element(ns + "Setter.Value")!.Element(ns + "ControlTemplate")!;

        CheckThumbBorder(verticalTemplate, alongAxis: "VerticalAlignment", label: "vertical");
        CheckThumbBorder(horizontalTemplate, alongAxis: "HorizontalAlignment", label: "horizontal");
    }

    private static void CheckThumbBorder(XElement controlTemplate, string alongAxis, string label)
    {
        var thumbBorder = controlTemplate
            .Descendants(controlTemplate.Name.Namespace + "Track.Thumb")
            .SelectMany(t => t.Descendants(controlTemplate.Name.Namespace + "ControlTemplate"))
            .Select(ct => ct.Elements().FirstOrDefault(e => e.Name.LocalName == "Border"))
            .FirstOrDefault(b => b is not null)
            ?? throw new Xunit.Sdk.XunitException($"The {label} scrollbar template must give its thumb a Border visual.");

        string? along = (string?)thumbBorder.Attribute(alongAxis);
        if (along is not null)
            throw new Xunit.Sdk.XunitException(
                $"The {label} thumb Border must not set {alongAxis}=\"{along}\" — centring along the track axis " +
                "measures the visual to zero size and the scrollbar renders nothing (the v1.15.12 invisible-scrollbar bug).");
    }

    [Fact]
    public void Strip_wheel_forwarding_checks_headroom_in_matched_direction_and_negates_delta()
    {
        string code = File.ReadAllText(Path.Combine(SolutionRoot, "src", "WatermarkRemover.App", "MainWindow.xaml.cs"));

        // wheel-up must test headroom ABOVE (VerticalOffset > 0); the swapped check ate
        // every upward notch once the strip sat at its top — the "怎么滚都不动" freeze.
        if (!code.Contains("bool canScroll = direction > 0"))
            throw new Xunit.Sdk.XunitException("OnStripPreviewMouseWheel: wheel-up must test headroom ABOVE (direction > 0 => VerticalOffset > 0).");

        // the forwarded list step must negate the delta: wheel-up arrives as a POSITIVE delta,
        // so a non-negated step scrolls the list the WRONG way after forwarding.
        if (!code.Contains("double step = -e.Delta / 120.0 * 48.0"))
            throw new Xunit.Sdk.XunitException("OnStripPreviewMouseWheel: the forwarded step must be -e.Delta/120*48 (delta is positive when scrolling up).");
    }

    [Fact]
    public void Stable_gutter_scrollers_reserve_the_scrollbar_column_so_bar_toggles_cannot_shift_content()
    {
        var doc = XDocument.Load(Path.Combine(SolutionRoot, "src", "WatermarkRemover.App", "Themes", "Controls.xaml"));
        XNamespace ns = doc.Root!.Name.Namespace;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        // A stable gutter (fixed scrollbar column) is the whole point: while the zoom rail is
        // dragged, the strip overflows and un-overflows many times AND the record list's own
        // scrollbar toggles on every settle — an Auto gutter steals and restores 10px of
        // content width on each toggle, so first the tiles, then the whole window tremble.
        var style = doc.Descendants(ns + "Style")
            .SingleOrDefault(s => (string?)s.Attribute(x + "Key") == "StableGutterScrollViewer")
            ?? throw new Xunit.Sdk.XunitException(
                "Controls.xaml must define the StableGutterScrollViewer style (reserved scrollbar gutter).");

        var template = style.Descendants(ns + "ControlTemplate").Single();
        Assert.Contains(template.Descendants(ns + "ColumnDefinition"), c => (string?)c.Attribute("Width") == "10");

        var bar = template.Descendants(ns + "ScrollBar")
            .Single(b => (string?)b.Attribute(x + "Name") == "PART_VerticalScrollBar");
        Assert.Equal("1", (string?)bar.Attribute("Grid.Column"));

        string mainWindow = File.ReadAllText(Path.Combine(SolutionRoot, "src", "WatermarkRemover.App", "MainWindow.xaml"));
        if (!mainWindow.Contains("Style=\"{StaticResource StableGutterScrollViewer}\""))
            throw new Xunit.Sdk.XunitException("The strip ScrollViewer in MainWindow.xaml must use StableGutterScrollViewer.");

        // The record list reaches the stable gutter through a ListView template override —
        // its default template's internal ScrollViewer is otherwise unreachable.
        var listViewStyle = doc.Descendants(ns + "Style")
            .SingleOrDefault(s => (string?)s.Attribute(x + "Key") == "StableGutterListView")
            ?? throw new Xunit.Sdk.XunitException(
                "Controls.xaml must define the StableGutterListView style (JobList's stable-gutter template).");
        var listViewTemplate = listViewStyle.Descendants(ns + "ControlTemplate").Single();
        Assert.Single(listViewTemplate.Descendants(ns + "ItemsPresenter"));
        if (!listViewTemplate.ToString().Contains("StableGutterScrollViewer"))
            throw new Xunit.Sdk.XunitException("StableGutterListView's template must host its ItemsPresenter in a StableGutterScrollViewer.");

        if (!mainWindow.Contains("Style=\"{StaticResource StableGutterListView}\""))
            throw new Xunit.Sdk.XunitException("JobList in MainWindow.xaml must use StableGutterListView.");
    }
}
