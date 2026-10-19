using PdfSharpCore.Pdf.IO;
using SkiaSharp;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Rename tracking: the strip keeps the path it was opened with, but the user is free to
/// rename the file while it is open — every edit then hit a dead path and the buttons felt
/// dead. The strip heals itself by matching the remembered file stamp (size + creation time,
/// both rename-proof) against its directory, and tells the host so the row follows the file.
/// </summary>
public class PdfRenameTrackingTests
{
    private sealed class FakeRenderer(int pageCount) : IPdfPageRenderer
    {
        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi)
        {
            // A real renderer (PDFium) would throw its own flavour of failure for a missing
            // file; what matters to the strip is that the file is not where it remembers.
            if (!File.Exists(pdfPath))
                throw new FileNotFoundException("渲染失败：文件不存在", pdfPath);

            var info = new SKImageInfo(40, 30, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]>(data.ToArray());
        }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new NotSupportedException();
    }

    private static async Task DrainAsync(PdfPreviewViewModel vm)
    {
        for (int i = 0; i < 300 && (vm.IsLoading || vm.IsRotating || vm.Thumbnails.Any(t => t.Image is null)); i++)
            await Task.Delay(10);
    }

    private static int RotationOf(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.InformationOnly);
        return doc.Pages[0].Rotate;
    }

    [Fact]
    public async Task A_renamed_file_is_tracked_and_the_edit_lands_on_the_new_name()
    {
        string pdf = Path.Combine(Path.GetTempPath(), "wm-rename-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());
        string renamed = Path.ChangeExtension(pdf, ".renamed.pdf");
        try
        {
            string? hostNotified = null;
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1),
                onRelocated: p => hostNotified = p);
            await vm.LoadAsync(); // first successful read remembers the stamp
            await DrainAsync(vm);

            File.Move(pdf, renamed); // the user renames the file behind the app's back

            await vm.RotatePageAsync(0);
            await DrainAsync(vm);

            Assert.Equal(string.Empty, vm.StatusText); // the edit went through silently
            Assert.Equal(90, RotationOf(renamed));     // …and landed on the renamed file
            Assert.Equal(renamed, hostNotified);       // the host was told where the file went
            Assert.Equal(Path.GetFileName(renamed), vm.Title);
        }
        finally
        {
            File.Delete(pdf);
            File.Delete(renamed);
        }
    }

    [Fact]
    public async Task A_deleted_file_reports_clearly_instead_of_failing_silently()
    {
        string pdf = Path.Combine(Path.GetTempPath(), "wm-rename-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(pdf, PdfFixture.CamScannerLike().Build());
        try
        {
            var vm = new PdfPreviewViewModel(pdf, "scan.pdf", new FakeRenderer(1));
            await vm.LoadAsync();
            await DrainAsync(vm);

            File.Delete(pdf); // gone for good — no candidate can match

            await vm.RotatePageAsync(0);
            await DrainAsync(vm);

            Assert.Equal("文件已被移动或重命名，且无法自动找回。", vm.StatusText);
        }
        finally
        {
            File.Delete(pdf);
        }
    }
}
