using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Renames a finished job's output on disk. Name rules come from <see cref="OutputNameEditor"/>;
/// a name collision is resolved by counting up, never by overwriting. The move retries through a
/// short IO backoff because the background naming chain holds the file open in milliseconds-wide
/// pdfium render passes while it OCRs.
/// </summary>
public sealed class JobRenamer
{
    private readonly IDialogService _dialogs;

    public JobRenamer(IDialogService dialogs) =>
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    /// <summary>Applies the user's rename to the job's output file.</summary>
    public void Rename(FileJobViewModel job, string desiredName)
    {
        if (job.OutputPath is null) return;

        string currentName = Path.GetFileName(job.OutputPath);
        string? newName = OutputNameEditor.BuildRenamedName(currentName, desiredName);
        if (newName is null || string.Equals(newName, currentName, StringComparison.OrdinalIgnoreCase))
            return;

        string directory = Path.GetDirectoryName(job.OutputPath)!;
        _ = RenameCoreAsync(job, directory, newName);
    }

    /// <summary>Toggles a quick suffix chip on the job's display name.</summary>
    public void ApplySuffix(FileJobViewModel job, string suffix) =>
        Rename(job, OutputNameEditor.WithSuffix(job.DisplayName, suffix));

    private async Task RenameCoreAsync(FileJobViewModel job, string directory, string newName)
    {
        using var op = AppLog.BeginOperation(
            LogCategory.File,
            "手动改名",
            $"{Path.GetFileName(job.OutputPath)} → {newName}");
        try
        {
            string target = OutputNameGenerator.FindAvailablePath(directory, newName);
            // The file gets a numbered name when the wanted one is taken; saying so briefly keeps
            // 「合同 (2).pdf」 from looking like a bug.
            bool numbered = !string.Equals(
                Path.GetFileName(target), newName, StringComparison.OrdinalIgnoreCase);
            op.Step("解析目标名", Path.GetFileName(target));

            string currentPath = job.OutputPath!;
            int attempt = 1;
            for (; ; attempt++)
            {
                try
                {
                    File.Move(currentPath, target);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    // Rebuild from the job's live path each pass — the background naming chain
                    // may have moved it meanwhile.
                    currentPath = job.OutputPath ?? currentPath;
                    await Task.Delay(attempt * 400).ConfigureAwait(true);
                }
            }
            op.Step("移动文件", $"尝试 {attempt} 次");

            AppLog.Info($"重命名输出文件：{Path.GetFileName(job.OutputPath)} → {Path.GetFileName(target)}");
            job.UpdateRenamed(target);
            if (numbered) ShowNameNote(job, $"同名已存在，已命名为 {Path.GetFileName(target)}");
            op.Complete($"→ {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"重命名失败：{job.OutputPath} → {newName}", ex);
            op.Fail("重命名失败", ex);
            _dialogs.Warn($"重命名失败：{ex.Message}", "提示");
        }
    }

    /// <summary>Shows an explanation on the row for a few seconds, then clears it — but only if
    /// nothing newer (e.g. the background naming note) has taken the line since.</summary>
    private static void ShowNameNote(FileJobViewModel job, string note)
    {
        job.SetNamingNote(note);
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            if (string.Equals(job.NamingNote, note, StringComparison.Ordinal))
                job.SetNamingNote(null);
        });
    }
}
