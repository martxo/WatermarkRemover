// Assembles the Tencent Cloud update mirror's repository from LOCAL material only.
//
// Why this exists: the publisher's --seed-mirror path first downloads the whole 阿里云盘
// repository, and that download is currently hard-blocked by OSS ExceedMaxConcurrency 403s
// on this network. Everything the mirror actually needs is available locally:
//   • every chunk of the latest manifest can be re-sliced from publish-staging with the same
//     ContentChunker the publisher used (chunks are content-addressed; sha256 is verified);
//   • the local seed directory still holds the older manifests (history 148-153) and the
//     historical chunks (rollback coverage);
//   • latest.json / versions.json / history 153-155 are fetched from the mirror itself.
//
//   MirrorBackfill assemble <stagingDir> <seedDir> <manifestFile> <mirrorFilesDir> <outDir>
//
// Output layout under <outDir>:
//   core/    latest.json, versions.json, history/*.json, files/<sha256> — the complete chunk
//            set of the latest manifest plus every history manifest. Push this first: it makes
//            every forward update (any client version → latest) fully mirror-served.
//   legacy/  historical chunks that the latest manifest no longer references (rollback only).
//            Optional to push; the client falls back to 阿里云盘 per chunk when one is absent.

using System.Security.Cryptography;
using WatermarkRemover.Core.Updates;

if (args.Length != 6 || args[0] != "assemble")
{
    Console.Error.WriteLine(
        "usage: MirrorBackfill assemble <stagingDir> <seedDir> <manifestFile> <mirrorFilesDir> <outDir>");
    return 2;
}

string stagingDir = args[1];
string seedDir = args[2];
string manifestFile = args[3];
string mirrorFilesDir = args[4];
string outDir = args[5];

string coreDir = Path.Combine(outDir, "core");
string legacyDir = Path.Combine(outDir, "legacy");
string coreFiles = Path.Combine(coreDir, CloudUpdateFormat.ChunksFolder);
string coreHistory = Path.Combine(coreDir, CloudUpdateFormat.HistoryFolder);
string legacyFiles = Path.Combine(legacyDir, CloudUpdateFormat.ChunksFolder);
string seedFiles = Path.Combine(seedDir, CloudUpdateFormat.ChunksFolder);
string seedHistory = Path.Combine(seedDir, CloudUpdateFormat.HistoryFolder);

Directory.CreateDirectory(coreFiles);
Directory.CreateDirectory(coreHistory);
Directory.CreateDirectory(legacyFiles);

// ── the latest manifest: the authoritative chunk list of the forward path ──────────────
CloudManifest? manifest = CloudUpdateFormat.TryParse<CloudManifest>(await File.ReadAllTextAsync(manifestFile));
if (manifest is null || string.IsNullOrEmpty(manifest.Version))
{
    Console.Error.WriteLine($"清单解析失败：{manifestFile}");
    return 2;
}

var wanted = new Dictionary<string, int>(StringComparer.Ordinal);
foreach (CloudManifestFile file in manifest.Files)
{
    foreach (CloudChunkRef chunk in file.Chunks)
    {
        wanted.TryAdd(chunk.Sha256, chunk.Size);
    }
}

Console.WriteLine($"清单 v{manifest.Version}：{manifest.Files.Count} 个文件，{wanted.Count} 个唯一分块");

