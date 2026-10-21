using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// UpdateStorage's residue model: the Velopack cache must keep the LATEST full package (it is
/// the diff base every future delta needs) while old versions, applied deltas and partial
/// downloads are residue; the legacy %TEMP% channel's zips/staging/script leftovers are all
/// residue. Driven through the public Describe/Clean API with injected directories, so no test
/// touches the real %TEMP% or the dev install's package cache.
/// </summary>
public class UpdateStorageTests : IDisposable
{
    private readonly string _root;
    private readonly string _packages;
    private readonly string _temp;

    public UpdateStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wr-ustorage-{Guid.NewGuid():N}");
        _packages = Path.Combine(_root, "packages");
        _temp = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_packages);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private void Put(string dir, string name, int bytes = 100)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    // ── Velopack packages cache ────────────────────────────────────────────────────────────────

    [Fact]
    public void Latest_full_is_kept_old_fulls_deltas_and_partials_are_residue()
    {
        Put(_packages, "WatermarkRemover-9.9.9-full.nupkg", 500); // latest → kept (delta base)
        Put(_packages, "WatermarkRemover-1.0.0-full.nupkg");      // old full → residue
        Put(_packages, "WatermarkRemover-1.0.0-delta.nupkg");     // applied delta → residue
        Put(_packages, "WatermarkRemover-9.9.9-full.nupkg.tmp");  // partial download → residue

        var location = UpdateStorage.Describe(_packages, _temp).Single(l => l.Title == "安装包缓存");

        Assert.Equal(3, location.ResidueCount);
        Assert.Equal(300, location.ResidueBytes); // three 100-byte residue files
    }

    [Fact]
    public void Clean_removes_residue_but_keeps_the_latest_full()
    {
        Put(_packages, "WatermarkRemover-9.9.9-full.nupkg", 500);
        Put(_packages, "WatermarkRemover-1.0.0-full.nupkg");
        Put(_packages, "WatermarkRemover-1.0.0-delta.nupkg");
        Put(_packages, "WatermarkRemover-9.9.9-full.nupkg.tmp");

        var (deleted, freed) = UpdateStorage.Clean(_packages, _temp);

        Assert.True(deleted >= 3);
        Assert.True(freed >= 300);
        Assert.Equal(
            new[] { "WatermarkRemover-9.9.9-full.nupkg" },
            Directory.GetFiles(_packages).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Current_version_full_survives_even_when_older_than_latest()
    {
        // The Velopack delta base is BOTH the latest full AND the currently-installed full:
        // a current-version package must survive cleanup even when the feed has a newer one,
        // otherwise the next update silently loses its delta path.
        string current = UpdateService.CurrentVersion.ToString(); // test host runs the app assembly
        Put(_packages, $"WatermarkRemover-{current}-full.nupkg", 500);
        Put(_packages, "WatermarkRemover-9.9.9-full.nupkg", 500);
        Put(_packages, "WatermarkRemover-1.0.0-full.nupkg");

        UpdateStorage.Clean(_packages, _temp);

        Assert.Equal(
            new[] { $"WatermarkRemover-{current}-full.nupkg", "WatermarkRemover-9.9.9-full.nupkg" },
            Directory.GetFiles(_packages).Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    // ── Legacy %TEMP% channel ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Legacy_zips_staging_dirs_and_scripts_are_residue()
    {
        Put(_temp, "WatermarkRemover-1.0.0.zip", 300);
        Put(_temp, "unrelated-file.txt");
        Put(_temp, "wr-update-abc.cmd");
        string staging = Path.Combine(_temp, "wr-staging-123");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "inner.bin"), new byte[50]);

        var location = UpdateStorage.Describe(_packages, _temp).Single(l => l.Title == "更新下载临时区");

        Assert.Equal(3, location.ResidueCount); // zip + cmd + staging dir; unrelated file untouched
        Assert.Equal(450, location.ResidueBytes); // zip 300 + cmd 100 + staging contents 50
    }

    [Fact]
    public void Clean_removes_legacy_leftovers_and_leaves_foreign_files()
    {
        Put(_temp, "WatermarkRemover-1.0.0.zip", 300);
        Put(_temp, "unrelated-file.txt");
        Put(_temp, "wr-update-abc.cmd");
        string staging = Path.Combine(_temp, "wr-staging-123");
        Directory.CreateDirectory(staging);

        var (deleted, freed) = UpdateStorage.Clean(_packages, _temp);

        Assert.Equal(3, deleted);
        Assert.True(freed >= 300);
        Assert.True(File.Exists(Path.Combine(_temp, "unrelated-file.txt")));
        Assert.False(Directory.Exists(staging));
    }

    // ── Edge cases ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_directories_report_empty_and_clean_is_a_noop()
    {
        string gone = Path.Combine(_root, "gone");
        Directory.Delete(_packages);
        Directory.Delete(_temp);

        var locations = UpdateStorage.Describe(gone, gone);

        Assert.All(locations, l => Assert.False(l.Exists));
        Assert.All(locations, l => Assert.Equal(0, l.ResidueCount));
        var (deleted, _) = UpdateStorage.Clean(gone, gone);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void Package_version_parsing_accepts_full_and_delta_names_only()
    {
        Assert.True(UpdateStorage.TryParsePackageVersion("WatermarkRemover-1.16.2-full.nupkg", "full", out var full));
        Assert.Equal(new Version(1, 16, 2), full);
        Assert.True(UpdateStorage.TryParsePackageVersion("WatermarkRemover-1.16.2-delta.nupkg", "delta", out var delta));
        Assert.Equal(new Version(1, 16, 2), delta);
        Assert.False(UpdateStorage.TryParsePackageVersion("WatermarkRemover-1.16.2-delta.nupkg", "full", out _));
        Assert.False(UpdateStorage.TryParsePackageVersion("WatermarkRemover-full.nupkg", "full", out _));
        Assert.False(UpdateStorage.TryParsePackageVersion("OtherApp-1.0.0-full.nupkg", "full", out _));
        Assert.False(UpdateStorage.TryParsePackageVersion("WatermarkRemover-notaversion-full.nupkg", "full", out _));
    }
}
