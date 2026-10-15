using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// Renders PDF pages with the Windows.Data.Pdf API built into Windows 10+ — no extra dependency.
/// Output is PNG, the format every OCR engine accepts. Swap for a PDFium implementation if the
/// system renderer ever proves insufficient.
/// </summary>
public sealed class WinRtPdfPageRenderer : IPdfPageRenderer
{
    /// <summary>
    /// The system OCR engine rejects images larger than OcrEngine.MaxImageDimension, so pages are
    /// capped here regardless of the requested DPI.
    /// </summary>
    private const uint MaxImageSide = 2400;

    public async Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi)
    {
        ValidateDpi(dpi);

        StorageFile file = await StorageFile.GetFileFromPathAsync(pdfPath);
        PdfDocument document = await PdfDocument.LoadFromFileAsync(file);

        var results = new List<byte[]>((int)document.PageCount);
        for (uint i = 0; i < document.PageCount; i++)
        {
            using PdfPage page = document.GetPage(i);
            results.Add(await RenderPageAsync(page, dpi));
        }

        return results;
    }

    public async Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi)
    {
        ValidateDpi(dpi);

        StorageFile file = await StorageFile.GetFileFromPathAsync(pdfPath);
        PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
        if (document.PageCount == 0)
            throw new InvalidOperationException("文档没有可渲染的页面。");

        using PdfPage page = document.GetPage(0);
        return await RenderPageAsync(page, dpi);
    }

    private static void ValidateDpi(int dpi)
    {
        if (dpi is < 72 or > 600)
            throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be between 72 and 600.");
    }

    private static async Task<byte[]> RenderPageAsync(PdfPage page, int dpi)
    {
        // Scale to the requested DPI, then shrink proportionally when a side exceeds the cap.
        double scale = dpi / 72.0;
        double width = page.Size.Width * scale;
        double height = page.Size.Height * scale;
        if (Math.Max(width, height) > MaxImageSide)
        {
            double shrink = MaxImageSide / Math.Max(width, height);
            width *= shrink;
            height *= shrink;
        }

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height)),
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        return await ReEncodeAsPngAsync(stream);
    }

    /// <summary>RenderToStreamAsync writes a raw bitmap; normalise it to PNG with correct metadata.</summary>
    private static async Task<byte[]> ReEncodeAsPngAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync();

        stream.Size = 0;
        stream.Seek(0);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            decoder.BitmapPixelFormat,
            decoder.BitmapAlphaMode,
            decoder.OrientedPixelWidth,
            decoder.OrientedPixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels.DetachPixelData());
        await encoder.FlushAsync();

        stream.Seek(0);
        return await ReadAllBytesAsync(stream);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
