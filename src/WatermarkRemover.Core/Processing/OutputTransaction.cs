using System.IO;

namespace WatermarkRemover.Core.Processing;

/// <summary>
/// 一次「先写临时文件、成功后再改名」的输出事务。
/// 产物先落在目标目录下的一次性临时目录里，写入成功后才原子改名到最终位置；任务中途失败时
/// 临时目录连同半截文件一起删掉，输出目录里不会出现写了一半的产物，已有的旧文件也不会被这次
/// 失败的任务覆盖。
/// </summary>
public sealed class OutputTransaction : IDisposable
{
    /// <summary>临时目录名。带点前缀是为了和用户自己的文件在视觉上分开，它用完即删。</summary>
    private const string ScratchName = ".wm-tmp";

    private readonly string _scratchRoot;
    private readonly string _scratchDirectory;
    private readonly string _tempPath;
    private bool _committed;

    private OutputTransaction(string finalPath, string tempPath, string scratchRoot, string scratchDirectory)
    {
        FinalPath = finalPath;
        _tempPath = tempPath;
        _scratchRoot = scratchRoot;
        _scratchDirectory = scratchDirectory;
    }

    /// <summary>最终产物路径。只有 <see cref="Commit"/> 成功之后这个路径才存在。</summary>
    public string FinalPath { get; }

    /// <summary>实际写入的临时路径：与目标同目录、同扩展名（PDF 保存器按扩展名选格式）。</summary>
    public string TempPath => _tempPath;

    /// <summary>
    /// 为 <paramref name="finalPath"/> 开一个事务。临时目录建在目标目录下，保证提交时是同卷
    /// 改名（跨卷的 File.Move 会退化成复制 + 删除，就不再是原子操作）。
    /// </summary>
    public static OutputTransaction Begin(string finalPath)
    {
        if (string.IsNullOrWhiteSpace(finalPath))
            throw new ArgumentException("输出路径不能为空。", nameof(finalPath));

        string full = Path.GetFullPath(finalPath);
        string directory = Path.GetDirectoryName(full)
            ?? throw new ArgumentException("输出路径缺少所在目录。", nameof(finalPath));

        string scratchRoot = Path.Combine(directory, ScratchName);
        // 每个事务独占一个子目录，并发任务互不干扰。
        string scratchDirectory = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDirectory);

        string tempPath = Path.Combine(scratchDirectory, Path.GetFileName(full));
        return new OutputTransaction(full, tempPath, scratchRoot, scratchDirectory);
    }

    /// <summary>
    /// 把临时文件改名到最终位置。目标已存在时直接抛 <see cref="IOException"/>——宁可让这一份
    /// 失败，也不覆盖别人刚生成的文件。
    /// </summary>
    public void Commit()
    {
        if (_committed) return;
        if (!File.Exists(_tempPath))
            throw new FileNotFoundException("临时输出文件不存在，写入没有完成。", _tempPath);

        File.Move(_tempPath, FinalPath);
        _committed = true;
        CleanupScratch();
    }

    /// <summary>没提交就释放 = 回滚：删掉半截文件，最终路径保持原样。</summary>
    public void Dispose()
    {
        if (!_committed)
        {
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch (Exception)
            {
                // 清理是尽力而为；删不掉也不影响最终结果（它本来就不该存在）。
            }
        }

        CleanupScratch();
    }

    private void CleanupScratch()
    {
        try
        {
            if (Directory.Exists(_scratchDirectory))
                Directory.Delete(_scratchDirectory, recursive: true);

            // 根目录由所有事务共享，只有空了才删得掉——并发任务还在用时它会静默留下。
            if (Directory.Exists(_scratchRoot))
                Directory.Delete(_scratchRoot, recursive: false);
        }
        catch (Exception)
        {
            // 同上：清理失败只留下一个空目录，不代表任何任务输出有问题。
        }
    }
}
