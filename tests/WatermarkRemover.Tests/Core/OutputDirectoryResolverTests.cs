using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// Resolution contract shared by scans and phone uploads: the configured directory wins, an empty
/// setting lands on the Desktop ITSELF (no hidden subfolder), and an unusable Desktop degrades to
/// the application root.
/// </summary>
public sealed class OutputDirectoryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"output-dir-tests-{Guid.NewGuid():N}");

    public OutputDirectoryResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Uses_and_creates_the_configured_directory_when_set()
    {
        string configured = Path.Combine(_root, "out");

        Assert.Equal(configured, OutputDirectoryResolver.Resolve(configured, "pics", _root));
        Assert.True(Directory.Exists(configured));
    }

    [Fact]
    public void Falls_back_to_the_desktop_when_not_configured()
    {
        string desktop = Path.Combine(_root, "Desktop");

        Assert.Equal(desktop, OutputDirectoryResolver.Resolve(null, desktop, _root));
        Assert.Equal(desktop, OutputDirectoryResolver.Resolve("   ", desktop, _root));
    }

    [Fact]
    public void Empty_setting_resolves_to_the_desktop_itself_without_a_subfolder()
    {
        string desktop = Path.Combine(_root, "Desktop");
        string resolved = OutputDirectoryResolver.Resolve(string.Empty, desktop, Path.Combine(_root, "app-root"));

        Assert.Equal(desktop, resolved);
        Assert.DoesNotContain("手机传文件", resolved);
        Assert.False(Directory.Exists(Path.Combine(desktop, "手机传文件")));
    }

    [Fact]
    public void Falls_back_to_the_app_root_when_the_desktop_is_unavailable()
    {
        // An existing FILE path makes CreateDirectory fail, standing in for an unwritable folder.
        string blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        string unusableDesktop = Path.Combine(blocker, "Desktop");
        string fallback = Path.Combine(_root, "app-root");

        Assert.Equal(fallback, OutputDirectoryResolver.Resolve(null, unusableDesktop, fallback));
        Assert.Equal(_root, OutputDirectoryResolver.Resolve(null, "", _root));
        Assert.True(Directory.Exists(fallback));
    }

    [Fact]
    public void Reads_the_scan_specific_setting_not_the_watermark_output_directory()
    {
        // Scans have their own output directory; the watermark feature's fields must not leak in
        // (they used to — scans silently followed OutputMode/OutputDirectory).
        var settings = new AppSettings
        {
            OutputMode = OutputMode.CustomDirectory,
            OutputDirectory = Path.Combine(_root, "watermark-out"),
            ScanOutputDirectory = Path.Combine(_root, "scan-out"),
        };

        string resolved = OutputDirectoryResolver.Resolve(settings.ScanOutputDirectory, _root);
        Assert.Equal(Path.Combine(_root, "scan-out"), resolved);
        Assert.True(Directory.Exists(Path.Combine(_root, "scan-out")));
    }
}
