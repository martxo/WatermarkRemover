using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.ViewModels;
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
        try
        {
            string target = OutputNameGenerator.FindAvailablePath(directory, newName);
            string currentPath = job.OutputPath!;
            for (int attempt = 1; ; attempt++)
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

            AppLog.Info($"重命名输出文件：{Path.GetFileName(job.OutputPath)} → {Path.GetFileName(target)}");
            job.UpdateRenamed(target);
        }
        catch (Exception ex)
        {
            AppLog.Error($"重命名失败：{job.OutputPath} → {newName}", ex);
            _dialogs.Warn($"重命名失败：{ex.Message}", "提示");
        }
    }
}
