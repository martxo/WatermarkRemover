using System.IO;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using SkiaSharp;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.Pdf.Rendering;

/// <summary>
/// Renders PDF pages with PDFium (via Docnet.Core) instead of the WinRT Windows.Data.Pdf API.
/// PDFium ships inside the app and needs no OS component, so rendering keeps working on machines
/// where the WinRT PDF stack is missing or unregistered (Windows N / LTSC / stripped systems) —
/// the failure that surfaced as REGDB_E_CLASSNOTREG (0x80040154). Output is PNG, the format every
/// OCR engine accepts.
///
/// This lives in the PDF library (not in the WPF app) so any consumer — OCR, naming, previews,
/// or another project — gets the same serialised pdfium access without copying this logic.
/// </summary>
public sealed class PdfiumPdfPageRenderer : IPdfPageRenderer
{
    /// <summary>
    /// OCR engines reject very large images and larger gains nothing for text recognition, so
    /// rendered pages are capped on the long side (same cap the previous WinRT renderer used).
    /// </summary>
    private const int MaxImageSide = 2400;

    /// <summary>PDF points per inch; converting a requested DPI to a PDFium scale factor.</summary>
    private const double PointsPerInch = 72.0;

    /// <summary>Reading the page count needs a document reader, not an image — any valid scale works.</summary>
    private const int MetadataDpi = 96;

    /// <summary>White background for transparent page areas — OCR must see black text on white.</summary>
    private static readonly IImageBytesConverter WhiteBackground = new NaiveTransparencyRemover(255, 255, 255);

    /// <summary>
    /// Docnet's <see cref="DocLib"/> instance is a process-wide singleton and NOT thread-safe:
    /// overlapping readers (one scan's naming chain still rendering while the next scan's starts)
    /// make pdfium cross pages — scan B rendered scan A's pixels, OCR'd A's content and inherited
    /// A's content-based name. Every pdfium touch (open → render → dispose) serialises here.
    /// </summary>
    private static readonly PriorityGate PdfiumGate = new();

