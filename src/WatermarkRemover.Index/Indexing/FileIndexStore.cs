using System.Collections.Concurrent;
using System.Text;

namespace WatermarkRemover.Index.Indexing;

/// <summary>One indexed item. FileId/ParentFileId are NTFS file reference numbers.</summary>
public readonly record struct IndexEntry(ulong FileId, ulong ParentFileId, uint Attributes, string Name)
{
    public bool IsFolder => (Attributes & 0x10) != 0;
}

/// <summary>
/// The in-memory name index — Everything's "db". Keyed by file reference number so USN records
/// (which only carry FRNs) can be applied without touching the directory tree.
///
/// Paths are NOT stored. A hit is resolved by walking ParentFileId up to the volume root, with a
/// cache for directory prefixes: on a warm cache that is one dictionary lookup per result.
///
/// Reads and writes use ConcurrentDictionary, so the USN monitor never blocks a query.
/// </summary>
public sealed class FileIndexStore
{
    /// <summary>MFT record number of the root directory.</summary>
    public const ulong RootFileId = 5;

    /// <summary>
    /// A USN/USN journal file reference number is (sequence &lt;&lt; 48) | record number. Only the
    /// record number identifies the tree node; the high bits change when an FRN is reused. Real
    /// volumes therefore report the root as e.g. 0x0005000000000005, not 5 — comparing the full
    /// 64-bit value against <see cref="RootFileId"/> silently orphans the whole volume.
    /// </summary>
    public const ulong RecordNumberMask = 0x0000_FFFF_FFFF_FFFF;

    private const int MaxDepth = 256;

    private readonly ConcurrentDictionary<ulong, IndexEntry> _entries = new();
    private readonly ConcurrentDictionary<ulong, string> _directoryPaths = new();
    private int _folderCount;

    public FileIndexStore(string rootPath)
    {
        RootPath = NormalizeRoot(rootPath);
    }

    /// <summary>Always ends with a backslash, e.g. <c>C:\</c>.</summary>
    public string RootPath { get; }

    public static ulong RecordNumber(ulong fileId) => fileId & RecordNumberMask;

    public static bool IsRoot(ulong fileId) => RecordNumber(fileId) == RootFileId;

    public int Count => _entries.Count;

    public int FolderCount => Volatile.Read(ref _folderCount);

    public IEnumerable<IndexEntry> Entries => _entries.Values;

    public void Upsert(IndexEntry entry)
    {
        if (IsRoot(entry.FileId) || entry.Name.Length == 0) return;

        bool existed = _entries.TryGetValue(entry.FileId, out IndexEntry previous);
        _entries[entry.FileId] = entry;

        if (entry.IsFolder && (!existed || !previous.IsFolder))
            Interlocked.Increment(ref _folderCount);
        else if (!entry.IsFolder && existed && previous.IsFolder)
        {
            Interlocked.Decrement(ref _folderCount);
            _directoryPaths.Clear();
        }

        // A renamed/moved directory invalidates every prefix built through it. New entries cannot
        // invalidate anything (nothing could have resolved through a directory that did not exist).
        if (existed && (previous.Name != entry.Name || previous.ParentFileId != entry.ParentFileId))
            _directoryPaths.Clear();
    }

    public bool Remove(ulong fileId)
    {
        if (IsRoot(fileId)) return false;
        if (!_entries.TryRemove(fileId, out IndexEntry removed)) return false;

        if (removed.IsFolder)
        {
            Interlocked.Decrement(ref _folderCount);
            _directoryPaths.Clear();
        }

        return true;
    }

    public bool TryGet(ulong fileId, out IndexEntry entry) => _entries.TryGetValue(fileId, out entry);

    /// <summary>Full path of an entry, or null when it is an orphan (its parent chain is broken).</summary>
    public string? ResolvePath(in IndexEntry entry)
    {
        if (IsRoot(entry.FileId)) return RootPath;

        string? parent = ResolveDirectoryPath(entry.ParentFileId);
        if (parent is null) return null;

        return entry.IsFolder ? parent + entry.Name + "\\" : parent + entry.Name;
    }

    /// <summary>Directory prefix ending in a backslash, or null for an orphan/unresolvable chain.</summary>
    public string? ResolveDirectoryPath(ulong fileId)
    {
        if (fileId == 0 || IsRoot(fileId)) return RootPath;
        if (_directoryPaths.TryGetValue(fileId, out string? cached)) return cached;

        var chain = new List<(ulong Id, string Name)>(8);
        ulong current = fileId;

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (IsRoot(current)) break;

            if (!_entries.TryGetValue(current, out IndexEntry entry) || !entry.IsFolder) return null;
            chain.Add((current, entry.Name));
            current = entry.ParentFileId;

            if (current == 0) return null;
        }

        if (!IsRoot(current)) return null;

        var builder = new StringBuilder(RootPath);
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            builder.Append(chain[i].Name).Append('\\');
            _directoryPaths.TryAdd(chain[i].Id, builder.ToString());
        }

        return builder.ToString();
    }

    public void Reset()
    {
        _entries.Clear();
        _directoryPaths.Clear();
        Interlocked.Exchange(ref _folderCount, 0);
    }

    public void Load(IEnumerable<IndexEntry> entries)
    {
        Reset();
        foreach (IndexEntry entry in entries) Upsert(entry);
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("卷根路径不能为空。", nameof(rootPath));
        return rootPath.EndsWith('\\') ? rootPath : rootPath + '\\';
    }
}
