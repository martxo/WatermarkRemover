using System.Collections.ObjectModel;
using System.Text.Json;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.Tests;

/// <summary>
/// DingTalk Drive over the dws CLI: argument assembly (root folder omits --folder, page caps,
/// --yes on destructive shortcuts) and tolerant JSON mapping (nodeId/name/size aliases, hasMore,
/// profile list). Every session call runs against canned CLI output — no process, no network.
/// </summary>
public class DingTalkDriveTests
{
    /// <summary>Records every invocation and answers from a script keyed by the command head.</summary>
    private sealed class FakeDws : DwsCliRunner
    {
        public FakeDws(Func<IReadOnlyList<string>, DwsCommandResult> responder)
            : base("C:\\fake\\dws.exe")
        {
            _responder = responder;
        }

        private readonly Func<IReadOnlyList<string>, DwsCommandResult> _responder;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public List<DwsRunOptions?> Options { get; } = new();

        public override Task<DwsCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            DwsRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(arguments);
            Options.Add(options);
            return Task.FromResult(_responder(arguments));
        }
    }

    private static DwsCommandResult Ok(string json) => new(0, json, string.Empty);

    private static string ListJson(string items, bool hasMore = false, string? cursor = null) =>
        $$"""{ "files": {{items}}, "count": 1, "hasMore": {{(hasMore ? "true" : "false").ToLowerInvariant()}}{{(cursor is null ? "" : $", \"nextCursor\": \"{cursor}\"")}} }""";

    [Fact]
    public async Task ListRootOmitsFolderFlagAndMapsAliases()
    {
        var dws = new FakeDws(_ => Ok(ListJson(
            """[{ "dentryUuid": "n1", "name": "报表.pdf", "type": "file", "size": 2048, "modifiedTime": 1730000000000 }]""",
            hasMore: true, cursor: "c9")));
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));

        var page = await session.ListAsync("");

        var call = Assert.Single(dws.Calls);
        Assert.DoesNotContain("--folder", call);
        Assert.DoesNotContain("--space-id", call);
        var file = Assert.Single(page.Items);
        Assert.Equal(new NetdiskFile("n1", "报表.pdf", false, 2048,
            DateTimeOffset.FromUnixTimeMilliseconds(1730000000000), null), file);
        Assert.Equal("c9", page.NextCursor);
    }

    [Fact]
    public async Task ListSubFolderPassesFolderIdAndCapsPageSize()
    {
        var dws = new FakeDws(_ => Ok(ListJson("[]")));
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));

        await session.ListAsync("folder-1", pageSize: 500);

        var call = Assert.Single(dws.Calls);
        var limitIndex = call.ToList().IndexOf("--limit");
        Assert.Equal("50", call.ElementAt(limitIndex + 1));
        Assert.Contains("folder-1", call);
    }

    [Fact]
    public async Task SearchClampsLimitAndMapsEntries()
    {
        var dws = new FakeDws(_ => Ok(ListJson(
            """[{ "nodeId": "s1", "name": "合同", "fileType": "file", "sizeBytes": 10 }]""")));
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));

        var page = await session.SearchAsync("合同", pageSize: 99);

        var call = Assert.Single(dws.Calls);
        Assert.Equal(new[] { "drive", "+search", "--query", "合同", "--limit", "30" }, call);
        var file = Assert.Single(page.Items);
        Assert.Equal("s1", file.Id);
        Assert.False(file.IsFolder);
    }

    [Fact]
    public async Task CreateFolderReadsBackNodeId()
    {
        var dws = new FakeDws(_ => Ok(
            """{ "success": true, "nodeId": "f1", "folder": { "fileId": "f1", "name": "归档" } }"""));
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));

        var folder = await session.CreateFolderAsync("root-1", "归档");

        Assert.Equal(new NetdiskFile("f1", "归档", true, 0, folder.ModifiedAt, "root-1"), folder);
        var call = Assert.Single(dws.Calls);
        Assert.Equal(new[] { "drive", "+create-folder", "--name", "归档", "--folder", "root-1" }, call);
    }

    [Fact]
    public async Task DeleteAndMoveUseYesFlagPerNode()
    {
        var dws = new FakeDws(_ => Ok("""{ "success": true }"""));
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));

        await session.DeleteAsync(new[] { "a", "b" });
        await session.MoveAsync(new[] { "a" }, "target-1");

        Assert.Equal(
            new[]
            {
                new[] { "drive", "+delete", "--node", "a" },
                new[] { "drive", "+delete", "--node", "b" },
                new[] { "drive", "+move", "--node", "a", "--folder", "target-1" },
            },
            dws.Calls.Select(c => c.ToArray()).ToArray());
        Assert.All(dws.Options, options => Assert.True(options!.Yes));
    }

    [Fact]
    public async Task UploadRunsInFileDirectoryWithBasenameAndReportsProgress()
    {
        var dws = new FakeDws(_ => Ok("""{ "success": true, "nodeId": "u1" }"""));
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));
        var dir = Directory.CreateTempSubdirectory("wr-dingtalk-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "报告.pdf");
            await File.WriteAllTextAsync(path, "payload");

            var reported = new List<NetdiskTransferProgress>();
            var uploaded = await session.UploadAsync("parent-1", path, new SynchronousProgress(reported.Add));

            Assert.Equal("u1", uploaded.Id);
            var call = Assert.Single(dws.Calls);
            Assert.Equal(new[] { "drive", "+upload", "--file", "报告.pdf", "--folder", "parent-1" }, call);
            Assert.Equal(dir.FullName, dws.Options[0]!.WorkingDirectory);
            Assert.True(dws.Options[0]!.Yes);
            Assert.Equal(2, reported.Count);
            Assert.Equal(new FileInfo(path).Length, reported[^1].BytesTransferred);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsOverwriteFindsExistingNodeFirst()
    {
        var dws = new FakeDswScript()
            .Expect(call => call.Contains("--folder"), ListJson(
                """[{ "nodeId": "old-1", "name": "manifest.json", "type": "file", "size": 5 }]"""))
            .Expect(_ => true, """{ "success": true, "nodeId": "old-1" }""");
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));
        var dir = Directory.CreateTempSubdirectory("wr-dingtalk-test-");
        try
        {
            // Same local and remote name: a pure overwrite must not carry --file-name.
            var path = Path.Combine(dir.FullName, "manifest.json");
            await File.WriteAllTextAsync(path, "{}");

            var uploaded = await session.UploadAsAsync("parent-1", path, "manifest.json", overwrite: true);

            Assert.Equal(2, dws.Calls.Count);
            var upload = dws.Calls[1];
            Assert.Contains("--node", upload);
            Assert.Contains("old-1", upload);
            Assert.DoesNotContain("--file-name", upload);
            Assert.Equal("old-1", uploaded.Id);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task QuotaFallsBackToZeroOnUnknownShape()
    {
        var dws = new FakeDws(_ => Ok("""{ "unknown": "shape" }"""));
        await using var session = new DingTalkDriveSession(dws, NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));

        var quota = await session.GetQuotaAsync();

        Assert.Equal(new NetdiskQuota(0, 0), quota);
    }

    [Fact]
    public async Task DrivesExposeMyTeamAndChatAreas()
    {
        await using var session = new DingTalkDriveSession(
            new FakeDws(_ => Ok("{}")), NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"));

        Assert.Equal(new[] { "我的文件", "团队文件", "聊天文件" }, session.Drives.Select(d => d.Name));
        Assert.Equal(DingTalkDriveSession.MyDriveId, session.CurrentDriveId);
        await session.SwitchDriveAsync(DingTalkDriveSession.TeamDriveId);
        Assert.Equal(DingTalkDriveSession.TeamDriveId, session.CurrentDriveId);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SwitchDriveAsync("other"));
    }

    [Fact]
    public async Task DiscoverSpacesReadsMyAndTeamSpaces()
    {
        var dws = new FakeDws(call =>
        {
            var spaceType = call[call.ToList().IndexOf("--type") + 1];
            return Ok(spaceType == "mySpace"
                ? """{ "result": { "items": [ { "spaceId": "my-1", "spaceName": "我的文件" } ] } }"""
                : """{ "result": { "items": [ { "spaceId": "team-1", "spaceName": "全员文件夹" } ] } }""");
        });

        var spaces = await DingTalkDriveSession.DiscoverSpacesAsync(dws);

        Assert.Equal("my-1", spaces.MySpaceId);
        var team = Assert.Single(spaces.TeamSpaces);
        Assert.Equal(new DingTalkSpace("team-1", "全员文件夹"), team);
    }

    /// <summary>
    /// 团队文件 root lists the team spaces; entering one must send its --space-id and the returned
    /// node ids must keep that context so nested listing stays inside the same space.
    /// </summary>
    [Fact]
    public async Task TeamDriveListsSpacesAndKeepsSpaceContext()
    {
        var dws = new FakeDws(call => call.Contains("--space-id")
            ? Ok(ListJson("""[{ "nodeId": "n1", "name": "合同.pdf", "type": "FILE", "fileSize": 10 }]"""))
            : Ok(ListJson("[]")));
        var spaces = new DingTalkSpaces("my-1", new[] { new DingTalkSpace("team-1", "全员文件夹") });
        await using var session = new DingTalkDriveSession(
            dws, NetdiskCredentials.Empty, new NetdiskAccountInfo("p1", "钉钉·张三"), spaces);

        await session.SwitchDriveAsync(DingTalkDriveSession.TeamDriveId);
        var root = await session.ListAsync("");

        var spaceFolder = Assert.Single(root.Items);
        Assert.Equal("team:team-1", spaceFolder.Id);
        Assert.Equal("全员文件夹", spaceFolder.Name);
        Assert.True(spaceFolder.IsFolder);

        var children = await session.ListAsync("team:team-1");

        var file = Assert.Single(children.Items);
        Assert.Equal("team:team-1:n1", file.Id);
        var call = dws.Calls[^1];
        Assert.Equal("team-1", call[call.ToList().IndexOf("--space-id") + 1]);
    }

    /// <summary>
    /// 聊天文件 root lists conversations; opening one resolves newCSpaceIdIM and lists that
    /// conversation's file space, so chat attachments become browsable.
    /// </summary>
    [Fact]
    public async Task ChatDriveListsConversationsThenTheirFileSpace()
    {
        var dws = new FakeDws(call =>
        {
            if (call.Contains("+chat-list"))
            {
                return Ok("""
                    { "chats": [ { "openConversationId": "cid1", "name": "华和文件群" } ],
                      "hasMore": false, "count": 1 }
                    """);
            }

            if (call.Contains("+conversation-info"))
            {
                return Ok("""
                    { "success": true,
                      "result": { "conversationInfo": {
                        "title": "华和文件群",
                        "extension": { "newCSpaceIdIM": "im-space-1" } } } }
                    """);
            }

            return Ok(ListJson("""[{ "nodeId": "f1", "name": "征信.pdf", "type": "FILE", "fileSize": 99 }]"""));
        });
        await using var session = new DingTalkDriveSession(
            dws, NetdiskCredentials.Empty, new NetdiskAccountInfo("p1", "钉钉·张三"),
            new DingTalkSpaces("my-1", Array.Empty<DingTalkSpace>()));

        await session.SwitchDriveAsync(DingTalkDriveSession.ChatDriveId);
        var conversations = await session.ListAsync("");

        var chat = Assert.Single(conversations.Items);
        Assert.Equal("chat:cid1", chat.Id);
        Assert.Equal("华和文件群", chat.Name);

        var files = await session.ListAsync("chat:cid1");

        var file = Assert.Single(files.Items);
        Assert.Equal("chat:cid1:f1", file.Id);
        var listCall = dws.Calls.First(c => c.Contains("--space-id"));
        Assert.Equal("im-space-1", listCall[listCall.ToList().IndexOf("--space-id") + 1]);
    }

    [Fact]
    public async Task TeamRootRejectsUploadUntilASpaceIsEntered()
    {
        await using var session = new DingTalkDriveSession(
            new FakeDws(_ => Ok("{}")), NetdiskCredentials.Empty,
            new NetdiskAccountInfo("p1", "钉钉·张三"),
            new DingTalkSpaces("my-1", new[] { new DingTalkSpace("team-1", "全员文件夹") }));

        await session.SwitchDriveAsync(DingTalkDriveSession.TeamDriveId);

        var ex = await Assert.ThrowsAsync<NetdiskException>(() =>
            session.CreateFolderAsync("", "新文件夹"));
        Assert.Contains("团队文件夹", ex.Message);
    }

    [Fact]
    public void AuthStatusEnvelopeIsReadTolerantly()
    {
        using var loggedOut = JsonDocument.Parse("""{ "success": true, "authenticated": false, "message": "未登录" }""");
        Assert.False(DwsJson.FirstBool(loggedOut.RootElement, "authenticated"));

        using var loggedIn = JsonDocument.Parse("""{ "success": true, "authenticated": true }""");
        Assert.True(DwsJson.FirstBool(loggedIn.RootElement, "authenticated"));
    }

    [Fact]
    public async Task ResolveAccountPicksCurrentProfile()
    {
        var dws = new FakeDws(_ => Ok(
            """
            {
              "success": true,
              "currentProfile": "corp1:u1",
              "profiles": [
                { "profile": "corp1:u1", "corpName": "钉钉", "userName": "张三", "isCurrent": true },
                { "profile": "corp2:u2", "corpName": "其他", "userName": "李四", "isCurrent": false }
              ]
            }
            """));

        var account = await DingTalkDriveProvider.ResolveAccountAsync(dws);

        Assert.Equal("corp1:u1", account.UserId);
        Assert.Equal("钉钉·张三", account.DisplayName);
    }

    [Fact]
    public async Task ProfileListParsesEveryIdentityOnTheMachine()
    {
        var dws = new FakeDws(_ => Ok(
            """
            {
              "success": true,
              "profiles": [
                { "profile": "corp1:u1", "corpName": "甲公司", "userName": "张三",
                  "lastLoginAt": "2026-11-01T09:00:00+08:00", "isCurrent": false },
                { "profile": "corp2:u2", "corpName": "乙公司", "userName": "李四",
                  "lastLoginAt": "2026-11-06T13:45:55+08:00", "isCurrent": true }
              ]
            }
            """));

        var profiles = await DingTalkDriveProvider.ListProfilesAsync(dws);

        Assert.Equal(2, profiles.Count);
        Assert.Equal("甲公司·张三", profiles[0].DisplayName);
        Assert.Equal("乙公司·李四", profiles[1].DisplayName);
        Assert.True(profiles[1].IsCurrent);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 13, 45, 55, TimeSpan.FromHours(8)),
            profiles[1].LastLoginAt);
    }

    /// <summary>
    /// Second DingTalk account: the newly authorized identity must be picked even though dws
    /// still reports the first one as current — otherwise every scan updates the same account.
    /// </summary>
    [Fact]
    public void PickAuthorizedProfilePrefersTheIdentityThatJustLoggedIn()
    {
        var before = new[]
        {
            new DwsProfileInfo("corp1:u1", "甲公司", "张三",
                IsCurrent: true, new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(8))),
        };
        var after = new[]
        {
            new DwsProfileInfo("corp1:u1", "甲公司", "张三",
                IsCurrent: true, new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(8))),
            new DwsProfileInfo("corp2:u2", "乙公司", "李四",
                IsCurrent: false, new DateTimeOffset(2026, 11, 6, 13, 45, 55, TimeSpan.FromHours(8))),
        };

        var picked = DingTalkDriveProvider.PickAuthorizedProfile(before, after, reauthProfile: null);

        Assert.NotNull(picked);
        Assert.Equal("corp2:u2", picked!.Profile);
        Assert.Equal("乙公司·李四", picked.DisplayName);
    }

    [Fact]
    public void PickAuthorizedProfileKeepsTheReauthTargetEvenWhenAnotherIdentityIsNewer()
    {
        var after = new[]
        {
            new DwsProfileInfo("corp1:u1", "甲公司", "张三",
                IsCurrent: true, new DateTimeOffset(2026, 11, 6, 14, 0, 0, TimeSpan.FromHours(8))),
            new DwsProfileInfo("corp2:u2", "乙公司", "李四",
                IsCurrent: false, new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.FromHours(8))),
        };

        var picked = DingTalkDriveProvider.PickAuthorizedProfile(
            Array.Empty<DwsProfileInfo>(), after, reauthProfile: "corp2:u2");

        Assert.Equal("corp2:u2", picked!.Profile);
    }

    /// <summary>
    /// Scanning a different DingTalk account while an existing row is selected must create a new
    /// account, not overwrite the selected one (the old code always used the selected profile).
    /// </summary>
    [Fact]
    public void PickAuthorizedProfileDoesNotOverwriteTheSelectedAccount()
    {
        var t1 = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(8));
        var t2 = new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.FromHours(8));
        var before = new[]
        {
            new DwsProfileInfo("corp1:u1", "甲公司", "张三", IsCurrent: true, t1),
        };
        var after = new[]
        {
            new DwsProfileInfo("corp1:u1", "甲公司", "张三", IsCurrent: true, t1),
            new DwsProfileInfo("corp2:u2", "乙公司", "李四", IsCurrent: false, t2),
        };

        var picked = DingTalkDriveProvider.PickAuthorizedProfile(before, after, reauthProfile: "corp1:u1");

        Assert.Equal("corp2:u2", picked!.Profile);
    }

    [Fact]
    public void PickAuthorizedProfileFallsBackToCurrentWhenNothingMoved()
    {
        var profiles = new[]
        {
            new DwsProfileInfo("corp1:u1", "甲公司", "张三", IsCurrent: false, null),
            new DwsProfileInfo("corp2:u2", "乙公司", "李四", IsCurrent: true, null),
        };

        var picked = DingTalkDriveProvider.PickAuthorizedProfile(profiles, profiles, reauthProfile: null);

        Assert.Equal("corp2:u2", picked!.Profile);
    }

    /// <summary>
    /// Data isolation between accounts: every command for account A carries A's profile and
    /// account B's carries B's, so the CLI cannot fall back to "current" and mix drives.
    /// </summary>
    [Fact]
    public void EveryCommandCarriesTheAccountsOwnProfile()
    {
        var args = new[] { "drive", "+list" };

        var first = DwsCliRunner.BuildArguments(args, DwsRunOptions.Default, "corp1:u1");
        var second = DwsCliRunner.BuildArguments(args, DwsRunOptions.Default, "corp2:u2");

        Assert.Equal("corp1:u1", first[first.IndexOf("--profile") + 1]);
        Assert.Equal("corp2:u2", second[second.IndexOf("--profile") + 1]);
        Assert.Equal(new[] { "drive", "+list", "--format", "json", "--profile", "corp1:u1" }, first);
    }

    /// <summary>
    /// Reopening must resume the last connected account, not the first registered product.
    /// </summary>
    [Fact]
    public void WindowResumesTheLastConnectedAccount()
    {
        var catalog = new NetdiskProviderCatalog();
        catalog.Register(new StubProvider("alipan", "阿里云盘", "阿里云盘用户"));
        catalog.Register(new StubProvider("dingtalk", "钉钉云盘", "甲公司·张三"));
        var accounts = new ObservableCollection<NetdiskAccountState>
        {
            // DingTalk first, 阿里云盘 added later: the window must resume the remembered 钉钉
            // account instead of the most recent or first registered product.
            new("dingtalk", "甲公司·张三", new Dictionary<string, string>()),
            new("alipan", "阿里云盘（本机登录）", new Dictionary<string, string>()),
        };

        var vm = new NetdiskViewModel(catalog, accounts, _ => { }, new NullDialogs(), (_, _) => { },
            lastProviderId: "dingtalk", lastAccountName: "甲公司·张三");

        Assert.Equal("dingtalk", vm.SelectedProvider?.Id);
        Assert.Equal("甲公司·张三", vm.SelectedAccountName);
    }

    /// <summary>
    /// The reported bug: after a 钉钉 login the management window kept showing 阿里云盘. The auth
    /// panel now calls SelectAccountAsync, which must switch product + account and connect.
    /// </summary>
    [Fact]
    public async Task SelectAccountSwitchesProductAndConnects()
    {
        var catalog = new NetdiskProviderCatalog();
        catalog.Register(new StubProvider("alipan", "阿里云盘", "阿里云盘用户"));
        catalog.Register(new StubProvider("dingtalk", "钉钉云盘", "甲公司·张三"));
        var accounts = new ObservableCollection<NetdiskAccountState>
        {
            // 钉钉 saved first, 阿里云盘 later, so the window starts on 阿里云盘 — the exact
            // situation the switch has to correct after a 钉钉 login.
            new("dingtalk", "甲公司·张三", new Dictionary<string, string>
            {
                [DingTalkDriveProvider.CliLoginKey] = "true",
                [DingTalkDriveProvider.ProfileKey] = "corp1:u1",
            }),
            new("alipan", "阿里云盘（本机登录）", new Dictionary<string, string>()),
        };
        var vm = new NetdiskViewModel(catalog, accounts, _ => { }, new NullDialogs(), (_, _) => { });

        // The newest account is 阿里云盘, so that is where the window starts.
        Assert.Equal("alipan", vm.SelectedProvider?.Id);

        await vm.SelectAccountAsync("dingtalk", "甲公司·张三");

        Assert.Equal("dingtalk", vm.SelectedProvider?.Id);
        Assert.Equal("甲公司·张三", vm.SelectedAccountName);
        Assert.Contains("已连接", vm.StatusText);
        Assert.Contains("甲公司·张三", vm.StatusText);
    }

    private sealed class StubProvider(string id, string name, string accountName) : INetdiskProvider
    {
        public string Id => id;

        public string DisplayName => name;

        public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; } = Array.Empty<NetdiskCredentialField>();

        public INetdiskOAuthFlow? OAuth => null;

        public Task<INetdiskSession> ConnectAsync(NetdiskCredentials credentials, CancellationToken ct = default)
            => Task.FromResult<INetdiskSession>(new StubSession(accountName, credentials));
    }

    private sealed class StubSession(string accountName, NetdiskCredentials credentials) : INetdiskSession
    {
        public NetdiskAccountInfo Account { get; } = new("stub", accountName);

        public NetdiskCredentials CurrentCredentials { get; } = credentials;

        public IReadOnlyList<NetdiskDrive> Drives { get; } = new[] { new NetdiskDrive("d", "默认盘") };

        public string CurrentDriveId => "d";

        public Task SwitchDriveAsync(string driveId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<NetdiskQuota> GetQuotaAsync(CancellationToken ct = default) => Task.FromResult(new NetdiskQuota(1, 2));

        public Task<NetdiskPage> ListAsync(string parentFolderId, int pageSize = 100, string? cursor = null,
            CancellationToken ct = default) => Task.FromResult(new NetdiskPage(Array.Empty<NetdiskFile>(), null));

        public Task<NetdiskFile> CreateFolderAsync(string parentFolderId, string name, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(IReadOnlyList<string> fileIds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<NetdiskFile> UploadAsync(string parentFolderId, string localPath,
            IProgress<NetdiskTransferProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DownloadAsync(string fileId, string localPath,
            IProgress<NetdiskTransferProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullDialogs : IDialogService
    {
        public bool Confirm(string message, string title) => true;
        public bool ConfirmYesNo(string message, string title) => true;
        public void Info(string message, string title) { }
        public void Warn(string message, string title) { }
        public void Error(string message, string title) { }
    }

    [Fact]
    public void ProviderDeclaresCliOnlyLogin()
    {
        var provider = new DingTalkDriveProvider();

        Assert.Equal("dingtalk", provider.Id);
        Assert.Equal("钉钉云盘", provider.DisplayName);
        Assert.Empty(provider.CredentialFields);
        Assert.Null(provider.OAuth);
        Assert.True(provider.SupportsCliLogin);
    }

    [Fact]
    public void LocateExecutableHonoursEnvOverride()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"dws-fake-{Guid.NewGuid():N}", "dws.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        File.WriteAllText(fake, "not a real cli");
        try
        {
            Environment.SetEnvironmentVariable("WATERMARKREMOVER_DWS_CLI", fake);
            Assert.Equal(fake, DwsCliRunner.LocateExecutable());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WATERMARKREMOVER_DWS_CLI", null);
            Directory.Delete(Path.GetDirectoryName(fake)!, recursive: true);
        }
    }

    /// <summary>Progress sink that runs inline — Progress&lt;T&gt; would post and race the asserts.</summary>
    private sealed class SynchronousProgress(Action<NetdiskTransferProgress> handler) : IProgress<NetdiskTransferProgress>
    {
        public void Report(NetdiskTransferProgress value) => handler(value);
    }

    /// <summary>Answers invocations in order from expectation predicates.</summary>
    private sealed class FakeDswScript : DwsCliRunner
    {
        private readonly List<(Func<IReadOnlyList<string>, bool> Match, string Json)> _steps = new();
        private int _index;

        public FakeDswScript() : base("C:\\fake\\dws.exe")
        {
        }

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public List<DwsRunOptions?> Options { get; } = new();

        public FakeDswScript Expect(Func<IReadOnlyList<string>, bool> match, string json)
        {
            _steps.Add((match, json));
            return this;
        }

        public override Task<DwsCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            DwsRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            (Func<IReadOnlyList<string>, bool>, string)? step =
                _index < _steps.Count ? _steps[_index] : null;
            _index++;
            Calls.Add(arguments);
            Options.Add(options);
            if (step is null || !step.Value.Item1(arguments))
            {
                throw new InvalidOperationException(
                    $"Unexpected dws call #{_index}: {string.Join(' ', arguments)}");
            }

            return Task.FromResult(Ok(step.Value.Item2));
        }

        private static DwsCommandResult Ok(string json) => new(0, json, string.Empty);
    }
}
