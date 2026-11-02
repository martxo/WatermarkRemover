using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 「等待预览稳定」必须把缩略图填充也算进去：条带每画一张缩略图都会短暂打开 PDF，改名
/// （File.Move）撞上这个读句柄时 Windows 会拒绝，结果是名字已经识别对了却退回时间戳命名。
/// 2026-11-02 的实测日志就是这个形态（命名落盘 IOException，紧挨着 19 张缩略图渲染）。
/// </summary>
public class PdfSettleWaitTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-settle-" + Guid.NewGuid().ToString("N"));

    public PdfSettleWaitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>每页渲染都慢一拍，保证「填充还没结束」的窗口足够长，断言不会碰运气。</summary>
    private sealed class SlowRenderer(int pageCount, int delayMs) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public async Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false)
        {
            await Task.Delay(delayMs);
            var info = new SKImageInfo(30, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    private string WritePdf(int pages)
    {
        string path = Path.Combine(_dir, "scan.pdf");
        using (var doc = new PdfDocument())
        {
            for (int i = 0; i < pages; i++)
            {
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, 100, 140));
            }
            doc.Save(path);
        }
        return path;
    }

    [Fact]
    public async Task WaitForSettled_returns_only_after_the_thumbnail_fill_stops()
    {
        string pdf = WritePdf(10);
        var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new SlowRenderer(10, 80));
        try
        {
            await vm.LoadAsync();

            // 载入后条带开始逐页渲染缩略图；每一张的渲染都短暂打开 PDF。
            await vm.WaitForSettledAsync();

            // 填充结束才算稳定——否则改名会撞上正在读取的缩略图渲染。
            Assert.All(vm.Thumbnails, tile => Assert.NotNull(tile.Image));

            // 稳定之后文件确实可以改名：自动命名只有在这一步能拿回真正的名字。
            string renamed = Path.Combine(_dir, "named.pdf");
            File.Move(pdf, renamed);
        }
        finally
        {
            File.Delete(pdf);
        }
    }
}
