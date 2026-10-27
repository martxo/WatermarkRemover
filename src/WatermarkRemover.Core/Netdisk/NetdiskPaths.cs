using System.IO;

namespace WatermarkRemover.Core.Netdisk;

/// <summary>
/// Path-level conveniences on top of the id-based <see cref="INetdiskSession"/> vocabulary.
/// The update repository is laid out by names ("/WatermarkRemover/files/&lt;sha&gt;") while the
/// API speaks folder ids, so every operation here resolves segments in order.
/// </summary>
public static class NetdiskPaths
{
    /// <summary>Finds one direct child by name; null when absent. <paramref name="isFolder"/> filters the kind.</summary>
    public static async Task<NetdiskFile?> FindChildAsync(
        this INetdiskSession session,
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default,
        bool? isFolder = null)
    {
        string? cursor = null;
        do
        {
            NetdiskPage page = await session.ListAsync(parentFolderId, 200, cursor, cancellationToken)
                .ConfigureAwait(false);
            NetdiskFile? match = page.Items.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (isFolder is null || item.IsFolder == isFolder));
            if (match is not null) return match;
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return null;
    }

    /// <summary>Every entry of one folder, across all pages.</summary>
    public static async Task<List<NetdiskFile>> ListAllAsync(
        this INetdiskSession session,
        string parentFolderId,
        CancellationToken cancellationToken = default)
    {
        var items = new List<NetdiskFile>();
        string? cursor = null;
        do
        {
            NetdiskPage page = await session.ListAsync(parentFolderId, 200, cursor, cancellationToken)
                .ConfigureAwait(false);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return items;
    }

    /// <summary>Resolves "/a/b" to a folder id, creating missing segments. Empty path = drive root.</summary>
    public static async Task<string> EnsureFolderAsync(
        this INetdiskSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        string current = string.Empty;
        foreach (string segment in Split(path))
        {
            NetdiskFile? existing = await session.FindChildAsync(current, segment, cancellationToken, isFolder: true)
                .ConfigureAwait(false);
            current = existing?.Id
                ?? (await session.CreateFolderAsync(current, segment, cancellationToken).ConfigureAwait(false)).Id;
        }

        return current;
    }

    /// <summary>Resolves "/a/b" to a folder id; null when any segment is missing.</summary>
    public static async Task<string?> FindFolderAsync(
        this INetdiskSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        string current = string.Empty;
        foreach (string segment in Split(path))
        {
            NetdiskFile? existing = await session.FindChildAsync(current, segment, cancellationToken, isFolder: true)
                .ConfigureAwait(false);
            if (existing is null) return null;
            current = existing.Id;
        }

        return current;
    }

    /// <summary>Downloads one child by name into memory; null when it does not exist.</summary>
    public static async Task<byte[]?> DownloadBytesAsync(
        this INetdiskSession session,
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        NetdiskFile? file = await session.FindChildAsync(parentFolderId, name, cancellationToken, isFolder: false)
            .ConfigureAwait(false);
        if (file is null) return null;

        string temp = Path.Combine(Path.GetTempPath(), $"wr-cloud-{Guid.NewGuid():N}");
        try
        {
            await session.DownloadAsync(file.Id, temp, null, cancellationToken).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>Uploads in-memory bytes under an explicit fixed name (overwriting).</summary>
    public static async Task UploadBytesAsync(
        this INetdiskSession session,
        string parentFolderId,
        string name,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"wr-cloud-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string temp = Path.Combine(tempDir, name);
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            await session.UploadAsAsync(parentFolderId, temp, name, overwrite: true, null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Deletes a direct child by name; false when it was not there.</summary>
    public static async Task<bool> DeleteByNameAsync(
        this INetdiskSession session,
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        NetdiskFile? file = await session.FindChildAsync(parentFolderId, name, cancellationToken)
            .ConfigureAwait(false);
        if (file is null) return false;

        await session.DeleteAsync(new[] { file.Id }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static IEnumerable<string> Split(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
