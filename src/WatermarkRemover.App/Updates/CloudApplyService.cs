using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// The "apply after exit" half of the cloud update: writes a plan + a PowerShell script, lets the
/// script replace files while the backup is kept, and folds the verdict back into the install
/// history on the next start. Kept apart from the download/check flow so the script contract and
/// its rollback rules can be read (and reasoned about) on their own.
/// </summary>
internal static class CloudApplyService
{
    /// <summary>
    /// Schedules the staged files to replace the install after this process exits. The caller
    /// shuts the application down right after this returns; the script relaunches it.
    /// </summary>
    public static void ApplyAndRestart(CloudStageResult staged, string direction, string note)
    {
        if (string.IsNullOrEmpty(staged.StagingDirectory) || !Directory.Exists(staged.StagingDirectory))
        {
            throw new InvalidOperationException("暂存目录不存在，无法应用更新。");
        }

        string appDir = AppDirectory();
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        string guid = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(CloudUpdatePaths.State);
        string backup = Path.Combine(CloudUpdatePaths.Backup, guid);
        Directory.CreateDirectory(backup);

        var plan = new CloudApplyPlan
        {
            AppDir = appDir,
            StagingDir = staged.StagingDirectory,
            BackupDir = backup,
            ResultPath = CloudUpdatePaths.Result,
            ExePath = exe,
            FromVersion = staged.Check.Current.ToString(),
            ToVersion = staged.Check.Latest?.ToString() ?? staged.Check.Manifest?.Version ?? string.Empty,
            Direction = direction,
            Note = note,
            WaitPid = Environment.ProcessId,
            Files = staged.Files.ToList(),
            Deletes = staged.Deletes.ToList(),
        };

        string planPath = Path.Combine(CloudUpdatePaths.State, $"apply-{guid}.json");
        File.WriteAllText(
            planPath, JsonSerializer.Serialize(plan, CloudUpdateJson.Options), new UTF8Encoding(false));

        AppendHistory(new CloudUpdateHistoryEntry
        {
            At = DateTimeOffset.Now.ToString("O"),
            FromVersion = plan.FromVersion,
            ToVersion = plan.ToVersion,
            Direction = direction,
            Note = note,
            Status = "应用中",
            FileCount = plan.Files.Count,
            Bytes = staged.DownloadedBytes,
        });

        string scriptPath = Path.Combine(CloudUpdatePaths.State, $"apply-{guid}.ps1");
        File.WriteAllText(scriptPath, BuildApplyScript(), new UTF8Encoding(true));

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -PlanPath \"{planPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("无法启动更新脚本。");
        }

