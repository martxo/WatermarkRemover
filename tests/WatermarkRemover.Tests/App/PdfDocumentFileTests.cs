using System.IO;
using WatermarkRemover.Pdf.Preview;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 改名跟踪（指纹找回）的行为契约。生产事故：扫描完成后 200ms 内后台命名改名，预览会话
/// 还指着旧路径；找回逻辑靠「读候选文件头哈希」确认，而看图窗口把新文件以 FileShare.None
/// 独占时读不到 → 「候选 0 个」→ 预览条和大图（共用会话）永远卡在死路径上。
/// </summary>
public class PdfDocumentFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-relocate-" + Guid.NewGuid().ToString("N"));

    public PdfDocumentFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string WritePdf(string name, int length)
    {
        string path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        byte[] header = "%PDF-1.7\n"u8.ToArray();
        stream.Write(header);
        var body = new byte[length - header.Length];
        Random.Shared.NextBytes(body);
        stream.Write(body);
        return path;
    }

    [Fact]
    public void A_plain_rename_is_tracked_by_the_stamp_and_head_hash()
    {
        string original = WritePdf("扫描0001.pdf", 704_512);
        var file = new PdfDocumentFile(original);
        file.RememberStamp();

        string renamed = Path.Combine(_dir, "蒙金华 (2).pdf");
        File.Move(original, renamed);

        Assert.True(file.TryRelocate());
        Assert.Equal(renamed, file.Path);
        Assert.True(file.Exists);
    }

    [Fact]
    public void A_candidate_held_without_read_sharing_is_still_tracked_when_unique()
    {
        // 事故场景：唯一候选被看图窗口 FileShare.None 独占，头哈希读不到——不能因此放弃。
        string original = WritePdf("扫描0002.pdf", 704_512);
        var file = new PdfDocumentFile(original);
        file.RememberStamp();

        string renamed = Path.Combine(_dir, "蒙金华 (3).pdf");
        File.Move(original, renamed);

        using (new FileStream(renamed, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(file.TryRelocate());
        }

        Assert.Equal(renamed, file.Path);
    }

    [Fact]
    public void Two_same_stamp_candidates_with_unreadable_heads_are_refused()
    {
        // 尺寸+创建时间相同、头都读不到 → 无法唯一确定，绝不猜（可能改错文件）。
        string original = WritePdf("扫描0003.pdf", 999_424);
        var file = new PdfDocumentFile(original);
        file.RememberStamp();

        DateTime stamp = File.GetCreationTimeUtc(original);
        string twinA = Path.Combine(_dir, "同形状 A.pdf");
        string twinB = Path.Combine(_dir, "同形状 B.pdf");
        File.Move(original, twinA);            // 改名保留创建时间
        WritePdf("同形状 B.pdf", 999_424);     // 同长度
        File.SetCreationTimeUtc(twinB, stamp); // 同创建时间、不同内容
        File.SetCreationTimeUtc(twinA, stamp);

        using (new FileStream(twinA, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(twinB, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(file.TryRelocate());
        }

        Assert.Equal(original, file.Path);
    }

    [Fact]
    public void A_readable_candidate_with_a_different_head_hash_is_rejected()
    {
        // 尺寸+创建时间撞上了、但内容不同 → 不是同一个文件，拒绝。
        string original = WritePdf("扫描0004.pdf", 555_555);
        var file = new PdfDocumentFile(original);
        file.RememberStamp();

        DateTime stamp = File.GetCreationTimeUtc(original);
        string impostor = Path.Combine(_dir, "冒名.pdf");
        WritePdf("冒名.pdf", 555_555);
        File.SetCreationTimeUtc(impostor, stamp);
        File.Delete(original);

        Assert.False(file.TryRelocate());
    }
}
