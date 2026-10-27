using System.IO;
using System.Security.Cryptography;

namespace WatermarkRemover.Core.Updates;

/// <summary>Where one chunk already exists locally: a file path plus the byte range inside it.</summary>
public sealed record LocalChunkRef(string Path, int Offset, int Length);

/// <summary>A local file's hash and chunk layout, used to reuse bytes during an update.</summary>
public sealed record LocalFileRecord(string Path, long Size, string Sha256, IReadOnlyList<LocalChunkRef> Chunks);

/// <summary>
/// Snapshot of the files a plan may touch. Every file is chunked with the same algorithm the
/// publisher used, so a locally present chunk can be sliced out instead of downloaded.
/// </summary>
public sealed class LocalFileIndex
{
    public Dictionary<string, LocalFileRecord> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, LocalChunkRef> Chunks { get; } = new(StringComparer.Ordinal);

    public static LocalFileIndex Scan(string root, IEnumerable<string> relativePaths)
    {
        var index = new LocalFileIndex();
        foreach (string relative in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = ToLocalPath(root, relative);
            if (!File.Exists(path)) continue;

            byte[] bytes = File.ReadAllBytes(path);
            var chunks = ContentChunker.Split(bytes);
            string normalized = Normalize(relative);
            index.Files[normalized] = new LocalFileRecord(
                normalized, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                chunks.Select(c => new LocalChunkRef(normalized, c.Offset, c.Length)).ToList());

            foreach (ContentChunk chunk in chunks)
            {
                index.Chunks[chunk.Sha256] = new LocalChunkRef(normalized, chunk.Offset, chunk.Length);
            }
        }

        return index;
    }

    public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public static string ToLocalPath(string root, string relative) =>
        Path.Combine(root, Normalize(relative).Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// What one client must transfer to reach a manifest: changed files, removed files and the exact
/// chunks it does not already have on disk.
/// </summary>
public sealed record CloudUpdatePlan(
    string FromVersion,
    string ToVersion,
    IReadOnlyList<CloudManifestFile> ChangedFiles,
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<CloudChunkRef> RequiredChunks,
    long DownloadBytes)
{
    public long ChangedBytes => ChangedFiles.Sum(f => f.Size);

    public bool HasChanges => ChangedFiles.Count > 0 || DeletedFiles.Count > 0;

    /// <summary>One human-readable line per changed file, for the update details panel.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = ChangedFiles
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"+ {f.Path}  {FormatBytes(f.Size)}")
            .ToList();
        lines.AddRange(DeletedFiles.Select(p => $"- {p}  （删除）"));
        return lines;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>Pure planning logic shared by the publisher and the client (no network, no UI).</summary>
public static class CloudUpdatePlanner
{
    /// <summary>
    /// Compares <paramref name="remote"/> against the local snapshot. Only files whose whole-file
    /// hash differs are touched; only chunks missing from the local snapshot are downloaded.
    /// </summary>
    public static CloudUpdatePlan CreatePlan(
        string fromVersion,
        CloudManifest remote,
        LocalFileIndex local)
    {
        var changed = new List<CloudManifestFile>();
        var required = new Dictionary<string, CloudChunkRef>(StringComparer.Ordinal);

        foreach (CloudManifestFile file in remote.Files)
        {
            string path = LocalFileIndex.Normalize(file.Path);
            if (local.Files.TryGetValue(path, out LocalFileRecord? existing) &&
                string.Equals(existing.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            changed.Add(file);
            foreach (CloudChunkRef chunk in file.Chunks)
            {
                if (!local.Chunks.ContainsKey(chunk.Sha256))
                {
                    required[chunk.Sha256] = chunk;
                }
            }
        }

        var deleted = remote.Deleted
            .Select(LocalFileIndex.Normalize)
            .Where(p => local.Files.ContainsKey(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CloudUpdatePlan(
            fromVersion,
            remote.Version,
            changed,
            deleted,
            required.Values.OrderBy(c => c.Sha256, StringComparer.Ordinal).ToList(),
            required.Values.Sum(c => (long)c.Size));
    }

    /// <summary>
    /// Builds a full manifest from a published output directory. Debug symbols are excluded by
    /// default: they are never loaded at runtime and would otherwise dominate every upload.
    /// </summary>
    public static CloudManifest BuildManifest(
        string root,
        string version,
        string note,
        CloudManifest? previous,
        Func<string, bool>? include = null,
        IDictionary<string, (string Path, int Offset, int Length)>? chunkSources = null)
    {
        include ??= DefaultInclude;
        var files = new List<CloudManifestFile>();

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = LocalFileIndex.Normalize(Path.GetRelativePath(root, path));
            if (!include(relative)) continue;

            byte[] bytes = File.ReadAllBytes(path);
            var chunks = ContentChunker.Split(bytes);
            files.Add(new CloudManifestFile
            {
                Path = relative,
                Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Chunks = chunks.Select(c => new CloudChunkRef(c.Sha256, c.Length)).ToList(),
            });

            if (chunkSources is not null)
            {
                foreach (ContentChunk chunk in chunks)
                {
                    chunkSources.TryAdd(chunk.Sha256, (path, chunk.Offset, chunk.Length));
                }
            }
        }

        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        var manifest = new CloudManifest
        {
            Version = version,
            PublishedAt = DateTimeOffset.UtcNow.ToString("O"),
            Note = note,
            PreviousVersion = previous?.Version,
            Files = files,
        };
        manifest.Deleted = manifest.ComputeDeleted(previous);
        return manifest;
    }

    /// <summary>Publish filter: everything except symbol files and OS clutter.</summary>
    public static bool DefaultInclude(string relativePath)
    {
        if (relativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) return false;
        if (relativePath.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)) return false;
        string name = System.IO.Path.GetFileName(relativePath);
        return !name.StartsWith(".", StringComparison.Ordinal)
               && !name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }
}
