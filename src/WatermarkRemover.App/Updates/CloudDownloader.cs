using System.Diagnostics;
using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// The cloud-update download hops, on their own: the signed-CDN call, the bundled aliyunpan CLI
/// fallback, and the timing that tells "CDN stalled" apart from "endpoint rejected". Extracted
/// from CloudUpdateService so the orchestration there stays readable and a future host (a CLI
/// updater, a test) can fetch manifests/chunks without constructing the whole service.
/// </summary>
internal static class CloudDownloader
{
    /// <summary>A manifest that has not arrived in this long is considered a broken CDN hop.</summary>
    private const int ManifestTimeoutSeconds = 25;

    /// <summary>The CLI is slower but deterministic; give one file two minutes at most.</summary>
    private const int CliTimeoutSeconds = 180;

    /// <summary>
    /// Downloads one cloud file through the API; when the signed CDN URL is unreachable or
    /// stalls, the bundled CLI (same stable login) fetches it instead.
    /// </summary>
    public static async Task<byte[]?> TryDownloadBytesAsync(
        INetdiskSession session,
        string? folderId,
        string name,
        string remotePath,
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(folderId))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(ManifestTimeoutSeconds));
            long apiTicks = Stopwatch.GetTimestamp();
            try
            {
                return await session.DownloadBytesAsync(folderId, name, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                AppLog.Warn($"云更新：接口下载 {name} 失败（耗时 {ElapsedMs(apiTicks)} ms），" +
                            $"改用 CLI：{ex.Message}");
            }
        }

        long cliTicks = Stopwatch.GetTimestamp();
        byte[]? bytes = await DownloadBytesViaCliAsync(remotePath, stateDirectory, cancellationToken)
            .ConfigureAwait(false);
        AppLog.Debug($"云更新：CLI 下载 {Path.GetFileName(remotePath)} 耗时 {ElapsedMs(cliTicks)} ms");
        return bytes;
    }

    /// <summary>Fetches one chunk by remote path through the CLI, moving it onto the target.</summary>
    public static async Task DownloadChunkViaCliAsync(
        string remotePath, string targetPath, string stateDirectory, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(stateDirectory, "cli-download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await RunCliDownloadAsync(remotePath, directory, cancellationToken).ConfigureAwait(false);
            string? downloaded = Directory
                .EnumerateFiles(directory, Path.GetFileName(remotePath), SearchOption.AllDirectories)
                .FirstOrDefault();
            if (downloaded is null)
            {
                throw new FileNotFoundException($"aliyunpan 没有下载出 {Path.GetFileName(remotePath)}。");
            }

            File.Move(downloaded, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task<byte[]?> DownloadBytesViaCliAsync(
        string remotePath, string stateDirectory, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(stateDirectory, "cli-download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await RunCliDownloadAsync(remotePath, directory, cancellationToken).ConfigureAwait(false);
            string? downloaded = Directory
                .EnumerateFiles(directory, Path.GetFileName(remotePath), SearchOption.AllDirectories)
                .FirstOrDefault();
            return downloaded is null
                ? null
                : await File.ReadAllBytesAsync(downloaded, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task RunCliDownloadAsync(
        string remotePath, string targetDirectory, CancellationToken cancellationToken)
    {
        string executable = AliyunpanCliRuntime.LocateExecutable()
            ?? throw new InvalidOperationException("未找到 aliyunpan 工具，无法备用下载云更新。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"download -ow \"{remotePath}\" \"{targetDirectory}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["ALIYUNPAN_CONFIG_DIR"] =
            Path.GetDirectoryName(AliyunpanCliRuntime.ConfigPathFor(executable))
            ?? AliyunpanCliRuntime.StableConfigDirectory;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("aliyunpan 启动失败。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(CliTimeoutSeconds));
        string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"aliyunpan 下载超时（{CliTimeoutSeconds} 秒）。");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"aliyunpan 下载失败：{error.Trim()} {output.Trim()}".Trim());
        }
    }

    public static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { /* best effort */ }
    }

    private static bool IsCallerCancellation(Exception ex, CancellationToken cancellationToken) =>
        ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static long ElapsedMs(long startTicks) =>
        (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
}
