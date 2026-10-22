using WatermarkRemover.Index.Contracts;
using WatermarkRemover.Index.Ntfs;

namespace WatermarkRemover.Index.Indexing;

/// <summary>
/// The privileged half of the design: it owns every volume index and answers the pipe client.
/// The UI process never touches a volume handle, so it can stay un-elevated forever.
/// </summary>
public sealed class IndexHostService
{
    private const int MaxResultsCeiling = 1000;

    private readonly string _stateDirectory;
    private readonly List<VolumeIndexer> _volumes = new();

    public IndexHostService(string stateDirectory)
    {
        _stateDirectory = stateDirectory;
        Directory.CreateDirectory(stateDirectory);
    }

    public void StartAll(CancellationToken cancellationToken)
    {
        foreach (string root in VolumeCatalog.GetFixedNtfsRoots())
        {
            var indexer = new VolumeIndexer(root, _stateDirectory);
            _volumes.Add(indexer);
            indexer.Start(cancellationToken);
        }
    }

    public IndexResponse Handle(IndexRequest request)
    {
        try
        {
            return request.Cmd switch
            {
                "status" => new IndexResponse { Ok = true, Status = GetStatus() },
                "search" => new IndexResponse
                {
                    Ok = true,
                    Results = Search(request.Query, request.Max),
                },
                "shutdown" => new IndexResponse { Ok = true },
                _ => IndexResponse.Failure($"未知命令：{request.Cmd}"),
            };
        }
        catch (Exception ex)
        {
            return IndexResponse.Failure(ex.Message);
        }
    }

    public IndexStatusDto GetStatus()
    {
        string[] volumes = _volumes.Select(v => v.RootPath).ToArray();
        string? error = _volumes.Select(v => v.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e));

        if (_volumes.Count == 0)
            error ??= "没有可索引的本机 NTFS 固定磁盘";

        bool scanning = _volumes.Any(v => v.IsScanning);
        bool ready = _volumes.Count > 0 && _volumes.Any(v => v.IsReady);

        return new IndexStatusDto(
            ready,
            scanning,
            _volumes.Sum(v => Math.Max(0, v.Store.Count - v.Store.FolderCount)),
            _volumes.Sum(v => v.Store.FolderCount),
            volumes,
            error);
    }

    public IReadOnlyList<IndexHitDto> Search(string? query, int maxResults)
    {
        SearchPattern? pattern = SearchPattern.Parse(query);
        if (pattern is null) return Array.Empty<IndexHitDto>();

        int limit = Math.Clamp(maxResults, 1, MaxResultsCeiling);

        var matches = new List<IndexMatch>();
        foreach (VolumeIndexer volume in _volumes)
            matches.AddRange(IndexQuery.Search(volume.Store, pattern, limit));

        List<IndexMatch> ordered = matches
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Entry.Name.Length)
            .ThenBy(m => m.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        var hits = new List<IndexHitDto>(ordered.Count);
        foreach (IndexMatch match in ordered)
        {
            (long size, DateTime? modified) = ReadMetadata(match.FullPath, match.Entry.IsFolder);
            hits.Add(new IndexHitDto(match.FullPath, match.Entry.IsFolder, size, modified));
        }

        return hits;
    }

    private static (long Size, DateTime? Modified) ReadMetadata(string path, bool isFolder)
    {
        try
        {
            if (isFolder)
                return (0, Directory.Exists(path) ? Directory.GetLastWriteTime(path) : null);

            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTime) : (0, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A hit deleted between the query and this read simply reports no metadata.
            return (0, null);
        }
    }
}
