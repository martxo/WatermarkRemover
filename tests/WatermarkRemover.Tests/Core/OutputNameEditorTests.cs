using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

public class OutputNameEditorTests
{
    // ---------- BuildRenamedName ----------

    [Fact]
    public void Rename_keeps_the_current_extension()
    {
        Assert.Equal("新名字.pdf", OutputNameEditor.BuildRenamedName("旧的.pdf", "新名字"));
    }

    [Fact]
    public void Rename_drops_a_typed_extension()
    {
        // The output must stay the same document type.
        Assert.Equal("新名字.pdf", OutputNameEditor.BuildRenamedName("旧的.pdf", "新名字.txt"));
        Assert.Equal("新名字.pdf", OutputNameEditor.BuildRenamedName("旧的.pdf", "新名字.pdf"));
    }

    [Theory]
    [InlineData("a<b>c.pdf", "a_b_c.pdf")]
    [InlineData("报告:_final?.pdf", "报告__final_.pdf")]
    public void Rename_replaces_invalid_characters(string desired, string expected)
    {
        Assert.Equal(expected, OutputNameEditor.BuildRenamedName("old.pdf", desired));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("<>")]
    public void Rename_returns_null_for_unusable_names(string desired)
    {
        Assert.Null(OutputNameEditor.BuildRenamedName("old.pdf", desired));
    }

    [Fact]
    public void Rename_trims_whitespace_and_trailing_dots()
    {
        Assert.Equal("名字.pdf", OutputNameEditor.BuildRenamedName("old.pdf", "  名字.  "));
    }

    // ---------- WithSuffix / IsSuffixApplied ----------

    [Fact]
    public void Suffix_is_appended_before_the_extension()
    {
        Assert.Equal("xxx身份证.pdf", OutputNameEditor.WithSuffix("xxx.pdf", "身份证"));
    }

    [Fact]
    public void Clicking_the_same_suffix_again_removes_it()
    {
        string renamed = OutputNameEditor.WithSuffix("xxx.pdf", "身份证");
        Assert.Equal("xxx.pdf", OutputNameEditor.WithSuffix(renamed, "身份证"));
    }

    [Fact]
    public void Suffix_toggles_work_with_counter_names()
    {
        Assert.Equal("xxx (2)征信.pdf", OutputNameEditor.WithSuffix("xxx (2).pdf", "征信"));
        Assert.Equal("xxx (2).pdf", OutputNameEditor.WithSuffix("xxx (2)征信.pdf", "征信"));
    }

    [Fact]
    public void Suffix_prefixes_are_not_toggled()
    {
        // "身份证" inside "身份证背面" must not count as applied.
        Assert.False(OutputNameEditor.IsSuffixApplied("身份证背面.pdf", "身份证"));
        Assert.Equal("身份证背面身份证.pdf", OutputNameEditor.WithSuffix("身份证背面.pdf", "身份证"));
    }

    [Fact]
    public void Empty_suffix_is_ignored()
    {
        Assert.Equal("xxx.pdf", OutputNameEditor.WithSuffix("xxx.pdf", "  "));
        Assert.False(OutputNameEditor.IsSuffixApplied("xxx.pdf", ""));
    }

    [Fact]
    public void Default_quick_suffixes_are_present()
    {
        var settings = new AppSettings();
        Assert.Equal(new[] { "身份证", "户口本", "房产证", "征信", "执照" }, settings.QuickSuffixes);
    }

    // ---------- FindAvailablePath ----------

    [Fact]
    public void FindAvailablePath_returns_the_name_when_free()
    {
        using var dir = new TempDirectory();
        string path = OutputNameGenerator.FindAvailablePath(dir.Path, "a.pdf");
        Assert.Equal(Path.Combine(dir.Path, "a.pdf"), path);
    }

    [Fact]
    public void FindAvailablePath_counts_up_past_existing_files()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "a.pdf"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "a (2).pdf"), "x");

        string path = OutputNameGenerator.FindAvailablePath(dir.Path, "a.pdf");

        Assert.Equal(Path.Combine(dir.Path, "a (3).pdf"), path);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "wrm-tests-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
