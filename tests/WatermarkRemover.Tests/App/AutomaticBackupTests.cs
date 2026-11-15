using System.Collections.Concurrent;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

public sealed class AutomaticBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-automatic-backup-" + Guid.NewGuid().ToString("N"));

    public AutomaticBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("报告.PDF", true)]
    [InlineData("照片.jpeg", true)]
    [InlineData("表格.xlsx", true)]
    [InlineData("压缩包.rar", true)]
    [InlineData("说明.txt", false)]
    [InlineData("~$表格.xlsx", false)]
    [InlineData("._照片.png", false)]
    [InlineData("文档.pdf.tmp", false)]
    public void Only_document_types_are_supported_and_office_temporary_files_are_excluded(
        string name, bool expected)
    {
        Assert.Equal(expected, AutomaticBackupFileFilter.IsSupported(Path.Combine(_dir, name)));
    }

    [Fact]
    public void Relative_paths_cannot_escape_the_monitored_root()
    {
        string root = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(root);
        string outside = Path.Combine(_dir, "outside.pdf");
        File.WriteAllText(outside, "x");

        Assert.False(AutomaticBackupFileFilter.TryGetSupportedRelativePath(root, outside, out _));
        Assert.True(AutomaticBackupFileFilter.TryGetSupportedRelativePath(
            root, Path.Combine(root, "项目", "报告.pdf"), out string relative));
        Assert.Equal("项目/报告.pdf", relative);
        Assert.Equal("/桌面文件备份/项目/2026", AutomaticBackupFileFilter.CombineRemoteDirectory(
            "/桌面文件备份/", "项目\\2026"));
        Assert.Throws<ArgumentException>(() =>
            AutomaticBackupFileFilter.NormalizeRelativePath("../outside.pdf"));
    }

    [Fact]
    public void Consent_is_invalidated_when_scope_account_or_destination_changes()
    {
        var settings = ValidSettings();

        Assert.True(AutomaticBackupFileFilter.HasValidConsent(settings));

        AppSettings changed = settings.Clone();
        changed.AutomaticBackupRemoteDirectory = "/另一个目录";
        Assert.False(AutomaticBackupFileFilter.HasValidConsent(changed));

        changed = settings.Clone();
        changed.AutomaticBackupAccountName = "另一个账号";
        Assert.False(AutomaticBackupFileFilter.HasValidConsent(changed));

        changed = settings.Clone();
        changed.AutomaticBackupEnabled = false;
        Assert.False(AutomaticBackupFileFilter.HasValidConsent(changed));
    }

    [Fact]
    public void State_is_isolated_by_configuration_and_relative_path_and_survives_reload()
    {
        string path = Path.Combine(_dir, "state", "state.json");
        var store = new AutomaticBackupStateStore(path);
        var first = new AutomaticBackupState(
            12, DateTimeOffset.UtcNow, "abc", DateTimeOffset.UtcNow);

        Assert.True(store.Upsert("Config-A", "项目\\报告.pdf", first));
        Assert.True(store.Upsert("Config-B", "项目/报告.pdf", first with { Length = 99 }));

        var reloaded = new AutomaticBackupStateStore(path);
        Assert.True(reloaded.TryGet("config-a", "项目/报告.pdf", out AutomaticBackupState? a));
        Assert.True(reloaded.TryGet("config-b", "项目\\报告.pdf", out AutomaticBackupState? b));
        Assert.Equal(12, a!.Length);
        Assert.Equal(99, b!.Length);
        Assert.False(reloaded.TryGet("config-c", "项目/报告.pdf", out _));
        Assert.False(File.Exists(path + ".tmp"));

        Assert.True(reloaded.PruneMissing("config-a", Array.Empty<string>()));
        Assert.False(reloaded.TryGet("config-a", "项目/报告.pdf", out _));
        Assert.True(reloaded.TryGet("config-b", "项目/报告.pdf", out _));
    }

    [Fact]
    public void Damaged_state_is_treated_as_not_uploaded()
    {
        string path = Path.Combine(_dir, "damaged", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not json");
        var store = new AutomaticBackupStateStore(path);

        Assert.False(store.TryGet("config", "报告.pdf", out _));
    }

    [Fact]
    public async Task Authorized_service_recursively_uploads_stable_files_and_records_state()
    {
        string root = Path.Combine(_dir, "watched");
        string nested = Path.Combine(root, "项目", "2026");
        Directory.CreateDirectory(nested);
        string file = Path.Combine(nested, "报告.pdf");
        await File.WriteAllTextAsync(file, "stable-content");
        AppSettings settings = ValidSettings(root);
        var store = new AutomaticBackupStateStore(Path.Combine(_dir, "state.json"));
        var uploads = new ConcurrentQueue<UploadCall>();

        using var service = NewService(store, (target, relativeDirectory, localPath, overwrite, _) =>
        {
            uploads.Enqueue(new UploadCall(
                target, relativeDirectory, Path.GetFileName(localPath), File.ReadAllText(localPath), overwrite));
            return Task.CompletedTask;
        });

        await service.ApplyConfigurationAsync(settings);
        await WaitUntilAsync(() => uploads.Count == 1 && service.PendingCount == 0);

        UploadCall upload = Assert.Single(uploads);
        Assert.Equal("项目/2026", upload.RelativeDirectory);
        Assert.Equal("报告.pdf", upload.FileName);
        Assert.Equal("stable-content", upload.Content);
        Assert.Equal("/桌面文件备份", upload.Target.RemoteDirectory);
        Assert.Equal("alice", upload.Target.AccountName);
        Assert.True(store.TryGet(
            settings.AutomaticBackupConsentFingerprint, "项目/2026/报告.pdf",
            out AutomaticBackupState? state));
        Assert.Equal(new FileInfo(file).Length, state!.Length);
        Assert.False(string.IsNullOrWhiteSpace(state.Sha256));
        Assert.True(service.IsRunning);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task An_in_place_update_overwrites_but_a_new_same_named_path_never_does()
    {
        string root = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "征信.pdf");
        await File.WriteAllTextAsync(file, "11月报告");
        AppSettings settings = ValidSettings(root);
        var store = new AutomaticBackupStateStore(Path.Combine(_dir, "state.json"));
        var uploads = new ConcurrentQueue<UploadCall>();

        using var service = NewService(store, (target, relativeDirectory, localPath, overwrite, _) =>
        {
            uploads.Enqueue(new UploadCall(
                target, relativeDirectory, Path.GetFileName(localPath), File.ReadAllText(localPath), overwrite));
            return Task.CompletedTask;
        });

        await service.ApplyConfigurationAsync(settings);
        await WaitUntilAsync(() => uploads.Count == 1 && service.PendingCount == 0);
        // First upload: brand-new path, no record — the drive may hold an unrelated file under
        // the same name (e.g. an earlier backup whose local copy was deleted), so never overwrite.
        Assert.False(Assert.Single(uploads).Overwrite);

        // In-place update of the SAME file: the cloud copy is the old version of itself.
        await File.WriteAllTextAsync(file, "12月报告");
        await WaitUntilAsync(() => uploads.Count == 2 && service.PendingCount == 0);
        Assert.True(uploads.Last().Overwrite);

        // Simulate "old local copy deleted, new file with the SAME name appears": clear the
        // record, drop fresh content. The drive may still hold the older backup — keep it.
        store.PruneMissing(settings.AutomaticBackupConsentFingerprint,
            new List<string> { "不存在的路径.pdf" }.ToHashSet(StringComparer.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(file, "1月报告");
        await WaitUntilAsync(() => uploads.Count == 3 && service.PendingCount == 0);
        Assert.False(uploads.Last().Overwrite);
    }

    [Fact]
    public async Task A_restart_does_not_upload_an_unchanged_file_again()
    {
        string root = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "报告.pdf"), "same");
        AppSettings settings = ValidSettings(root);
        string statePath = Path.Combine(_dir, "state.json");
        int uploads = 0;

        using (var first = NewService(
                   new AutomaticBackupStateStore(statePath),
                   (_, _, _, _, _) =>
                   {
                       Interlocked.Increment(ref uploads);
                       return Task.CompletedTask;
                   }))
        {
            await first.ApplyConfigurationAsync(settings);
            await WaitUntilAsync(() => uploads == 1 && first.PendingCount == 0);
        }

        using var restarted = NewService(
            new AutomaticBackupStateStore(statePath),
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref uploads);
                return Task.CompletedTask;
            });
        await restarted.ApplyConfigurationAsync(settings);
        await WaitUntilAsync(() => restarted.PendingCount == 0 && restarted.IsRunning);
        await Task.Delay(150);

        Assert.Equal(1, uploads);
    }

    [Fact]
    public async Task A_file_created_after_startup_is_observed_recursively()
    {
        string root = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(root);
        AppSettings settings = ValidSettings(root);
        var uploads = new ConcurrentQueue<UploadCall>();
        using var service = NewService(
            new AutomaticBackupStateStore(Path.Combine(_dir, "state.json")),
            (target, relativeDirectory, localPath, overwrite, _) =>
            {
                uploads.Enqueue(new UploadCall(
                    target, relativeDirectory, Path.GetFileName(localPath), File.ReadAllText(localPath), overwrite));
                return Task.CompletedTask;
            });
        await service.ApplyConfigurationAsync(settings);

        string nested = Path.Combine(root, "新建目录");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "新增.docx"), "new-file");

        await WaitUntilAsync(() => uploads.Count == 1);
        Assert.Equal("新建目录", Assert.Single(uploads).RelativeDirectory);
    }

    [Fact]
    public async Task Without_valid_consent_the_service_does_not_start_or_upload()
    {
        string root = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "报告.pdf"), "private");
        AppSettings settings = ValidSettings(root);
        settings.AutomaticBackupConsentFingerprint = "stale";
        int uploads = 0;
        using var service = NewService(
            new AutomaticBackupStateStore(Path.Combine(_dir, "state.json")),
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref uploads);
                return Task.CompletedTask;
            });

        await service.ApplyConfigurationAsync(settings);
        await Task.Delay(150);

        Assert.False(service.IsRunning);
        Assert.Equal(0, service.PendingCount);
        Assert.Equal(0, uploads);
    }

    private AppSettings ValidSettings(string? root = null)
    {
        root ??= Path.Combine(_dir, "watched");
        Directory.CreateDirectory(root);
        var settings = new AppSettings
        {
            AutomaticBackupEnabled = true,
            AutomaticBackupRootDirectory = root,
            AutomaticBackupProviderId = "stub",
            AutomaticBackupAccountName = "alice",
            AutomaticBackupDrive = "资源库",
            AutomaticBackupRemoteDirectory = "/桌面文件备份",
            AutomaticBackupConsentedAtUtc = DateTimeOffset.UtcNow,
            // 0 = 未配置：服务回退到注入的短窗口，测试不必等真实的 15 秒/30 分钟。
            AutomaticBackupStableSeconds = 0,
            AutomaticBackupRescanMinutes = 0,
        };
        settings.AutomaticBackupConsentFingerprint =
            AutomaticBackupFileFilter.ConsentFingerprint(settings);
        return settings;
    }

    [Fact]
    public async Task Settings_stable_seconds_override_the_injected_default_window()
    {
        // 注入的稳定窗口是 10 秒；配置写 1 秒时必须按 1 秒生效——证明设置真的接到了服务。
        string root = Path.Combine(_dir, "watched-stable");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "报告.pdf"), "config-window");
        AppSettings settings = ValidSettings(root);
        settings.AutomaticBackupStableSeconds = 1;
        settings.AutomaticBackupConsentFingerprint =
            AutomaticBackupFileFilter.ConsentFingerprint(settings);
        int uploads = 0;
        using var service = new AutomaticBackupService(
            new AutomaticBackupStateStore(Path.Combine(_dir, "stable-state.json")),
            Path.Combine(_dir, "snapshots"),
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref uploads);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20));

        await service.ApplyConfigurationAsync(settings);
        await WaitUntilAsync(() => uploads == 1);

        Assert.Equal(1, uploads);
    }

    [Fact]
    public void New_installation_defaults_are_fifteen_seconds_and_thirty_minutes()
    {
        var settings = new AppSettings();

        Assert.Equal(15, settings.AutomaticBackupStableSeconds);
        Assert.Equal(30, settings.AutomaticBackupRescanMinutes);

        AppSettings clone = settings.Clone();
        clone.AutomaticBackupStableSeconds = 90;
        clone.AutomaticBackupRescanMinutes = 5;
        Assert.Equal(15, settings.AutomaticBackupStableSeconds);
        Assert.Equal(30, settings.AutomaticBackupRescanMinutes);
    }

    private AutomaticBackupService NewService(
        AutomaticBackupStateStore store,
        Func<NetdiskUploadTarget, string, string, bool, CancellationToken, Task> upload) =>
        new(store, Path.Combine(_dir, "snapshots"), upload,
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed record UploadCall(
        NetdiskUploadTarget Target,
        string RelativeDirectory,
        string FileName,
        string Content,
        bool Overwrite);
}
