using System.IO;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

public class AppFoldersTests
{
    [Fact]
    public void Preferred_directory_is_used_when_writable()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wr-test-{Guid.NewGuid():N}");
        try
        {
            string root = AppFolders.ResolveRoot(dir, Path.Combine(dir, "fallback"));

            Assert.Equal(dir, root);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Falls_back_when_preferred_path_is_blocked_by_a_file()
    {
        // A file occupying the preferred path makes directory creation fail — the same condition
        // as a read-only Program Files directory, but deterministic in a test.
        string dir = Path.Combine(Path.GetTempPath(), $"wr-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string blocked = Path.Combine(dir, "blocked");
        File.WriteAllText(blocked, "not a directory");

        try
        {
            string fallback = Path.Combine(dir, "fallback");
            string root = AppFolders.ResolveRoot(blocked, fallback);

            Assert.Equal(fallback, root);
            Assert.True(Directory.Exists(root), "回退目录应被创建以便立即可用");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Writability_probe_leaves_no_files_behind()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wr-test-{Guid.NewGuid():N}");
        try
        {
            AppFolders.ResolveRoot(dir, Path.Combine(dir, "unused"));

            string[] leftovers = Directory.GetFiles(dir, ".write-probe-*.tmp");
            Assert.Empty(leftovers);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
