using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

public sealed class NetdiskAccessGateTests
{
    [Fact]
    public void Ordinary_settings_hide_netdisk_navigation_and_disable_every_cloud_command()
    {
        var settings = new AppSettings
        {
            NetdiskUploadDrive = "资源库",
            NetdiskUploadDirectory = "/原目录",
        };
        var dialogs = new NullDialogs();
        int browseCalls = 0;
        var vm = new SettingsViewModel(
            settings, dialogs, new NullShell(),
            browseUploadFolder: () =>
            {
                browseCalls++;
                return ("资源库", "/新目录");
            },
            isNetdiskAdmin: false);

        Assert.DoesNotContain(vm.NavItems, item => item.Id == "netdisk");
        vm.SearchText = "网盘";
        Assert.DoesNotContain(vm.NavItems, item => item.IsVisible && item.Id == "netdisk");
        Assert.False(vm.UploadLogCommand.CanExecute(null));
        Assert.False(vm.UploadCrashReportsCommand.CanExecute(null));
        Assert.False(vm.ExportLoginPackCommand.CanExecute(null));
        Assert.False(vm.ImportLoginPackCommand.CanExecute(null));
        Assert.False(vm.BrowseUploadFolderCommand.CanExecute(null));
        Assert.Empty(vm.CloudAccountText);

        // Execution guards matter too: a stale binding or direct call must not bypass CanExecute.
        vm.BrowseUploadFolderCommand.Execute(null);
        vm.ImportLoginPackFrom(Path.Combine(Path.GetTempPath(), "missing-login-pack.json"));
        Assert.Equal(0, browseCalls);
        Assert.Equal("/原目录", vm.Settings.NetdiskUploadDirectory);
        Assert.Equal(0, dialogs.PromptCount);
        vm.Detach();
    }

    [Fact]
    public void Admin_settings_retains_the_netdisk_navigation()
    {
        var vm = new SettingsViewModel(
            new AppSettings(), new NullDialogs(), new NullShell(), isNetdiskAdmin: true);

        Assert.Contains(vm.NavItems, item => item.Id == "netdisk");
        Assert.NotEmpty(vm.CloudAccountText);
        vm.Detach();
    }

    [Fact]
    public async Task Ordinary_coordinator_cannot_open_or_upload_and_does_not_expose_accounts()
    {
        var settings = new AppSettings();
        var coordinator = new NetdiskCoordinator(
            settings, new NullDialogs(), () => false, isNetdiskAdmin: false);
        coordinator.Upsert(new NetdiskAccountState(
            "unknown-test-provider", "秘密账号", new Dictionary<string, string>()));

        Assert.False(coordinator.OpenCommand.CanExecute(null));
        Assert.False(coordinator.QuickUploadCommand.CanExecute(null));
        Assert.Null(coordinator.BrowseForUploadFolder());
        Assert.Empty(coordinator.SummaryText);

        var failures = await coordinator.UploadFilesAsync(new[] { "secret.pdf" });

        var failure = Assert.Single(failures);
        Assert.Equal("secret.pdf", failure.Path);
        Assert.Equal("当前模式不允许手动上传", failure.Error);
        Assert.Empty(coordinator.SummaryText);
    }

    [Fact]
    public void Ordinary_composition_does_not_register_the_netdisk_feature()
    {
        var composition = new WatermarkRemover.App.AppComposition(isNetdiskAdmin: false);

        Assert.DoesNotContain(composition.Features, feature => feature.Id == "netdisk.page");
    }

    private sealed class NullDialogs : IDialogService
    {
        public int PromptCount { get; private set; }

        public bool Confirm(string message, string title)
        {
            PromptCount++;
            return true;
        }

        public bool ConfirmYesNo(string message, string title)
        {
            PromptCount++;
            return true;
        }

        public void Info(string message, string title) => PromptCount++;

        public void Warn(string message, string title) => PromptCount++;

        public void Error(string message, string title) => PromptCount++;
    }

    private sealed class NullShell : IShellOpenService
    {
        public bool TryOpenDirectory(string directory, out string? error)
        {
            error = null;
            return true;
        }
    }
}