    public async Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi)
    {
        ValidateDpi(dpi);
        return await Task.Run<IReadOnlyList<byte[]>>(async () =>
        {
            await PdfiumGate.WaitAsync(priority: true).ConfigureAwait(false);
            try
            {
                using IDocReader reader = Open(pdfPath, dpi);
                var results = new List<byte[]>(reader.GetPageCount());
                for (int i = 0; i < reader.GetPageCount(); i++)
                    results.Add(RenderPage(reader.GetPageReader(i)));
                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Wrap(pdfPath, ex);
            }
            finally
            {
                PdfiumGate.Release();
            }
        }).ConfigureAwait(false);
    }

    public async Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi)
    {
        ValidateDpi(dpi);
        return await Task.Run(async () =>
        {
            await PdfiumGate.WaitAsync(priority: true).ConfigureAwait(false);
            try
            {
                using IDocReader reader = Open(pdfPath, dpi);
                if (reader.GetPageCount() == 0)
                    throw new InvalidOperationException("文档没有可渲染的页面。");
                return RenderPage(reader.GetPageReader(0));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw Wrap(pdfPath, ex);
            }
            finally
            {
                PdfiumGate.Release();
            }
        }).ConfigureAwait(false);
    }

    public async Task<int> GetPageCountAsync(string pdfPath)
    {
        await PdfiumGate.WaitAsync(priority: true).ConfigureAwait(false);
        try
        {
            using IDocReader reader = Open(pdfPath, MetadataDpi);
            return reader.GetPageCount();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Wrap(pdfPath, ex);
        }
        finally
        {
            PdfiumGate.Release();
        }
    }

    public async Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false)
    {
        ValidateDpi(dpi);
        return await Task.Run(async () =>
        {
            await PdfiumGate.WaitAsync(priority).ConfigureAwait(false);
            try
            {
                using IDocReader reader = Open(pdfPath, dpi);
                int pageCount = reader.GetPageCount();
                if (pageIndex < 0 || pageIndex >= pageCount)
                    throw new ArgumentOutOfRangeException(nameof(pageIndex),
                        $"页码超出范围：第 {pageIndex + 1} 页（共 {pageCount} 页）。");
                return RenderPage(reader.GetPageReader(pageIndex));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentOutOfRangeException)
            {
                throw Wrap(pdfPath, ex);
            }
            finally
            {
                PdfiumGate.Release();
            }
        }).ConfigureAwait(false);
    }

    private static IDocReader Open(string pdfPath, int dpi) =>
        DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(dpi / PointsPerInch));

    /// <summary>
    /// Renders one page to BGRA bytes (transparent background flattened onto white) and encodes it
    /// as PNG, downscaling proportionally when a side exceeds <see cref="MaxImageSide"/>.
    /// </summary>
    private static byte[] RenderPage(IPageReader page)
    {
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("页面渲染尺寸无效。");

        using var bitmap = DecodeBitmap(page.GetImage(WhiteBackground), width, height);

        int longestSide = Math.Max(bitmap.Width, bitmap.Height);
        if (longestSide > MaxImageSide)
        {
            using SKBitmap scaled = Downscale(bitmap, MaxImageSide / (double)longestSide);
            return EncodePng(scaled);
        }

        return EncodePng(bitmap);
    }

    /// <summary>Wraps raw BGRA bytes (as PDFium returns them) in an Skia bitmap.</summary>
    private static SKBitmap DecodeBitmap(byte[] pixels, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    private static SKBitmap Downscale(SKBitmap source, double factor)
    {
        int w = Math.Max(1, (int)Math.Round(source.Width * factor));
        int h = Math.Max(1, (int)Math.Round(source.Height * factor));
        var target = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        if (!source.ScalePixels(target, new SKSamplingOptions(SKCubicResampler.Mitchell)))
        {
            target.Dispose();
            throw new InvalidOperationException("页面缩放失败。");
        }
        return target;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void ValidateDpi(int dpi)
    {
        if (dpi is < 72 or > 600)
            throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be between 72 and 600.");
    }

    private static Exception Wrap(string pdfPath, Exception ex) =>
        new InvalidOperationException($"渲染 PDF 页面失败：{Path.GetFileName(pdfPath)}\n{ex.Message}", ex);
}

/// <summary>
/// Serialisation gate with two priority classes. The pdfium singleton must serialise every
/// touch, but a plain FIFO let the strip's tile fills (N pages back-to-back) head-block the
/// scan post-process — blank-page scan and orientation probe queued behind every tile, which
/// read as "缩略图全出来了才开始调正". High-priority waiters (probes, blank scans, the on-screen
/// canvas) jump the queue: a pending treatment render starts after the render currently in
/// flight, tiles only when no treatment work is waiting.
/// </summary>
internal sealed class PriorityGate
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource> _high = new();
    private readonly Queue<TaskCompletionSource> _low = new();

    /// <summary>Takes the slot instantly when idle, otherwise queues behind same/higher priority.</summary>
    public Task WaitAsync(bool priority)
    {
        lock (_sync)
        {
            if (_high.Count == 0 && _low.Count == 0 && _slot.CurrentCount > 0)
            {
                _slot.Wait(0); // guaranteed by CurrentCount under the lock
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (priority ? _high : _low).Enqueue(waiter);
            return waiter.Task;
        }
    }

    /// <summary>Hands the slot to the oldest high-priority waiter, else low, else frees it.</summary>
    public void Release()
    {
        TaskCompletionSource? next;
        lock (_sync)
        {
            next = _high.Count > 0 ? _high.Dequeue()
                 : _low.Count > 0 ? _low.Dequeue()
                 : null;
        }

        if (next is not null) next.SetResult();
        else _slot.Release();
    }
}
