using System.IO;
using System.Linq;
using System.Text;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// The cloud update repository relies on two pure pieces: content-defined chunks (only changed
/// regions are uploaded/downloaded) and the manifest diff (which files a client must replace).
/// </summary>
public sealed class CloudUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wr-cloud-test-{Guid.NewGuid():N}");

    public CloudUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Chunks_reassemble_the_original_bytes()
    {
        var random = new Random(42);
        var data = new byte[5 * 1024 * 1024 + 123];
        random.NextBytes(data);

        var chunks = ContentChunker.Split(data);

        Assert.True(chunks.Count > 1);
        Assert.Equal(data.Length, chunks.Sum(c => c.Length));
        using var restored = new MemoryStream();
        foreach (ContentChunk chunk in chunks)
        {
            restored.Write(data, chunk.Offset, chunk.Length);
        }

        Assert.Equal(data, restored.ToArray());
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, ContentChunker.MaxChunkSize));
    }

    [Fact]
    public void Chunking_is_deterministic_and_stable_across_a_small_edit()
    {
        byte[] original = BuildText(2 * 1024 * 1024);
        byte[] edited = (byte[])original.Clone();
        for (int i = 900_000; i < 900_100; i++) edited[i] = (byte)(edited[i] ^ 0xFF);

        var first = ContentChunker.Split(original);
        var second = ContentChunker.Split(original);
        var changed = ContentChunker.Split(edited);

        Assert.Equal(first.Select(c => c.Sha256), second.Select(c => c.Sha256));

        // Most of the file is unchanged, so most chunks must still match: this is what makes a
        // release upload a couple of megabytes instead of the whole 300 MB payload.
        var before = first.Select(c => c.Sha256).ToHashSet(StringComparer.Ordinal);
        int reused = changed.Count(c => before.Contains(c.Sha256));
        Assert.True(reused >= changed.Count / 2, $"只复用了 {reused}/{changed.Count} 个分块");
    }

    [Fact]
    public void Planner_only_replaces_changed_files_and_requests_missing_chunks()
    {
        string remoteRoot = Path.Combine(_root, "remote");
        string localRoot = Path.Combine(_root, "local");
        Directory.CreateDirectory(remoteRoot);
        Directory.CreateDirectory(localRoot);

        File.WriteAllText(Path.Combine(remoteRoot, "same.txt"), "same content");
        File.WriteAllText(Path.Combine(remoteRoot, "changed.txt"), "new version of the content");
        File.WriteAllText(Path.Combine(remoteRoot, "added.txt"), "brand new file");

        File.WriteAllText(Path.Combine(localRoot, "same.txt"), "same content");
        File.WriteAllText(Path.Combine(localRoot, "changed.txt"), "old version of the content");
        File.WriteAllText(Path.Combine(localRoot, "removed.txt"), "to be deleted");

        CloudManifest remote = CloudUpdatePlanner.BuildManifest(remoteRoot, "1.1.0", "test", null);
        remote.Deleted = new List<string> { "removed.txt" };

        LocalFileIndex local = LocalFileIndex.Scan(localRoot,
            remote.Files.Select(f => f.Path).Concat(remote.Deleted));
        CloudUpdatePlan plan = CloudUpdatePlanner.CreatePlan("1.0.0", remote, local);

        Assert.Equal(new[] { "added.txt", "changed.txt" },
            plan.ChangedFiles.Select(f => f.Path).OrderBy(p => p).ToArray());
        Assert.Contains("removed.txt", plan.DeletedFiles);
        Assert.DoesNotContain(plan.RequiredChunks,
            c => remote.Files.First(f => f.Path == "same.txt").Chunks.Any(x => x.Sha256 == c.Sha256));
        Assert.True(plan.DownloadBytes > 0);
    }

    [Fact]
    public void Publisher_manifest_excludes_symbols_and_records_removed_files()
    {
        string firstRoot = Path.Combine(_root, "v1");
        string secondRoot = Path.Combine(_root, "v2");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);

        File.WriteAllText(Path.Combine(firstRoot, "app.exe"), "payload");
        File.WriteAllText(Path.Combine(firstRoot, "app.pdb"), "symbols");
        File.WriteAllText(Path.Combine(secondRoot, "app.exe"), "payload");

        CloudManifest first = CloudUpdatePlanner.BuildManifest(firstRoot, "1.0.0", "first", null);
        CloudManifest second = CloudUpdatePlanner.BuildManifest(secondRoot, "1.1.0", "second", first);

        Assert.Single(first.Files);
        Assert.Equal("app.exe", first.Files[0].Path);
        Assert.Empty(second.Deleted); // same file set → nothing removed
    }

    private static byte[] BuildText(int length)
    {
        var builder = new StringBuilder(length);
        int line = 0;
        while (builder.Length < length)
        {
            builder.Append("line ").Append(line++).Append(" content content content\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString(0, length));
    }
}
