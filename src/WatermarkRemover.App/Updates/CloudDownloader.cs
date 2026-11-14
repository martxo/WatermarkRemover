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
    /// <summary>
    /// A manifest that has not arrived in this long is considered a broken CDN hop. Evidence
    /// (app-20261103~08 logs): every hung API hop burned the FULL timeout while the CLI fetched
    /// the same file in ~4 s, so each check paid timeout + CLI every time; 8 s still leaves 2×
    /// headroom over the worst observed CLI fetch for these few-KB files.
    /// </summary>
    private const int ManifestTimeoutSeconds = 8;

    /// <summary>The CLI is slower but deterministic; give one file two minutes at most.</summary>
    private const int CliTimeoutSeconds = 180;

    /// <summary>
    /// aliyunpan keeps its login and refresh token in ONE config directory and rewrites it on
    /// startup. Parallel fallbacks therefore raced on that file: one process lost its token
    /// mid-download, exited 0 without writing anything, and the update died with
    /// "aliyunpan 没有下载出 …" after ~46 s (the "first attempt always fails" reports). One CLI
    /// hop at a time keeps that config single-writer; API downloads stay parallel.
    /// </summary>
    private static readonly SemaphoreSlim s_cliGate = new(1, 1);

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
            // One retry used to be enough to make the CLI the common path: a 403
            // "ExceedMaxConcurrency" on the first try sent a 5 KB manifest through a process
            // spawn, measured at 55 s in app-20261112.log. Three tries with a fresh link cost
            // ~1 s on a throttle and keep the CLI for a genuinely broken API hop.
            const int apiAttempts = 3;
            for (int attempt = 1; attempt <= apiAttempts; attempt++)
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
                    AppLog.Warn($"云更新：接口下载 {name} 第 {attempt}/{apiAttempts} 次失败" +
                                $"（耗时 {ElapsedMs(apiTicks)} ms）：{ex.Message}");
                    if (attempt == apiAttempts) break;
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        long cliTicks = Stopwatch.GetTimestamp();
        byte[]? bytes = await DownloadBytesViaCliAsync(remotePath, stateDirectory, cancellationToken)
            .ConfigureAwait(false);
        AppLog.Debug($"云更新：CLI 下载 {Path.GetFileName(remotePath)} 耗时 {ElapsedMs(cliTicks)} ms");
        return bytes;
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

    /// <summary>Runs one CLI download and returns its combined console output for diagnostics.</summary>
    private static async Task<string> RunCliDownloadAsync(
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

        await s_cliGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            return $"{error.Trim()} {output.Trim()}".Trim();
        }
        finally
        {
            s_cliGate.Release();
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
