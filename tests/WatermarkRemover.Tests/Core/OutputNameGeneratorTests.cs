using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

public class OutputNameGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-naming-" + Guid.NewGuid().ToString("N"));

    public OutputNameGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Inserts_suffix_before_the_extension_in_the_same_directory()
    {
        string input = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(input, Array.Empty<byte>());

        string output = OutputNameGenerator.GetOutputPath(input, new AppSettings { OutputSuffix = "_clean" });

        Assert.Equal(Path.Combine(_dir, "scan_clean.pdf"), output);
    }

    [Fact]
    public void Uses_the_custom_directory_when_configured()
    {
        string input = Path.Combine(_dir, "scan.pdf");
        string target = Path.Combine(_dir, "out");
        Directory.CreateDirectory(target);

        string output = OutputNameGenerator.GetOutputPath(input, new AppSettings
        {
            OutputSuffix = "_clean",
            OutputMode = OutputMode.CustomDirectory,
            OutputDirectory = target,
        });

        Assert.Equal(Path.Combine(target, "scan_clean.pdf"), output);
    }

    [Fact]
    public void Falls_back_to_the_input_directory_when_the_custom_directory_is_empty()
    {
        string input = Path.Combine(_dir, "scan.pdf");

        string output = OutputNameGenerator.GetOutputPath(input, new AppSettings
        {
            OutputMode = OutputMode.CustomDirectory,
            OutputDirectory = "   ",
        });

        Assert.Equal(Path.Combine(_dir, "scan_no-watermark.pdf"), output);
    }

    [Fact]
    public void Appends_a_counter_instead_of_overwriting()
    {
        string input = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(Path.Combine(_dir, "scan_clean.pdf"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_dir, "scan_clean (2).pdf"), Array.Empty<byte>());

        string output = OutputNameGenerator.GetOutputPath(input, new AppSettings { OutputSuffix = "_clean" });

        Assert.Equal(Path.Combine(_dir, "scan_clean (3).pdf"), output);
    }

    [Fact]
    public void Replaces_characters_that_are_invalid_in_file_names()
    {
        Assert.Equal("a_b_c", OutputNameGenerator.SanitizeSuffix("a/b*c"));
    }

    [Fact]
    public void Falls_back_to_a_default_when_the_suffix_would_be_empty()
    {
        Assert.Equal("_no-watermark", OutputNameGenerator.SanitizeSuffix("   "));
        Assert.Equal("_no-watermark", OutputNameGenerator.SanitizeSuffix(string.Empty));
    }

    [Fact]
    public void Keeps_a_dotfile_extension_intact()
    {
        string input = Path.Combine(_dir, "archive.tar.gz");

        string output = OutputNameGenerator.GetOutputPath(input, new AppSettings { OutputSuffix = "_x" });

        Assert.Equal("archive.tar_x.gz", Path.GetFileName(output));
    }

    [Fact]
    public void Rejects_an_empty_input_path()
    {
        Assert.Throws<ArgumentException>(() =>
            OutputNameGenerator.GetOutputPath("  ", new AppSettings()));
    }
}
