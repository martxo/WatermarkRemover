using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The scan-landing crash (v1.15.11, 2026-10-20): the strip unit template referenced
/// <c>{StaticResource BoolToVis}</c> — a key defined DIRECTLY in App.xaml's root dictionary.
/// A StaticResource inside the deferred template content of the MERGED dictionary
/// Themes/Controls.xaml resolves only against Controls.xaml's own keys and the dictionaries
/// merged beside it — never against the parent's direct entries. The window itself parsed
/// fine, so the app started normally; the first scan then landed a record row, the strip's
/// wrap panel applied the item template, and the XamlParseException took the process down
/// ("扫描就闪退").
///
/// The only runtime-faithful way to catch this would be inside a live Application whose BAML
/// parse context cannot be replicated in a unit test, so the guard is source-level instead:
/// every StaticResource key referenced anywhere in Controls.xaml must be defined in
/// Controls.xaml itself or in Palette.xaml. A reference to any app/window-level key fails
/// HERE, at build time, instead of on the user's first scan.
/// </summary>
public class StripTemplateResourceScopeTests
{
    /// <summary>The App project's source directory, found by walking up from the test binary
    /// to the solution root (test outputs live in stamp-isolated run dirs of varying depth —
    /// the .sln marker is the only stable anchor).</summary>
    private static readonly string AppProjectRoot = FindAppProjectRoot();

    private static string FindAppProjectRoot()
    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(typeof(StripTemplateResourceScopeTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WatermarkRemover.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "WatermarkRemover.App");
    }

    [Fact]
    public void Every_static_resource_in_controls_xaml_resolves_from_controls_or_palette()
    {
        string controlsPath = Path.Combine(AppProjectRoot, "Themes", "Controls.xaml");
        string palettePath = Path.Combine(AppProjectRoot, "Themes", "Palette.xaml");

        var defined = new HashSet<string>(StringComparer.Ordinal);
        defined.UnionWith(DefinedKeys(XDocument.Load(palettePath)));
        defined.UnionWith(DefinedKeys(XDocument.Load(controlsPath)));

        string controls = File.ReadAllText(controlsPath);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(controls, @"\{StaticResource\s+([^\s}]+)"))
            referenced.Add(match.Groups[1].Value);

        Assert.NotEmpty(referenced); // the scan found the file's real references, not nothing

        var unresolvable = referenced.Except(defined).ToList();
        Assert.True(unresolvable.Count == 0,
            "Controls.xaml references StaticResource keys that neither it nor Palette.xaml define " +
            "(template content cannot see App-root or window-level keys — the exact mechanism " +
            "that crashed the app when the first scan landed): " +
            string.Join(", ", unresolvable));
    }

    /// <summary>Every x:Key in the document, on any element (styles, brushes, templates).</summary>
    private static IEnumerable<string> DefinedKeys(XDocument document) =>
        document.Descendants()
            .Select(e => (string?)e.Attribute("Key") ?? (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(key => key is not null)
            .Select(key => key!);
}
