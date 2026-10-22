using WatermarkRemover.Index.Indexing;

namespace WatermarkRemover.Tests.Index;

/// <summary>
/// The store keeps only (FileId → ParentFileId/Name); paths are rebuilt on demand. These tests
/// pin the two invariants the USN monitor depends on: a renamed directory must invalidate cached
/// prefixes, and a deleted directory must turn its children into skipped orphans.
/// </summary>
public class FileIndexStoreTests
{
    private const uint Directory = 0x10;
    private const uint Archive = 0x20;

    private static FileIndexStore NewStore()
    {
        var store = new FileIndexStore(@"C:\");
        store.Upsert(new IndexEntry(100, FileIndexStore.RootFileId, Directory, "Users"));
        store.Upsert(new IndexEntry(110, 100, Directory, "dx"));
        store.Upsert(new IndexEntry(200, 110, Archive, "report.pdf"));
        return store;
    }

    [Fact]
    public void Root_is_normalised_with_a_trailing_backslash()
    {
        Assert.Equal(@"C:\", new FileIndexStore("C:").RootPath);
        Assert.Equal(@"D:\", new FileIndexStore(@"D:\").RootPath);
    }

    [Fact]
    public void Path_is_rebuilt_by_walking_the_parent_chain()
    {
        FileIndexStore store = NewStore();

        Assert.True(store.TryGet(200, out IndexEntry entry));
        Assert.Equal(@"C:\Users\dx\report.pdf", store.ResolvePath(entry));
        Assert.Equal(@"C:\Users\dx\", store.ResolveDirectoryPath(110));
    }

    [Fact]
    public void Folder_count_tracks_directories_only()
    {
        FileIndexStore store = NewStore();

        Assert.Equal(3, store.Count);
        Assert.Equal(2, store.FolderCount);

        store.Remove(110);
        Assert.Equal(2, store.Count);
        Assert.Equal(1, store.FolderCount);
    }

    [Fact]
    public void Renaming_a_directory_invalidates_cached_prefixes()
    {
        FileIndexStore store = NewStore();
        Assert.Equal(@"C:\Users\dx\report.pdf", store.ResolvePath(store.Entries.Single(e => e.FileId == 200)));

        // Same FRN, new name: this is what a USN RENAME_NEW_NAME record produces.
        store.Upsert(new IndexEntry(110, 100, Directory, "dx-new"));

        Assert.Equal(@"C:\Users\dx-new\report.pdf", store.ResolvePath(store.Entries.Single(e => e.FileId == 200)));
        Assert.Equal(@"C:\Users\dx-new\", store.ResolveDirectoryPath(110));
    }

    [Fact]
    public void Moving_a_file_updates_its_path()
    {
        FileIndexStore store = NewStore();

        store.Upsert(new IndexEntry(200, 100, Archive, "report.pdf"));

        Assert.Equal(@"C:\Users\report.pdf", store.ResolvePath(store.Entries.Single(e => e.FileId == 200)));
    }

    [Fact]
    public void Children_of_a_deleted_directory_become_orphans_and_stop_resolving()
    {
        FileIndexStore store = NewStore();

        store.Remove(110);

        Assert.True(store.TryGet(200, out IndexEntry orphan));
        Assert.True(orphan.IsFolder == false);
        Assert.Null(store.ResolvePath(orphan));
    }

    [Fact]
    public void Broken_parent_chain_resolves_to_null_instead_of_throwing()
    {
        var store = new FileIndexStore(@"C:\");
        store.Upsert(new IndexEntry(300, 999, Archive, "ghost.txt"));

        Assert.Null(store.ResolvePath(store.Entries.Single(e => e.FileId == 300)));
    }

    [Fact]
    public void Real_volume_file_references_carry_a_sequence_number()
    {
        // USN reports FRNs as (sequence << 48) | record number, so the root is 0x0005…0005, not 5.
        const ulong rootFrn = 0x0005_0000_0000_0005UL;
        const ulong windowsFrn = 0x0001_0000_0000_0064UL;
        const ulong notepadFrn = 0x0002_0000_0000_00C8UL;

        var store = new FileIndexStore(@"C:\");
        store.Upsert(new IndexEntry(rootFrn, rootFrn, Directory, "."));
        store.Upsert(new IndexEntry(windowsFrn, rootFrn, Directory, "Windows"));
        store.Upsert(new IndexEntry(notepadFrn, windowsFrn, Archive, "notepad.exe"));

        Assert.Equal(2, store.Count);
        Assert.Equal(1, store.FolderCount);
        Assert.Equal(@"C:\Windows\", store.ResolveDirectoryPath(windowsFrn));
        Assert.Equal(@"C:\Windows\notepad.exe", store.ResolvePath(store.Entries.Single(e => e.FileId == notepadFrn)));
        Assert.True(FileIndexStore.IsRoot(rootFrn));
        Assert.False(FileIndexStore.IsRoot(windowsFrn));
    }

    [Fact]
    public void Root_record_and_empty_names_are_never_indexed()
    {
        var store = new FileIndexStore(@"C:\");

        store.Upsert(new IndexEntry(FileIndexStore.RootFileId, 0, Directory, "."));
        store.Upsert(new IndexEntry(400, FileIndexStore.RootFileId, Archive, string.Empty));

        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Reset_drops_entries_and_cache()
    {
        FileIndexStore store = NewStore();
        _ = store.ResolveDirectoryPath(110);

        store.Reset();

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.FolderCount);
        Assert.Null(store.ResolveDirectoryPath(110));
    }
}