// ── slice sources: re-chunk the staging payload exactly like the publisher did ─────────
var slices = new Dictionary<string, (string Path, int Offset, int Length)>(StringComparer.Ordinal);
foreach (string path in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
{
    byte[] bytes = await File.ReadAllBytesAsync(path);
    foreach (ContentChunk chunk in ContentChunker.Split(bytes))
    {
        if (wanted.ContainsKey(chunk.Sha256))
        {
            slices.TryAdd(chunk.Sha256, (path, chunk.Offset, chunk.Length));
        }
    }
}

Console.WriteLine($"staging 可切片分块：{slices.Count} / {wanted.Count}");

// ── core: every chunk of the latest manifest (seed copy if it verifies, else slice) ────
int fromSeed = 0, sliced = 0, failed = 0;
foreach ((string sha, int size) in wanted)
{
    string target = Path.Combine(coreFiles, sha);
    if (File.Exists(target)) continue; // 重跑幂等

    string seedCopy = Path.Combine(seedFiles, sha);
    if (File.Exists(seedCopy) && new FileInfo(seedCopy).Length == size &&
        await HashEqualsAsync(seedCopy, sha))
    {
        File.Copy(seedCopy, target);
        fromSeed++;
        continue;
    }

    if (!slices.TryGetValue(sha, out var slice))
    {
        Console.Error.WriteLine($"致命：分块 {sha} 在 staging 中找不到（清单与发布目录不一致）");
        failed++;
        continue;
    }

    await using (FileStream source = File.OpenRead(slice.Path))
    {
        source.Seek(slice.Offset, SeekOrigin.Begin);
        byte[] buffer = new byte[slice.Length];
        int read = await source.ReadAsync(buffer.AsMemory(0, slice.Length));
        if (read != slice.Length)
        {
            Console.Error.WriteLine($"致命：分块 {sha} 读取不完整（{read}/{slice.Length}）");
            failed++;
            continue;
        }

        string actual = Convert.ToHexString(SHA256.HashData(buffer)).ToLowerInvariant();
        if (!string.Equals(actual, sha, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"致命：分块 {sha} 切片校验失败（{actual}）");
            failed++;
            continue;
        }

        await File.WriteAllBytesAsync(target, buffer);
        sliced++;
    }
}

Console.WriteLine($"core 分块：来自 seed {fromSeed}，本地切片 {sliced}，失败 {failed}");

// ── legacy: historical chunks no longer referenced by the latest manifest (rollback) ──
int legacyOk = 0, legacyBad = 0;
foreach (string path in Directory.EnumerateFiles(seedFiles))
{
    string sha = Path.GetFileName(path);
    if (wanted.ContainsKey(sha)) continue; // 已在 core
    if (!await HashEqualsAsync(path, sha))
    {
        Console.Error.WriteLine($"警告：seed 分块 {sha} 校验失败，跳过（回滚时可回退云盘）");
        legacyBad++;
        continue;
    }

    File.Copy(path, Path.Combine(legacyFiles, sha));
    legacyOk++;
}

Console.WriteLine($"legacy 分块：可用 {legacyOk}，损坏跳过 {legacyBad}");

// ── manifests: mirror's own latest/versions + 153-155, seed's 148-152 ─────────────────
File.Copy(Path.Combine(mirrorFilesDir, CloudUpdateFormat.LatestFile),
    Path.Combine(coreDir, CloudUpdateFormat.LatestFile), overwrite: true);
File.Copy(Path.Combine(mirrorFilesDir, CloudUpdateFormat.VersionsFile),
    Path.Combine(coreDir, CloudUpdateFormat.VersionsFile), overwrite: true);

int manifestsOk = 0;
foreach (string path in Directory.EnumerateFiles(mirrorFilesDir, "*.json", SearchOption.AllDirectories))
{
    string relative = Path.GetRelativePath(mirrorFilesDir, path);
    string target = Path.Combine(coreDir, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(path, target, overwrite: true);
    manifestsOk++;
}

foreach (string path in Directory.EnumerateFiles(seedHistory, "*.json"))
{
    string name = Path.GetFileName(path);
    if (File.Exists(Path.Combine(coreHistory, name))) continue; // 镜像已有的版本以镜像为准
    CloudManifest? history = CloudUpdateFormat.TryParse<CloudManifest>(await File.ReadAllTextAsync(path));
    string expect = Path.GetFileNameWithoutExtension(name);
    if (history is null || !string.Equals(history.Version, expect, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"警告：seed 清单 {name} 解析失败或版本不符，跳过");
        continue;
    }

    File.Copy(path, Path.Combine(coreHistory, name));
    manifestsOk++;
}

Console.WriteLine($"清单：{manifestsOk} 份就位（core/history 现有 {Directory.GetFiles(coreHistory).Length} 份）");

long coreBytes = DirBytes(coreDir), legacyBytes = DirBytes(legacyDir);
Console.WriteLine($"core 合计 {coreBytes / 1048576.0:F1} MB，legacy 合计 {legacyBytes / 1048576.0:F1} MB");
Console.WriteLine(failed == 0 ? "组装成功" : $"组装失败（{failed} 个分块缺失）");
return failed == 0 ? 0 : 1;

static async Task<bool> HashEqualsAsync(string path, string sha)
{
    await using FileStream stream = File.OpenRead(path);
    byte[] hash = await SHA256.HashDataAsync(stream);
    return string.Equals(Convert.ToHexString(hash).ToLowerInvariant(), sha, StringComparison.Ordinal);
}

static long DirBytes(string dir) =>
    Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
        : 0;
