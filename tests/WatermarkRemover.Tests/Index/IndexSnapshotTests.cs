using WatermarkRemover.Index.Indexing;

namespace WatermarkRemover.Tests.Index;

/// <summary>
/// The snapshot is the analogue of Everything.db: it is reused only when the volume identity
/// (root + journal id + USN window) still matches, and it must never leave a half-written file.
/// </summary>
public class IndexSnapshotTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public IndexSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wm-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "index-C.bin");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a lingering handle only wastes a temp folder */ }
    }

    private static List<IndexEntry> Entries() => new()
    {
        new IndexEntry(100, FileIndexStore.RootFileId, 0x10, "Users"),
        new IndexEntry(200, 100, 0x20, "报告.pdf"),
    };

    [Fact]
    public void Save_and_load_round_trips_entries_and_journal_state()
    {
        IndexSnapshot.Save(_path, @"C:\", journalId: 42, nextUsn: 9_000, Entries(), folderCount: 1);

        Assert.True(IndexSnapshot.TryLoad(_path, @"C:\", out SnapshotData? snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(42UL, snapshot!.JournalId);
        Assert.Equal(9_000L, snapshot.NextUsn);
        Assert.Equal(1, snapshot.FolderCount);
        Assert.Equal(Entries(), snapshot.Entries);
    }

    [Fact]
    public void A_snapshot_for_another_root_is_rejected()
    {
        IndexSnapshot.Save(_path, @"C:\", 42, 9_000, Entries(), 1);

        Assert.False(IndexSnapshot.TryLoad(_path, @"D:\", out _));
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        Assert.False(IndexSnapshot.TryLoad(Path.Combine(_dir, "nope.bin"), @"C:\", out _));
    }

    [Fact]
    public void A_truncated_file_is_rejected_instead_of_throwing()
    {
        IndexSnapshot.Save(_path, @"C:\", 42, 9_000, Entries(), 1);
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length / 2);

        Assert.False(IndexSnapshot.TryLoad(_path, @"C:\", out _));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Saving_replaces_the_previous_snapshot_atomically()
    {
        IndexSnapshot.Save(_path, @"C:\", 42, 1_000, Entries(), 1);
        IndexSnapshot.Save(_path, @"C:\", 42, 2_000, new List<IndexEntry>(), 0);

        Assert.True(IndexSnapshot.TryLoad(_path, @"C:\", out SnapshotData? snapshot));
        Assert.Equal(2_000L, snapshot!.NextUsn);
        Assert.Empty(snapshot.Entries);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Default_path_is_per_drive()
    {
        Assert.Equal(Path.Combine(_dir, "index-C.bin"), IndexSnapshot.DefaultPath(_dir, @"c:\"));
        Assert.Equal(Path.Combine(_dir, "index-D.bin"), IndexSnapshot.DefaultPath(_dir, @"D:\"));
        Assert.Equal(Path.Combine(_dir, "index-volume.bin"), IndexSnapshot.DefaultPath(_dir, @"\\?\"));
    }
}
