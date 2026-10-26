using System.IO;
using System.Text.Json;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Updates;

/// <summary>One automatic apply attempt, persisted so the next launch knows it already tried.</summary>
internal sealed record UpdateApplyAttempt(
    string TargetVersion,
    string RunningVersion,
    string ExePath,
    DateTime AttemptedAtUtc);

/// <summary>
/// Guards the startup auto-apply of an already-downloaded update. Velopack swaps the app through
/// Update.exe; when that fails it relaunches the old build, which would immediately see the same
/// pending package and try again — an endless restart loop. One attempt per target version per
/// cooldown, then the user gets a plain explanation instead of a loop. Any file problem allows the
/// attempt: an update must never be blocked by its own guard file.
/// </summary>
internal static class UpdateApplyGuard
{
    private const string FileName = "apply-guard.json";

    /// <summary>Within this window a repeated automatic attempt for the same version is refused.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    /// <summary>True when this version may be applied now; records the attempt when it is.</summary>
    public static bool ShouldAttempt(
        string directory, string targetVersion, string runningVersion, string exePath, DateTime nowUtc)
    {
        try
        {
            UpdateApplyAttempt? last = Read(directory);
            if (last is not null && last.TargetVersion == targetVersion
                && nowUtc - last.AttemptedAtUtc < Cooldown)
            {
                AppLog.Warn(
                    $"自动应用 v{targetVersion} 在冷却期内已尝试过（当时运行 v{last.RunningVersion}，" +
                    $"位置 {last.ExePath}），本次不再重试，避免反复重启");
                return false;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, FileName), JsonSerializer.Serialize(
                new UpdateApplyAttempt(targetVersion, runningVersion, exePath, nowUtc)));
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"更新应用守卫读写失败（{ex.Message}），仍继续尝试");
            return true;
        }
    }

    /// <summary>The last recorded attempt, used by the explanation shown when retries stop.</summary>
    public static UpdateApplyAttempt? Last(string directory)
    {
        try
        {
            return Read(directory);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取更新应用守卫失败：{ex.Message}");
            return null;
        }
    }

    private static UpdateApplyAttempt? Read(string directory)
    {
        string path = Path.Combine(directory, FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<UpdateApplyAttempt>(File.ReadAllText(path))
            : null;
    }
}
