namespace WatermarkRemover.Core.Netdisk;

/// <summary>A file or folder entry in a netdisk listing.</summary>
public sealed record NetdiskFile(
    string Id,
    string Name,
    bool IsFolder,
    long Size,
    DateTimeOffset? ModifiedAt,
    string? ParentId = null);

/// <summary>One page of a folder listing; <see cref="NextCursor"/> null means the last page.</summary>
public sealed record NetdiskPage(IReadOnlyList<NetdiskFile> Items, string? NextCursor);

/// <summary>Storage quota for the connected account.</summary>
public sealed record NetdiskQuota(long UsedBytes, long TotalBytes);

/// <summary>Identity of the connected account.</summary>
public sealed record NetdiskAccountInfo(string UserId, string DisplayName);

/// <summary>Transfer progress reported during upload/download.</summary>
public sealed record NetdiskTransferProgress(long BytesTransferred, long? TotalBytes);