        AppLog.Info($"云更新已交给后台脚本：{plan.FromVersion} → {plan.ToVersion}（{direction}），" +
                    $"{plan.Files.Count} 个文件、下载 {CloudUpdateService.FormatBytes(staged.DownloadedBytes)}。");
    }

    /// <summary>
    /// Called once at startup: folds the background script's result into the install history,
    /// deletes the consumed result file and prunes old staging/backup folders.
    /// </summary>
    public static CloudApplyResult? ReconcileLastApply()
    {
        CloudApplyResult? result = null;
        try
        {
            if (File.Exists(CloudUpdatePaths.Result))
            {
                result = JsonSerializer.Deserialize<CloudApplyResult>(
                    File.ReadAllText(CloudUpdatePaths.Result), CloudUpdateJson.Options);
                File.Delete(CloudUpdatePaths.Result);
            }

            if (result is not null)
            {
                var history = ReadHistory().ToList();
                var entry = history.LastOrDefault(e => e.Status == "应用中");
                if (entry is not null)
                {
                    entry.Status = result.Success ? "成功" : "已还原";
                    entry.Message = result.Message;
                    WriteHistory(history);
                }

                AppLog.Info(result.Success
                    ? $"云更新完成：v{result.FromVersion} → v{result.ToVersion}"
                    : $"云更新失败，已还原 v{result.FromVersion}：{result.Message}");
            }

            CleanupStagingAndBackups(result?.Success == true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取云更新结果失败：{ex.Message}");
        }

        return result;
    }

    public static IReadOnlyList<CloudUpdateHistoryEntry> ReadHistory()
    {
        try
        {
            if (!File.Exists(CloudUpdatePaths.History)) return Array.Empty<CloudUpdateHistoryEntry>();
            return JsonSerializer.Deserialize<List<CloudUpdateHistoryEntry>>(
                       File.ReadAllText(CloudUpdatePaths.History), CloudUpdateJson.Options)
                   ?? new List<CloudUpdateHistoryEntry>();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取云更新记录失败：{ex.Message}");
            return Array.Empty<CloudUpdateHistoryEntry>();
        }
    }

    private static void AppendHistory(CloudUpdateHistoryEntry entry)
    {
        var history = ReadHistory().ToList();
        history.Add(entry);
        if (history.Count > 30) history = history.Skip(history.Count - 30).ToList();
        WriteHistory(history);
    }

    private static void WriteHistory(IEnumerable<CloudUpdateHistoryEntry> history)
    {
        Directory.CreateDirectory(CloudUpdatePaths.State);
        File.WriteAllText(
            CloudUpdatePaths.History, JsonSerializer.Serialize(history, CloudUpdateJson.Options),
            new UTF8Encoding(false));
    }

    private static void CleanupStagingAndBackups(bool applySucceeded)
    {
        try
        {
            if (Directory.Exists(CloudUpdatePaths.Staging))
            {
                foreach (string dir in Directory.GetDirectories(CloudUpdatePaths.Staging))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* in use / best effort */ }
                }
            }

            if (!applySucceeded || !Directory.Exists(CloudUpdatePaths.Backup)) return;
            foreach (string dir in Directory.GetDirectories(CloudUpdatePaths.Backup)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc)
                         .Skip(CloudUpdateFormat.DefaultKeep))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清理云更新缓存失败：{ex.Message}");
        }
    }

    /// <summary>The install folder the files are replaced inside (one level above the exe).</summary>
    internal static string AppDirectory()
    {
        string exe = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "WatermarkRemover.exe");
        return Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
    }

    private static string BuildApplyScript() => """
        param([string]$PlanPath)
        $ErrorActionPreference = 'Stop'
        $plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
        function Copy-WithRetry([string]$Source, [string]$Target) {
            for ($i = 0; $i -lt 6; $i++) {
                try {
                    Copy-Item -LiteralPath $Source -Destination $Target -Force
                    return
                } catch {
                    if ($i -eq 5) { throw }
                    Start-Sleep -Milliseconds 500
                }
            }
        }
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-Process -Id $plan.waitPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
            Start-Sleep -Milliseconds 400
        }
        Start-Sleep -Milliseconds 400

        New-Item -ItemType Directory -Force -Path $plan.backupDir | Out-Null
        $ok = $true
        $msg = ''
        try {
            foreach ($rel in $plan.files) {
                $target = Join-Path $plan.appDir $rel
                if (Test-Path -LiteralPath $target) {
                    $bak = Join-Path $plan.backupDir $rel
                    New-Item -ItemType Directory -Force -Path (Split-Path $bak -Parent) | Out-Null
                    Copy-WithRetry $target $bak
                }
                $src = Join-Path $plan.stagingDir $rel
                New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
                Copy-WithRetry $src $target
            }
            foreach ($rel in $plan.deletes) {
                $target = Join-Path $plan.appDir $rel
                if (Test-Path -LiteralPath $target) {
                    $bak = Join-Path $plan.backupDir $rel
                    New-Item -ItemType Directory -Force -Path (Split-Path $bak -Parent) | Out-Null
                    Copy-WithRetry $target $bak
                    Remove-Item -LiteralPath $target -Force
                }
            }

            # Keep Velopack's install metadata in step with the files we just replaced; otherwise
            # the next startup sees sq.version older than the running assembly.
            $sq = Join-Path $plan.appDir 'sq.version'
            if (Test-Path -LiteralPath $sq) {
                Copy-WithRetry $sq (Join-Path $plan.backupDir 'sq.version')
                $xml = Get-Content -LiteralPath $sq -Raw
                $xml = [regex]::Replace($xml, '<version>[^<]+</version>', "<version>$($plan.toVersion)</version>")
                [IO.File]::WriteAllText($sq, $xml, [Text.UTF8Encoding]::new($false))
            }
        } catch {
            $ok = $false
            $msg = $_.Exception.Message
            try {
                foreach ($rel in $plan.files) {
                    $target = Join-Path $plan.appDir $rel
                    $bak = Join-Path $plan.backupDir $rel
                    if (Test-Path -LiteralPath $bak) {
                        Copy-Item -LiteralPath $bak -Destination $target -Force
                    } else {
                        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
                    }
                }
                foreach ($rel in $plan.deletes) {
                    $bak = Join-Path $plan.backupDir $rel
                    if (Test-Path -LiteralPath $bak) {
                        Copy-Item -LiteralPath $bak -Destination (Join-Path $plan.appDir $rel) -Force
                    }
                }
                $bakSq = Join-Path $plan.backupDir 'sq.version'
                if (Test-Path -LiteralPath $bakSq) {
                    Copy-Item -LiteralPath $bakSq -Destination (Join-Path $plan.appDir 'sq.version') -Force
                }
            } catch { }
        }

        $result = @{
            success = $ok
            message = $msg
            toVersion = $plan.toVersion
            fromVersion = $plan.fromVersion
            finishedAt = (Get-Date).ToString('O')
        } | ConvertTo-Json
        [IO.File]::WriteAllText($plan.resultPath, $result, [Text.UTF8Encoding]::new($false))
        Remove-Item -LiteralPath $plan.stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        Start-Process -FilePath $plan.exePath
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}

internal sealed class CloudApplyPlan
{
    public string AppDir { get; set; } = string.Empty;
    public string StagingDir { get; set; } = string.Empty;
    public string BackupDir { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string Direction { get; set; } = "update";
    public string Note { get; set; } = string.Empty;
    public int WaitPid { get; set; }
    public List<string> Files { get; set; } = new();
    public List<string> Deletes { get; set; } = new();
}
