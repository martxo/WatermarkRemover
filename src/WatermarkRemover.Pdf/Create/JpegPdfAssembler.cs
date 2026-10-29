using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Assembles JPEG pages into a PDF with no decode/re-encode round trip: each JPEG stream is
/// embedded verbatim as a /DCTDecode image XObject — the same hand-written XObject route
/// <see cref="PdfDocumentBuilder"/> uses for CCITT fax pages — so the bytes the server sent are
/// the bytes that land in the file. Page geometry comes from the JPEG's own pixels at
/// <see cref="DefaultDpi"/>; a SOF-marker walk supplies the dimensions without a full decode.
/// </summary>
public static class JpegPdfAssembler
{
    /// <summary>Assumed resolution of the embedded pages; 2480 px ≈ A4 at this size.</summary>
    private const double DefaultDpi = 300;

    /// <param name="title">Document metadata title (may be empty).</param>
    /// <param name="jpegPages">One complete JPEG stream per page, in page order.</param>
    public static byte[] Build(string title, IReadOnlyList<byte[]> jpegPages)
    {
        ArgumentNullException.ThrowIfNull(jpegPages);
        if (jpegPages.Count == 0) throw new ArgumentException("没有可写入的页面", nameof(jpegPages));

        PdfSharpRuntime.EnsureInitialized();

        using var pdf = new PdfDocument();
        pdf.Info.Title = title ?? string.Empty;

        foreach (var jpeg in jpegPages)
        {
            var (widthPx, heightPx, components) = ReadJpegHeader(jpeg);
            var colorSpace = components switch
            {
                1 => "/DeviceGray",
                3 => "/DeviceRGB",
                _ => throw new InvalidOperationException($"不支持的 JPEG 颜色分量数：{components}"),
            };
            double widthPt = widthPx * 72.0 / DefaultDpi;
            double heightPt = heightPx * 72.0 / DefaultDpi;

            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(widthPt);
            page.Height = XUnit.FromPoint(heightPt);

            var imageDict = new PdfDictionary(pdf);
            imageDict.Elements["/Type"] = new PdfName("/XObject");
            imageDict.Elements["/Subtype"] = new PdfName("/Image");
            imageDict.Elements["/Width"] = new PdfInteger(widthPx);
            imageDict.Elements["/Height"] = new PdfInteger(heightPx);
            imageDict.Elements["/ColorSpace"] = new PdfName(colorSpace);
            imageDict.Elements["/BitsPerComponent"] = new PdfInteger(8);
            imageDict.Elements["/Filter"] = new PdfName("/DCTDecode");
            imageDict.CreateStream(jpeg);
            pdf.Internals.AddObject(imageDict);

            var xobjects = new PdfDictionary(pdf);
            xobjects.Elements["/Im0"] = imageDict.Reference;
            page.Resources.Elements["/XObject"] = xobjects;

            var contentDict = new PdfDictionary(pdf);
            contentDict.CreateStream(
                Encoding.ASCII.GetBytes(
                    FormattableString.Invariant($"q {widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm /Im0 Do Q")));
            pdf.Internals.AddObject(contentDict);
            page.Elements["/Contents"] = contentDict.Reference;
        }

        using var output = new MemoryStream();
        pdf.Save(output, false);
        return output.ToArray();
    }

    /// <summary>Reads pixel dimensions and component count from the JPEG SOF marker.</summary>
    private static (int Width, int Height, int Components) ReadJpegHeader(byte[] jpeg)
    {
        var i = 2; // past the FFD8 start
        while (i + 10 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }

            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }                              // fill bytes
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; } // standalone

            if (i + 4 > jpeg.Length) break;
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];

            // SOF0-SOF15 minus DHT/JPG/DAC carry the frame header with the pixel geometry.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                var height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                var components = jpeg[i + 9];
                return (width, height, components);
            }

            i += 2 + length;
        }

        throw new InvalidOperationException("无法识别的 JPEG 页面数据");
    }
}