public sealed class NetdiskTransferServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-netdisk-transfer-" + Guid.NewGuid().ToString("N"));

    public NetdiskTransferServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Upload_uses_the_requested_drive_and_folder_without_overwrite_or_delete()
    {
        string file = Path.Combine(_dir, "报告.pdf");
        await File.WriteAllTextAsync(file, "content");
        var session = new StubSession();
        var catalog = new NetdiskProviderCatalog();
        catalog.Register(new StubProvider(session));
        NetdiskAccountState? persisted = null;
        var transfer = new NetdiskTransferService(
            catalog,
            (provider, name) => new NetdiskAccountState(
                provider, name, new Dictionary<string, string> { ["token"] = "old" }),
            account => persisted = account);

        await transfer.UploadFileAsync(
            new NetdiskUploadTarget("stub", "alice", "二级盘", "/桌面备份/项目"), file);

        StubUpload upload = Assert.Single(session.Uploads);
        Assert.Equal("folder-项目", upload.ParentFolderId);
        Assert.Equal("报告.pdf", upload.Name);
        Assert.False(upload.Overwrite);
        Assert.Equal("d2", session.CurrentDriveId);
        Assert.Equal(new[] { "桌面备份", "项目" }, session.CreatedFolders);
        Assert.Equal(0, session.DeleteCalls);
        Assert.NotNull(persisted);
        Assert.Equal("stub", persisted!.ProviderId);
        Assert.Equal("alice", persisted.Name);
        Assert.Equal("latest", persisted.Values["token"]);
    }

    private sealed record StubUpload(string ParentFolderId, string Name, bool Overwrite);

    private sealed class StubProvider(StubSession session) : INetdiskProvider
    {
        public string Id => "stub";
        public string DisplayName => "测试网盘";
        public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; } =
            Array.Empty<NetdiskCredentialField>();
        public INetdiskOAuthFlow? OAuth => null;

        public Task<INetdiskSession> ConnectAsync(
            NetdiskCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult<INetdiskSession>(session);
    }

    private sealed class StubSession : INetdiskSession
    {
        public NetdiskAccountInfo Account { get; } = new("stub", "alice");
        public NetdiskCredentials CurrentCredentials { get; } = new(
            new Dictionary<string, string> { ["token"] = "latest" });
        public IReadOnlyList<NetdiskDrive> Drives { get; } =
            new[] { new NetdiskDrive("d1", "资源库"), new NetdiskDrive("d2", "二级盘") };
        public string CurrentDriveId { get; private set; } = "d1";
        public List<StubUpload> Uploads { get; } = new();
        public List<string> CreatedFolders { get; } = new();
        public int DeleteCalls { get; private set; }

        public Task SwitchDriveAsync(string driveId, CancellationToken ct = default)
        {
            CurrentDriveId = driveId;
            return Task.CompletedTask;
        }

        public Task<NetdiskQuota> GetQuotaAsync(CancellationToken ct = default) =>
            Task.FromResult(new NetdiskQuota(1, 2));

        public Task<NetdiskPage> ListAsync(
            string parentFolderId, int pageSize = 100, string? cursor = null,
            CancellationToken ct = default) =>
            Task.FromResult(new NetdiskPage(Array.Empty<NetdiskFile>(), null));

        public Task<NetdiskFile> CreateFolderAsync(
            string parentFolderId, string name, CancellationToken ct = default)
        {
            CreatedFolders.Add(name);
            return Task.FromResult(new NetdiskFile(
                "folder-" + name, name, IsFolder: true, 0, null, parentFolderId));
        }

        public Task DeleteAsync(IReadOnlyList<string> fileIds, CancellationToken ct = default)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task<NetdiskFile> UploadAsync(
            string parentFolderId, string localPath,
            IProgress<NetdiskTransferProgress>? progress = null,
            CancellationToken ct = default) =>
            UploadAsAsync(parentFolderId, localPath, Path.GetFileName(localPath), false, progress, ct);

        public Task<NetdiskFile> UploadAsAsync(
            string parentFolderId, string localPath, string name, bool overwrite,
            IProgress<NetdiskTransferProgress>? progress = null,
            CancellationToken ct = default)
        {
            Uploads.Add(new StubUpload(parentFolderId, name, overwrite));
            return Task.FromResult(new NetdiskFile(
                "uploaded", name, IsFolder: false, 1, null, parentFolderId));
        }

        public Task DownloadAsync(
            string fileId, string localPath,
            IProgress<NetdiskTransferProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
