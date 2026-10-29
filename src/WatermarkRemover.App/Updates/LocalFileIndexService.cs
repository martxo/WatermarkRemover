using System.IO;
using System.Text;
using System.Text.Json;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Hashes the installed files and remembers the result between checks. A cloud check compares the
/// remote manifest with this index; re-hashing ~330 MB on every check made the page feel frozen,
/// so a (size, last-write) stamp decides whether a file must be re-chunked.
/// </summary>
internal static class LocalFileIndexService
{
    /// <summary>Builds the index for the given relative paths, reusing the on-disk hash cache.</summary>
    public static LocalFileIndex Build(string appDir, IEnumerable<string> paths)
    {
        var cache = ReadCache();
        string[] wanted = paths.Select(LocalFileIndex.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var index = new LocalFileIndex();
        bool dirty = false;

        foreach (string relative in wanted)
        {
            string path = LocalFileIndex.ToLocalPath(appDir, relative);
            if (!File.Exists(path)) continue;

            var info = new FileInfo(path);
            if (cache.TryGetValue(relative, out CachedFile? cached) &&
                cached.Size == info.Length &&
                cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                index.Files[relative] = new LocalFileRecord(relative, cached.Size, cached.Sha256,
                    cached.Chunks.Select(c => new LocalChunkRef(relative, c.Offset, c.Length)).ToList());
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var chunks = ContentChunker.Split(bytes);
            index.Files[relative] = new LocalFileRecord(relative, bytes.Length, CloudHash.Sha256Hex(bytes),
                chunks.Select(c => new LocalChunkRef(relative, c.Offset, c.Length)).ToList());
            cache[relative] = new CachedFile
            {
                Size = bytes.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = index.Files[relative].Sha256,
                Chunks = chunks.Select(c => new CachedChunk(c.Sha256, c.Offset, c.Length)).ToList(),
            };
            dirty = true;
        }

        // Build the global chunk → location map once, from every file (cached or fresh).
        index.Chunks.Clear();
        foreach (LocalFileRecord record in index.Files.Values)
        {
            foreach (LocalChunkRef chunk in record.Chunks)
            {
                string sha = ChunkHashAt(record, chunk, appDir, cache);
                if (sha.Length > 0) index.Chunks[sha] = chunk;
            }
        }

        if (dirty) WriteCache(cache);
        return index;
    }

    private static string ChunkHashAt(
        LocalFileRecord record, LocalChunkRef chunk, string appDir, Dictionary<string, CachedFile> cache)
    {
        if (cache.TryGetValue(record.Path, out CachedFile? cached))
        {
            CachedChunk? match = cached.Chunks.FirstOrDefault(
                c => c.Offset == chunk.Offset && c.Length == chunk.Length);
            if (match is not null) return match.Sha256;
        }

        // Cache miss after a fresh scan is not expected; re-chunk the file to recover the hash.
        string path = LocalFileIndex.ToLocalPath(appDir, record.Path);
        byte[] bytes = File.ReadAllBytes(path);
        var chunks = ContentChunker.Split(bytes);
        CachedChunk? found = chunks.Select(c => new CachedChunk(c.Sha256, c.Offset, c.Length))
            .FirstOrDefault(c => c.Offset == chunk.Offset && c.Length == chunk.Length);
        return found?.Sha256 ?? string.Empty;
    }

    private static Dictionary<string, CachedFile> ReadCache()
    {
        try
        {
            if (!File.Exists(CloudUpdatePaths.LocalIndex))
            {
                return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
            }

            return JsonSerializer.Deserialize<Dictionary<string, CachedFile>>(
                       File.ReadAllText(CloudUpdatePaths.LocalIndex), CloudUpdateJson.Options)
                   ?? new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteCache(Dictionary<string, CachedFile> cache)
    {
        try
        {
            Directory.CreateDirectory(CloudUpdatePaths.State);
            File.WriteAllText(
                CloudUpdatePaths.LocalIndex,
                JsonSerializer.Serialize(cache, CloudUpdateJson.Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入本地文件索引缓存失败：{ex.Message}");
        }
    }

    private sealed class CachedFile
    {
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public List<CachedChunk> Chunks { get; set; } = new();
    }

    private sealed class CachedChunk
    {
        public CachedChunk()
        {
        }

        public CachedChunk(string sha256, int offset, int length)
        {
            Sha256 = sha256;
            Offset = offset;
            Length = length;
        }

        public string Sha256 { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int Length { get; set; }
    }
}
