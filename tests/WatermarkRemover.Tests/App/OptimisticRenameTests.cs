using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 乐观改名：首屏命名一完成就先显示识别名，磁盘上仍是时间戳 PDF；File.Move 仍等落盘与
/// 预览稳定之后。显示名只是覆盖层——所有路径字段保持真实，手动改名优先，失败回退并提示。
/// </summary>
public sealed class OptimisticRenameTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "wm-optimistic-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDialogs _dialogs = new();

    public OptimisticRenameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public List<string> Messages { get; } = new();

        public bool Confirm(string message, string title) => true;

        public bool ConfirmYesNo(string message, string title) => true;

        public void Info(string message, string title) => Messages.Add(message);

        public void Warn(string message, string title) => Messages.Add(message);

        public void Error(string message, string title) => Messages.Add(message);
    }

    private sealed class FakeShell : IShellOpenService
    {
        public bool TryOpenDirectory(string directory, out string? error)
        {
            error = null;
            return true;
        }
    }

    /// <summary>A scan row under its timestamp name, exactly as Apply leaves it on disk.</summary>
    private (FileJobViewModel Job, string RealPath) NewFinishedScan()
    {
        string real = Path.Combine(_dir, "扫描 20260101-120000.pdf");
        File.WriteAllBytes(real, PdfFixture.CamScannerLike().Build());

        var job = new FileJobViewModel("扫描中", Array.Empty<string>())
        {
            OwnerFeatureId = "scan.page",
        };
        job.MarkProcessing("正在扫描…");
        job.AdoptSource(real);
        job.Apply(FeatureResult.Success(real, real, "扫描 2 页", 2));
        return (job, real);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        for (int i = 0; i < timeoutMs / 20 && !condition(); i++)
            await Task.Delay(20);
        return condition();
    }

    [Fact]
    public void First_screen_name_shows_immediately_while_the_real_path_stays()
    {
        string real = Path.Combine(_dir, "扫描 20260101-120000.pdf");
        File.WriteAllBytes(real, PdfFixture.CamScannerLike().Build());

        var job = new FileJobViewModel("扫描中", Array.Empty<string>())
        {
            OwnerFeatureId = "scan.page",
        };
        job.MarkProcessing("正在扫描…");

        // 扫描途中就拿到识别名：还没有 OutputPath，显示名先变，路径不存在可动。
        job.SetOptimisticDisplayName("黄小婷.pdf");
        Assert.Equal("黄小婷.pdf", job.DisplayName);
        Assert.True(job.IsDisplayNameOptimistic);
        Assert.Null(job.OutputPath);

        // PDF 落盘（时间戳名）不能把已经显示的识别名盖回去。
        job.AdoptSource(real);
        job.Apply(FeatureResult.Success(real, real, "扫描 2 页", 2));

        Assert.Equal("黄小婷.pdf", job.DisplayName);
        Assert.Equal(real, job.InputPath);
        Assert.Equal(real, job.OutputPath);
        Assert.Equal("扫描 20260101-120000.pdf", job.FileName);
    }

    [Fact]
    public void A_successful_move_with_the_same_name_does_not_flicker()
    {
        var (job, real) = NewFinishedScan();
        job.SetOptimisticDisplayName("黄小婷.pdf");

        var displayChanges = new List<string>();
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileJobViewModel.DisplayName))
                displayChanges.Add(job.DisplayName);
        };

        string moved = Path.Combine(_dir, "黄小婷.pdf");
        File.Move(real, moved);
        job.UpdateRenamed(moved);

        Assert.Equal("黄小婷.pdf", job.DisplayName);
        Assert.Empty(displayChanges);  // 已经显示对了，落盘时不跳变
        Assert.False(job.IsDisplayNameOptimistic);
        Assert.Equal(moved, job.OutputPath);
    }

    [Fact]
    public void A_failed_move_takes_the_optimistic_name_back_with_a_note()
    {
        var (job, _) = NewFinishedScan();
        job.SetOptimisticDisplayName("黄小婷.pdf");

        job.RevertOptimisticDisplayName("改名失败，已恢复原文件名");

        Assert.Equal("扫描 20260101-120000.pdf", job.DisplayName);
        Assert.False(job.IsDisplayNameOptimistic);
        Assert.Equal("改名失败，已恢复原文件名", job.NamingNote);
        Assert.Equal("改名失败，已恢复原文件名", job.StatusLine);
        // 路径仍是真实文件：回退只动显示。
        Assert.Equal(job.InputPath, job.OutputPath);
    }

    [Fact]
    public void A_failed_scan_row_does_not_keep_the_optimistic_name()
    {
        string real = Path.Combine(_dir, "扫描 20260101-120000.pdf");
        File.WriteAllBytes(real, PdfFixture.CamScannerLike().Build());
        var job = new FileJobViewModel("扫描中", Array.Empty<string>())
        {
            OwnerFeatureId = "scan.page",
        };
        job.MarkProcessing("正在扫描…");
        job.AdoptSource(real);
        job.SetOptimisticDisplayName("黄小婷.pdf");

        job.Apply(FeatureResult.Fail(real, "扫描失败"));

        Assert.Equal("扫描 20260101-120000.pdf", job.DisplayName);
        Assert.False(job.IsDisplayNameOptimistic);
    }

    [Fact]
    public void A_numbered_collision_refreshes_to_the_name_that_landed()
    {
        var (job, _) = NewFinishedScan();
        job.SetOptimisticDisplayName("黄小婷.pdf");

        job.UpdateRenamed(Path.Combine(_dir, "黄小婷 (2).pdf"));

        Assert.Equal("黄小婷 (2).pdf", job.DisplayName);
        Assert.False(job.IsDisplayNameOptimistic);
    }

    [Fact]
    public async Task A_quick_suffix_moves_the_real_path_not_the_optimistic_name()
    {
        var (job, real) = NewFinishedScan();
        job.SetOptimisticDisplayName("黄小婷.pdf");

        var renamer = new JobRenamer(_dialogs);
        renamer.ApplySuffix(job, "-已打印");

        string target = Path.Combine(_dir, "黄小婷-已打印.pdf");
        Assert.True(await WaitAsync(() => File.Exists(target)), "真实路径上的文件应被移动到后缀名");

        // 移动源是真实时间戳文件，乐观名本身从未被当成路径存在过。
        Assert.False(File.Exists(real));
        Assert.False(File.Exists(Path.Combine(_dir, "黄小婷.pdf")));
        Assert.Equal("黄小婷-已打印.pdf", job.DisplayName);
        Assert.False(job.IsDisplayNameOptimistic);
    }

    [Fact]
    public async Task A_manual_rename_invalidates_the_optimistic_name()
    {
        var (job, real) = NewFinishedScan();
        job.SetOptimisticDisplayName("黄小婷.pdf");

        var renamer = new JobRenamer(_dialogs);
        renamer.Rename(job, "李四");

        Assert.False(job.IsDisplayNameOptimistic);
        Assert.True(await WaitAsync(() => File.Exists(Path.Combine(_dir, "李四.pdf"))));
        Assert.False(File.Exists(real));
        Assert.Equal("李四.pdf", job.DisplayName);
    }

    [Fact]
    public void The_preview_strip_header_follows_the_display_name_but_reads_the_real_path()
    {
        string real = Path.Combine(_dir, "扫描 20260101-120000.pdf");
        File.WriteAllBytes(real, PdfFixture.CamScannerLike().Build());

        var vm = new MainViewModel(_dialogs, new FakeShell());
        var job = new FileJobViewModel("扫描中", Array.Empty<string>())
        {
            OwnerFeatureId = "scan.page",
        };
        job.MarkProcessing("正在扫描…");
        job.AdoptSource(real);
        job.Apply(FeatureResult.Success(real, real, "扫描 1 页", 1));
        job.SetOptimisticDisplayName("黄小婷.pdf");

        vm.OpenPreviewStrip(job);

        Assert.NotNull(job.Strip);
        Assert.Equal("黄小婷.pdf", job.Strip!.Title);   // 预览标题立即显示识别名
        Assert.Equal(real, job.OutputPath);             // 文档仍指向真实路径
        Assert.True(File.Exists(real));

        job.RevertOptimisticDisplayName();
        Assert.Equal("扫描 20260101-120000.pdf", job.Strip.Title);
    }
}
