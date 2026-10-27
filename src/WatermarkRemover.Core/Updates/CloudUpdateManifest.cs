using System.Text.Json;

namespace WatermarkRemover.Core.Updates;

/// <summary>Layout and schema constants of the 阿里云盘 update repository.</summary>
public static class CloudUpdateFormat
{
    /// <summary>Manifest schema; bumped when the on-disk JSON shape changes incompatibly.</summary>
    public const int Schema = 1;

    /// <summary>Folder inside the drive that holds the whole repository.</summary>
    public const string RootFolder = "WatermarkRemover";

    /// <summary>Content-addressed chunk blobs (file name = SHA256 hex of the chunk).</summary>
    public const string ChunksFolder = "files";

    /// <summary>One full manifest per retained version.</summary>
    public const string HistoryFolder = "history";

    /// <summary>Pointer to the newest manifest.</summary>
    public const string LatestFile = "latest.json";

    /// <summary>Version index: newest first, capped at <see cref="DefaultKeep"/> entries.</summary>
    public const string VersionsFile = "versions.json";

    /// <summary>How many versions the publisher keeps by default (cloud + local rollback).</summary>
    public const int DefaultKeep = 5;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parses a manifest/index; returns null for empty or malformed JSON.</summary>
    public static T? TryParse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>Serializes with the repository's stable JSON shape.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}

/// <summary>One chunk inside a file: content hash plus its byte length.</summary>
public sealed record CloudChunkRef(string Sha256, int Size);

/// <summary>One deployed file in a manifest, split into content-addressed chunks.</summary>
public sealed class CloudManifestFile
{
    /// <summary>Path relative to the install directory, always '/' separated.</summary>
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>SHA256 of the whole file (hex, lowercase) — verified after reassembly.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Ordered chunk list; concatenating them reproduces the file byte-for-byte.</summary>
    public List<CloudChunkRef> Chunks { get; set; } = new();

    public IEnumerable<string> ChunkHashes => Chunks.Select(c => c.Sha256);
}

/// <summary>
/// Full description of one released version. A client compares its own file hashes against this,
/// downloads only the chunks it does not already have, and applies the result in place.
/// </summary>
public sealed class CloudManifest
{
    public int Schema { get; set; } = CloudUpdateFormat.Schema;

    public string Version { get; set; } = string.Empty;

    /// <summary>ISO-8601 UTC publish time.</summary>
    public string PublishedAt { get; set; } = string.Empty;

    /// <summary>Human-readable release note shown in the version list.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Version this release was built on top of (null for the first one).</summary>
    public string? PreviousVersion { get; set; }

    /// <summary>Files present in the previous version but dropped here; the client removes them.</summary>
    public List<string> Deleted { get; set; } = new();

    public List<CloudManifestFile> Files { get; set; } = new();

    public long TotalBytes => Files.Sum(f => f.Size);

    public IEnumerable<string> AllChunkHashes() => Files.SelectMany(f => f.ChunkHashes).Distinct(StringComparer.Ordinal);

    /// <summary>Paths that disappeared between <paramref name="previous"/> and this manifest.</summary>
    public List<string> ComputeDeleted(CloudManifest? previous)
    {
        if (previous is null) return new List<string>();
        var current = Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return previous.Files
            .Select(f => f.Path)
            .Where(p => !current.Contains(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>One row of <c>versions.json</c>: what changed, without the full file list.</summary>
public sealed class CloudVersionSummary
{
    public string Version { get; set; } = string.Empty;

    public string PublishedAt { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public int FileCount { get; set; }

    /// <summary>Total size of the deployed payload (not the transfer size).</summary>
    public long TotalBytes { get; set; }

    /// <summary>Files added or modified relative to the previous version.</summary>
    public int ChangedFiles { get; set; }

    public int DeletedFiles { get; set; }

    /// <summary>New chunks uploaded for this release (the real incremental upload cost).</summary>
    public int NewChunks { get; set; }

    public long NewBytes { get; set; }
}

/// <summary>The retained-version index (newest first, capped by the publisher).</summary>
public sealed class CloudVersionIndex
{
    public int Schema { get; set; } = CloudUpdateFormat.Schema;

    public string UpdatedAt { get; set; } = string.Empty;

    public int Keep { get; set; } = CloudUpdateFormat.DefaultKeep;

    public List<CloudVersionSummary> Versions { get; set; } = new();
}
