using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Transfer;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The 手机传文件 chat rows: a file row carries the open/reveal split the page binds, and a
/// share-link row walks its own progress states (解析 → 下载 x/N → 就绪 / 失败) because that is
/// the only feedback the user gets while a 扫描全能王 link is pulled in the background.
/// </summary>
public class TransferChatItemViewModelTests
{
    private static TransferChatMessage Text(int id, string body, TransferChatDirection from) =>
        new(id, from, TransferChatKind.Text, body, string.Empty, 0, DateTime.Now);

    [Fact]
    public void A_file_row_formats_its_card_and_splits_open_from_reveal()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wr-chat-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "x");
        try
        {
            var row = new TransferChatItemViewModel(
                new TransferChatMessage(
                    1, TransferChatDirection.Phone, TransferChatKind.File, string.Empty,
                    "report.pdf", 2350, DateTime.Now),
                path);

            Assert.True(row.IsFile);
            Assert.False(row.IsMine);
            Assert.Equal("PDF", row.ExtensionText);
            Assert.Equal("2.3 KB", row.SizeText);
            Assert.True(row.HasPath);
            Assert.Equal(Path.GetDirectoryName(path), row.Directory);

            Assert.True(row.OpenCommand.CanExecute(null));
            Assert.True(row.OpenFolderCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_text_row_has_no_file_affordances()
    {
        var row = new TransferChatItemViewModel(Text(1, "hello", TransferChatDirection.Phone));

        Assert.True(row.IsText);
        Assert.False(row.IsFile);
        Assert.False(row.OpenCommand.CanExecute(null));
        Assert.False(row.OpenFolderCommand.CanExecute(null));
        Assert.False(row.HasPath);
    }

    [Fact]
    public void Text_and_link_rows_copy_through_the_owner_callback_file_rows_do_not()
    {
        // 复制入口曾随旧文字列表一起消失；钉死：文字与链接气泡有复制，文件气泡没有。
        var copied = new List<TransferChatItemViewModel>();
        var textRow = new TransferChatItemViewModel(
            Text(1, "hello", TransferChatDirection.Phone), copy: row => copied.Add(row));
        var linkRow = new TransferChatItemViewModel(
            new TransferChatMessage(
                2, TransferChatDirection.Computer, TransferChatKind.Link,
                "https://s.camscanner.com/x", "分享链接", 0, DateTime.Now),
            copy: row => copied.Add(row));
        var fileRow = new TransferChatItemViewModel(Text(3, "x", TransferChatDirection.Phone));

        Assert.True(textRow.HasCopy);
        Assert.True(textRow.CopyCommand.CanExecute(null));
        textRow.CopyCommand.Execute(null);
        Assert.Same(textRow, copied.Single());

        Assert.True(linkRow.HasCopy);
        Assert.True(linkRow.CopyCommand.CanExecute(null));

        Assert.False(fileRow.HasCopy);
        Assert.False(fileRow.CopyCommand.CanExecute(null));
    }

    [Fact]
    public void A_link_row_starts_busy_and_marches_until_the_page_count_is_known()
    {
        var row = new TransferChatItemViewModel(
            new TransferChatMessage(
                1, TransferChatDirection.Computer, TransferChatKind.Link,
                "https://example.com/s/abc", "分享链接", 0, DateTime.Now));

        Assert.True(row.IsLink);
        Assert.True(row.LinkBusy);
        Assert.True(row.LinkProgressIndeterminate);
        Assert.False(row.LinkReady);
        Assert.False(row.OpenPdfCommand.CanExecute(null));
        Assert.Equal("正在解析链接…", row.LinkStateText);

        row.ReportLinkTitle("扫描全能王 2026-9-30");
        Assert.True(row.HasLinkTitle);

        row.ReportLinkStage("正在下载第 2/5 页…", 0.4);
        Assert.False(row.LinkProgressIndeterminate);
        Assert.Equal(0.4, row.LinkProgress);

        row.ReportLinkReady("已下载 5 张图片，可合并后处理");
        Assert.False(row.LinkBusy);
        Assert.True(row.LinkReady);
        Assert.Equal(1, row.LinkProgress);
        Assert.True(row.OpenPdfCommand.CanExecute(null));
    }

    [Fact]
    public void A_failed_link_row_keeps_the_button_disabled_and_explains_why()
    {
        var row = new TransferChatItemViewModel(
            new TransferChatMessage(
                1, TransferChatDirection.Computer, TransferChatKind.Link,
                "https://example.com/s/abc", "分享链接", 0, DateTime.Now));

        row.ReportLinkStage("正在下载第 1/5 页…", 0.2);
        row.ReportLinkFailure("下载失败：链接已过期");

        Assert.True(row.LinkFailed);
        Assert.False(row.LinkBusy);
        Assert.False(row.LinkReady);
        Assert.False(row.OpenPdfCommand.CanExecute(null));
        Assert.Contains("链接已过期", row.LinkStateText);
    }
}
