using System.Collections.ObjectModel;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 「在哪个目录上传，就默认这个当前路径」：网盘管理窗口里上传成功后，当前浏览位置（存储盘 +
/// 目录路径）要回报给宿主持久化，成为之后任务行/快速上传的默认目标。路径记法与设置输入框一致
/// （"/上传/2026"，根目录 = 空串）。
/// </summary>
public sealed class UploadLocationMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-upload-loc-" + Guid.NewGuid().ToString("N"));

    public UploadLocationMemoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "/上传")]
    [InlineData(2, "/上传/2026")]
    public void The_browse_stack_joins_into_the_settings_path_notation(int levels, string expected)
    {
        var path = new List<(string Id, string Name)> { ("", "根目录") };
        if (levels >= 1) path.Add(("f1", "上传"));
        if (levels >= 2) path.Add(("f2", "2026"));

        Assert.Equal(expected, NetdiskViewModel.BuildRemotePath(path));
    }

    [Fact]
    public async Task Uploading_into_a_folder_remembers_that_location()
    {
        var session = new StubSession("测试用户", new NetdiskCredentials(new Dictionary<string, string>()));
        var remembered = new Remembered();
        var vm = CreateViewModel(session, remembered);

        await vm.SelectAccountAsync("stub", "测试用户");

        // 进入「上传」文件夹再上传 —— 记住的必须是那个文件夹，不是根目录。
        var folder = vm.Items.Single(i => i.File.IsFolder);
        vm.OpenFolderCommand.Execute(folder);
        for (int i = 0; i < 500 && vm.Breadcrumbs.Count < 2; i++) await Task.Delay(10);

        string file = Path.Combine(_dir, "报告.pdf");
        await File.WriteAllTextAsync(file, "x");
        await vm.UploadFilesAsync(new[] { file });

        Assert.Equal(new[] { "f1" }, session.Uploads);
        Assert.Equal(("默认盘", "/上传"), remembered.Value);
    }

    [Fact]
    public async Task Uploading_at_the_root_remembers_an_empty_directory()
    {
        var session = new StubSession("测试用户", new NetdiskCredentials(new Dictionary<string, string>()));
        var remembered = new Remembered();
        var vm = CreateViewModel(session, remembered);

        await vm.SelectAccountAsync("stub", "测试用户");
        string file = Path.Combine(_dir, "根目录文件.txt");
        await File.WriteAllTextAsync(file, "x");
        await vm.UploadFilesAsync(new[] { file });

        // 根目录 = 空串（与设置输入框"留空 = 该盘根目录"的约定一致），存储盘仍然记下。
        Assert.Equal(("默认盘", ""), remembered.Value);
    }

    [Fact]
    public async Task Uploading_nothing_remembers_nothing()
    {
        var session = new StubSession("测试用户", new NetdiskCredentials(new Dictionary<string, string>()));
        var remembered = new Remembered();
        var vm = CreateViewModel(session, remembered);

        await vm.SelectAccountAsync("stub", "测试用户");
        await vm.UploadFilesAsync(Array.Empty<string>());

        Assert.Empty(session.Uploads);
        Assert.Null(remembered.Value);
    }

    private sealed class Remembered
    {
        public (string Drive, string Path)? Value;
    }

    private NetdiskViewModel CreateViewModel(StubSession session, Remembered remembered)
    {
        var catalog = new NetdiskProviderCatalog();
        catalog.Register(new StubProvider("stub", "测试云盘", "测试用户", session));
        var accounts = new ObservableCollection<NetdiskAccountState>
        {
            new("stub", "测试用户", new Dictionary<string, string>()),
        };

        return new NetdiskViewModel(
            catalog, accounts, _ => { }, new NullDialogs(), (_, _) => { },
            rememberUploadLocation: (drive, path) => remembered.Value = (drive, path));
    }

    private sealed class NullDialogs : IDialogService
    {
        public bool Confirm(string message, string title) => true;
        public bool ConfirmYesNo(string message, string title) => true;
        public void Info(string message, string title) { }
        public void Warn(string message, string title) { }
        public void Error(string message, string title) { }
    }

    private sealed class StubProvider(string id, string name, string accountName, StubSession session) : INetdiskProvider
    {
        public string Id => id;

        public string DisplayName => name;

        public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; } =
            Array.Empty<NetdiskCredentialField>();

        public INetdiskOAuthFlow? OAuth => null;

        public Task<INetdiskSession> ConnectAsync(NetdiskCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult<INetdiskSession>(session);
    }

    /// <summary>Root lists one 「上传」 folder; uploads are recorded instead of transferred.</summary>
    private sealed class StubSession(string accountName, NetdiskCredentials credentials)
        : INetdiskSession
    {
        public NetdiskAccountInfo Account { get; } = new("stub", accountName);

        public NetdiskCredentials CurrentCredentials { get; } = credentials;

        public IReadOnlyList<NetdiskDrive> Drives { get; } = new[] { new NetdiskDrive("d", "默认盘") };

        public string CurrentDriveId => "d";

        public List<string> Uploads { get; } = new();

        public Task SwitchDriveAsync(string driveId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<NetdiskQuota> GetQuotaAsync(CancellationToken ct = default) =>
            Task.FromResult(new NetdiskQuota(1, 2));

        public Task<NetdiskPage> ListAsync(string parentFolderId, int pageSize = 100, string? cursor = null,
            CancellationToken ct = default)
        {
            IReadOnlyList<NetdiskFile> items = parentFolderId == string.Empty
                ? new[]
                {
                    new NetdiskFile("f1", "上传", IsFolder: true, 0, null),
                    new NetdiskFile("doc", "报告.pdf", IsFolder: false, 3, null),
                }
                : Array.Empty<NetdiskFile>();
            return Task.FromResult(new NetdiskPage(items, null));
        }

        public Task<NetdiskFile> CreateFolderAsync(string parentFolderId, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(IReadOnlyList<string> fileIds, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<NetdiskFile> UploadAsync(string parentFolderId, string localPath,
            IProgress<NetdiskTransferProgress>? progress = null, CancellationToken ct = default)
        {
            Uploads.Add(parentFolderId);
            return Task.FromResult(new NetdiskFile(localPath, Path.GetFileName(localPath), false, 1, null, parentFolderId));
        }

        public Task DownloadAsync(string fileId, string localPath,
            IProgress<NetdiskTransferProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
