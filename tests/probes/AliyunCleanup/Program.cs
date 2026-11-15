// Removes the software's entire presence from 阿里云盘 (the 资源库 drive).
//
//   AliyunCleanup list      — inventory: the /WatermarkRemover tree, file counts and bytes
//   AliyunCleanup delete    — delete the whole /WatermarkRemover folder (drive recycle bin)
//
// What lives under /WatermarkRemover and what deleting it costs:
//   latest.json / versions.json / history/ / files/   the cloud update channel. After deletion
//                                                   clients fail the cloud check and fall back
//                                                   to GitHub Releases (already published).
//   portable/                                        the green (unzip-and-run) build for testers.
//   private/                                         an encrypted backup of the netdisk login
//                                                   tokens (recoverable only with its passphrase).
//
// The drive's delete moves everything to the recycle bin — not permanent until the bin is
// emptied, so a mistake here is recoverable from the Aliyun web/客户端 回收站.

using System.Globalization;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;

if (args.Length != 1 || (args[0] != "list" && args[0] != "delete"))
{
    Console.Error.WriteLine("usage: AliyunCleanup list|delete");
    return 2;
}

bool delete = args[0] == "delete";
var ct = CancellationToken.None;

string cliPath = AliyunpanCliRuntime.LocateExecutable()
    ?? throw new InvalidOperationException("未找到 aliyunpan 命令行工具，请先安装并登录。");
if (!AliyunpanCliRuntime.TryReadConfig(AliyunpanCliRuntime.ConfigPathFor(cliPath), out string? deviceId, out var token) ||
    string.IsNullOrEmpty(token.AccessToken))
{
    throw new InvalidOperationException("读取 aliyunpan 配置失败，请先在命令行执行 aliyunpan login。");
}

var values = new Dictionary<string, string>(StringComparer.Ordinal)
{
    [AliyunpanCliRuntime.CliLoginKey] = "true",
    [AliyunpanCliRuntime.CliDeviceIdKey] = deviceId ?? string.Empty,
    [AliyunTokenService.AccessTokenKey] = token.AccessToken,
    [AliyunTokenService.ExpiresAtKey] = token.ExpiredAt.ToString("O", CultureInfo.InvariantCulture),
};

await using var session = await new AliyunDriveProvider().ConnectAsync(new NetdiskCredentials(values));
NetdiskDrive drive = session.Drives.FirstOrDefault(d => d.Name == "资源库")
    ?? throw new InvalidOperationException("账号没有「资源库」 drive。");
await session.SwitchDriveAsync(drive.Id, ct);

string? rootId = await session.FindFolderAsync("/WatermarkRemover", ct);
if (rootId is null)
{
    Console.WriteLine("阿里云盘上不存在 /WatermarkRemover，无需清理。");
    return 0;
}

// ── inventory ────────────────────────────────────────────────────────────────────
long totalBytes = 0;
int totalFiles = 0, totalFolders = 0;

async Task WalkAsync(string folderId, string prefix, int depth)
{
    if (depth > 3)
    {
        Console.WriteLine($"{prefix}…（超过 3 层，折叠）");
        return;
    }

    foreach (NetdiskFile item in await session.ListAllAsync(folderId, ct))
    {
        if (item.IsFolder)
        {
            totalFolders++;
            Console.WriteLine($"{prefix}{item.Name}/");
            await WalkAsync(item.Id, prefix + "  ", depth + 1);
        }
        else
        {
            totalFiles++;
            totalBytes += item.Size;
            // files/ holds ~1000 content-addressed chunks — one summary line instead of 1000.
            if (totalFiles <= 20 || !item.Name.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                Console.WriteLine($"{prefix}{item.Name}  {item.Size / 1024.0:N1} KB");
            }
            else if (totalFiles == 21)
            {
                Console.WriteLine($"{prefix}…（其余为 files/ 内容寻址分块，省略逐条列出）");
            }
        }
    }
}

Console.WriteLine($"/WatermarkRemover（drive「{drive.Name}」）");
await WalkAsync(rootId, "  ", 0);
Console.WriteLine();
Console.WriteLine($"合计：{totalFolders} 个文件夹，{totalFiles} 个文件，{totalBytes / 1048576.0:F1} MB");

// ── delete ───────────────────────────────────────────────────────────────────────
if (!delete)
{
    return 0;
}

Console.WriteLine();
Console.WriteLine("正在删除 /WatermarkRemover（进回收站）…");
await session.DeleteAsync(new[] { rootId }, ct);

string? after = await session.FindFolderAsync("/WatermarkRemover", ct);
Console.WriteLine(after is null
    ? "已删除：/WatermarkRemover 不再存在（回收站可恢复）。"
    : "警告：删除后目录仍然存在，请手动检查。");
return after is null ? 0 : 1;
