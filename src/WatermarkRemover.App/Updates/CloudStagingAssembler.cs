using System.IO;
using System.Security.Cryptography;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Rebuilds one changed file in the staging tree from cached chunks and ranges of local files,
/// verifying size and SHA-256 before anything is used. Extracted from CloudUpdateService: the
/// assembly rules are self-contained and were the part most worth reading on their own.
/// </summary>
internal static class CloudStagingAssembler
{
    /// <summary>
    /// Writes <paramref name="file"/> to <paramref name="target"/>: each chunk comes from the
    /// download cache when present, otherwise from a byte range of the installed file.
    /// </summary>
    public static void AssembleFile(
        CloudManifestFile file,
        string appDir,
        LocalFileIndex index,
        string chunkCacheDirectory,
        string target)
    {
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long written = 0;

        foreach (CloudChunkRef chunk in file.Chunks)
        {
            string cachePath = Path.Combine(chunkCacheDirectory, chunk.Sha256);
            if (File.Exists(cachePath))
            {
                written += CopyRange(cachePath, 0, chunk.Size, output, hash, buffer);
                continue;
            }

            if (!index.Chunks.TryGetValue(chunk.Sha256, out LocalChunkRef? local))
            {
                throw new InvalidDataException($"本地与缓存都没有分块 {chunk.Sha256[..12]}…，请重新检查更新。");
            }

            written += CopyRange(
                LocalFileIndex.ToLocalPath(appDir, local.Path), local.Offset, chunk.Size, output, hash, buffer);
        }

        output.Flush();
        if (written != file.Size ||
            !string.Equals(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), file.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"文件 {file.Path} 组装校验失败，已放弃本次更新。");
        }
    }

    private static long CopyRange(
        string path, int offset, int length, Stream output, IncrementalHash hash, byte[] buffer)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        source.Seek(offset, SeekOrigin.Begin);
        long copied = 0;
        while (copied < length)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length - copied));
            if (read == 0) throw new IOException($"读取 {path} 时提前结束。");
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            copied += read;
        }

        return copied;
    }
}
