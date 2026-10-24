using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;
using WatermarkRemover.Scan.Pdf.Internal;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace WatermarkRemover.Scan.Pdf;

/// <summary>Everything the PDF builder needs beyond the document itself.</summary>
public sealed record PdfBuildOptions
{
    public bool Searchable { get; init; }

    public bool PageNumbers { get; init; }

    /// <summary>Open-password; empty means the document is not encrypted.</summary>
    public string UserPassword { get; init; } = string.Empty;

    /// <summary>Permissions password; defaults to the user password when left empty.</summary>
    public string OwnerPassword { get; init; } = string.Empty;

    /// <summary>高压缩 PDF: cap embedded bitmaps at 300dpi and JPEG-encode them.</summary>
    public bool HighCompression { get; init; }

    public bool Encrypted => !string.IsNullOrEmpty(UserPassword) || !string.IsNullOrEmpty(OwnerPassword);
}

/// <summary>
/// One page's finished PDF payload: either a raw CCITT G4 fax stream (<see cref="Ccitt"/>) or a
/// compressed bitmap (<see cref="Bytes"/>), plus the page geometry derived from the ORIGINAL
/// pixels so later work never changes the page size. Captured pages can be encoded as they
/// arrive and the raw rasters released; assembly then consumes these small payloads.
/// </summary>
public sealed record EncodedScanPage(
    byte[]? Bytes,
    byte[]? Ccitt,
    int PixelWidth,
    int PixelHeight,
    double WidthPt,
    double HeightPt);

/// <summary>
/// Builds the PDF byte stream for a scanned document. Page layout, the optional searchable text
/// layer and the page-number stamp exist in exactly one place; callers supply the image encoder.
/// </summary>
public static class PdfDocumentBuilder
{
    private const double FallbackDpi = 300;

    /// <summary>Resolution cap for 高压缩 PDF embedded bitmaps.</summary>
    private const int HighCompressionDpi = 300;

    /// <summary>JPEG quality for 高压缩 PDF embedded bitmaps (the target's 60~75 range).</summary>
    private const int HighCompressionJpegQuality = 70;

    private static readonly object InitGate = new();
    private static bool _initialized;

    /// <summary>PDFsharp 6.x keeps a global (process wide) font resolver and licence state.</summary>
    private static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized)
            {
                return;
            }

            // PDFsharp 6.x ships under AGPL-3.0 and refuses to create a PdfDocument until the
            // licence is acknowledged at runtime, so this call is mandatory before any PdfDocument.
            PdfSharpLicense.SetAgplLicense();

            // The platform independent build cannot resolve Windows fonts, so install our own.
            GlobalFontSettings.FontResolver ??= new CjkFontResolver();

            _initialized = true;
        }
    }

    /// <param name="document">Pages, settings and base name to render.</param>
    /// <param name="options">Searchable layer / page numbers / password / compression knobs.</param>
    /// <param name="encoder">Image encoder supplied by the imaging layer (RasterImage -> bitmap bytes).</param>
    public static byte[] Build(ScanDocument document, PdfBuildOptions options, IImageEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(encoder);
        EnsureInitialized();

        var bytes = Render(document, options, encoder);
        if (options.Encrypted && !CanOpenWithPassword(bytes, options.UserPassword))
        {
            // A file that will not reopen with its own password is worse than no file: fail loudly
            // instead of writing a document the user can never unlock.
            throw new InvalidOperationException("加密 PDF 生成后无法用密码回读，已中止输出以免写出无法打开的文件。");
        }

        return bytes;
    }

    /// <summary>
    /// Assembles a document whose pages were already encoded by the capture path. The caller
    /// keeps only the small payloads, so a long scan never needs every raw frame in memory.
    /// </summary>
    public static byte[] Build(
        ScanDocument document,
        PdfBuildOptions options,
        IReadOnlyList<EncodedScanPage> encodedPages)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(encodedPages);

        if (encodedPages.Count != document.Pages.Count)
            throw new ArgumentException("编码页数与文档页数不一致。", nameof(encodedPages));

        EnsureInitialized();
        byte[] bytes = Assemble(document, options, ResolveDpi(document.Settings), encodedPages);
        if (options.Encrypted && !CanOpenWithPassword(bytes, options.UserPassword))
        {
            throw new InvalidOperationException("加密 PDF 生成后无法用密码回读，已中止输出以免写出无法打开的文件。");
        }
        return bytes;
    }

    /// <summary>The scan DPI used for page geometry; 300 when the setting is unset.</summary>
    public static double ResolveDpi(ScanSettings settings)
    {
        double dpi = (double)(int)settings.Resolution;
        return dpi > 0 ? dpi : FallbackDpi;
    }

    private static byte[] Render(ScanDocument document, PdfBuildOptions options, IImageEncoder encoder)
    {
        double dpi = ResolveDpi(document.Settings);
        EncodedScanPage[] encodedPages = EncodePages(document.Pages, options, encoder, dpi);
        return Assemble(document, options, dpi, encodedPages);
    }

    /// <summary>
    /// Encodes every page in parallel. Used by callers that still hold all raw pages; the scan
    /// path encodes each page as it arrives and calls the encoded overload instead.
    /// </summary>
    private static EncodedScanPage[] EncodePages(
        IReadOnlyList<ScannedPage> pages,
        PdfBuildOptions options,
        IImageEncoder encoder,
        double dpi)
    {
        // Per-page encoding (bilevel detection + G4 pack, or the 高压缩 resize + JPEG encode) is
        // the CPU-heavy part of a multi-page scan. The encoders are thread-safe by construction,
        // so the fan-out is safe. Capped at 8 workers: each in-flight page holds a ~35 MB frame.
        var encodedPages = new EncodedScanPage[pages.Count];
        var parallelism = Math.Min(Environment.ProcessorCount, 8);
        if (pages.Count > 1 && parallelism > 1)
        {
            Parallel.For(0, encodedPages.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                index => encodedPages[index] = EncodePage(pages[index], options, encoder, dpi));
        }
        else
        {
            for (var index = 0; index < encodedPages.Length; index++)
                encodedPages[index] = EncodePage(pages[index], options, encoder, dpi);
        }

        return encodedPages;
    }

    private static byte[] Assemble(
        ScanDocument document,
        PdfBuildOptions options,
        double dpi,
        IReadOnlyList<EncodedScanPage> encodedPages)
    {
        // --- Assemble the document single-threaded ---
        // PdfSharp's object graph is not thread-safe, so the assembly just consumes the finished
        // byte payloads — no pixel work happens here, it only writes dictionaries and streams.
        using var pdf = new PdfDocument();
        pdf.Info.Title = document.BaseName ?? string.Empty;

        if (options.Encrypted)
        {
            ApplyEncryption(pdf, options);
        }

        var total = document.Pages.Count;
        for (var index = 0; index < total; index++)
        {
            var source = document.Pages[index];
            var encoded = encodedPages[index];

            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(encoded.WidthPt);
            page.Height = XUnit.FromPoint(encoded.HeightPt);

            // Bilevel frames (engine 黑白 mode) take the official-software route: a raw CCITT G4
            // image XObject, never decoded back to a bitmap in between.
            if (encoded.Ccitt is { } ccitt)
            {
                WriteBilevelPage(pdf, page, encoded.PixelWidth, encoded.PixelHeight, encoded.WidthPt, encoded.HeightPt, ccitt);
                if (options.Searchable && source.Words.Count > 0)
                {
                    // Invisible text under the image: drawn into an earlier content stream, so
                    // the opaque fax image covers it (white glyphs stay hidden on paper).
                    using var textGfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend);
                    DrawTextLayer(textGfx, source.Words, dpi);
                }
            }
            else
            {
                using var gfx = XGraphics.FromPdfPage(page);

                // --- searchable text layer, painted BEFORE the image ---
                // The page bitmap is opaque, so anything drawn after it would be hidden - and would also
                // hide the text. Drawing the OCR words first (in white) and the image second keeps the
                // glyphs out of sight on screen and on paper while they still live in the page content
                // stream: a PDF reader can select, copy and search them.
                if (options.Searchable && source.Words.Count > 0)
                {
                    DrawTextLayer(gfx, source.Words, dpi);
                }

                using var stream = new MemoryStream(encoded.Bytes!);
                using var ximage = XImage.FromStream(stream);
                gfx.DrawImage(ximage, 0, 0, encoded.WidthPt, encoded.HeightPt);
            }

            if (options.PageNumbers)
            {
                using var numberGfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                DrawPageNumber(numberGfx, index + 1, total, encoded.WidthPt, encoded.HeightPt);
            }
        }

        using var output = new MemoryStream();
        pdf.Save(output, false);
        return output.ToArray();
    }

    /// <summary>Everything pixel-related for one page, safe to run on a worker thread: bilevel
    /// frames take the CCITT G4 route, everything else the 高压缩 resize + JPEG route (or PNG
    /// when 高压缩 is off). Public so the scan path can encode each page as it arrives.</summary>
    public static EncodedScanPage EncodePage(
        ScannedPage source,
        PdfBuildOptions options,
        IImageEncoder encoder,
        double dpi)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(encoder);

        var image = source.Image ?? throw new InvalidOperationException("页面缺少图像数据。");

        var widthPt = PageGeometry.PixelsToPoints(image.Width, dpi);
        var heightPt = PageGeometry.PixelsToPoints(image.Height, dpi);

        if (CcittG4Image.TryEncode(image, out var ccitt))
        {
            return new EncodedScanPage(null, ccitt, image.Width, image.Height, widthPt, heightPt);
        }

        if (options.HighCompression)
        {
            // 高压缩 PDF: cap the embedded bitmap at HighCompressionDpi; the JPEG below then
            // streams into the page as DCTDecode, which is where the size win comes from.
            var maxEdge = (int)Math.Round(Math.Max(widthPt, heightPt) * HighCompressionDpi / 72.0);
            image = encoder.Resize(image, maxEdge, upscale: false);
        }

        var bytes = options.HighCompression
            ? encoder.Encode(image, "jpg", HighCompressionJpegQuality)
            : encoder.Encode(image, "png", 100);
        return new EncodedScanPage(bytes, null, image.Width, image.Height, widthPt, heightPt);
    }

    /// <summary>
    /// Writes one page whose bitmap is a raw CCITT G4 fax stream: the image XObject is emitted
    /// by hand (/CCITTFaxDecode, 1bpc DeviceGray) and the content stream just places it at page
    /// size. No raster decode/re-encode round-trip happens anywhere — what the engine produced
    /// is what lands in the file, byte for byte.
    /// </summary>
    private static void WriteBilevelPage(
        PdfDocument pdf, PdfPage page, int widthPx, int heightPx, double widthPt, double heightPt, byte[] ccitt)
    {
        var imageDict = new PdfDictionary(pdf);
        imageDict.Elements["/Type"] = new PdfName("/XObject");
        imageDict.Elements["/Subtype"] = new PdfName("/Image");
        imageDict.Elements["/Width"] = new PdfInteger(widthPx);
        imageDict.Elements["/Height"] = new PdfInteger(heightPx);
        imageDict.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
        imageDict.Elements["/BitsPerComponent"] = new PdfInteger(1);
        imageDict.Elements["/Filter"] = new PdfName("/CCITTFaxDecode");
        var parms = new PdfDictionary(pdf);
        parms.Elements["/K"] = new PdfInteger(-1);
        parms.Elements["/Columns"] = new PdfInteger(widthPx);
        parms.Elements["/Rows"] = new PdfInteger(heightPx);
        imageDict.Elements["/DecodeParms"] = parms;
        imageDict.CreateStream(ccitt);
        pdf.Internals.AddObject(imageDict);

        var xobjects = new PdfDictionary(pdf);
        xobjects.Elements["/Im0"] = imageDict.Reference;
        page.Resources.Elements["/XObject"] = xobjects;

        var contentDict = new PdfDictionary(pdf);
        contentDict.CreateStream(
            System.Text.Encoding.ASCII.GetBytes(
                System.FormattableString.Invariant($"q {widthPt:0.####} 0 0 {heightPt:0.####} 0 0 cm /Im0 Do Q")));
        pdf.Internals.AddObject(contentDict);
        page.Elements["/Contents"] = contentDict.Reference;
    }

    /// <summary>
    /// Password-protects the document. Read access is what the password grants; editing, copying
    /// and reassembly stay with the owner.
    /// </summary>
    private static void ApplyEncryption(PdfDocument pdf, PdfBuildOptions options)
    {
        var security = pdf.SecuritySettings;

        // PDFsharp 6.x has no EncryptionAlgorithm enum and keeps the standard security handler
        // internal; simply setting a password triggers its default encryption, which is V4/AES-128.
        // The round-trip check in Build proves the output actually opens with the password.
        security.UserPassword = options.UserPassword;
        security.OwnerPassword = string.IsNullOrEmpty(options.OwnerPassword)
            ? options.UserPassword
            : options.OwnerPassword;

        security.PermitPrint = true;
        security.PermitFullQualityPrint = true;
        security.PermitExtractContent = false;
        security.PermitModifyDocument = false;
        security.PermitAnnotations = false;
        security.PermitFormsFill = false;
        security.PermitAssembleDocument = false;
    }

    /// <summary>Round-trips the bytes through PDFsharp's reader to prove the password works.</summary>
    private static bool CanOpenWithPassword(byte[] bytes, string password)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var reopened = PdfReader.Open(stream, password, PdfDocumentOpenMode.Import);
            return reopened.PageCount > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DrawTextLayer(XGraphics gfx, IReadOnlyList<OcrWord> words, double dpi)
    {
        var brush = new XSolidBrush(XColors.White);
        var fonts = new Dictionary<int, XFont>();

        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text) || word.Width <= 0 || word.Height <= 0)
            {
                continue;
            }

            // The box height in points gives a usable em size; one font per rounded size is cached so
            // a page with hundreds of words does not create hundreds of font objects.
            var sizePt = PageGeometry.PixelsToPoints(word.Height, dpi);
            var sizeKey = Math.Max(3, (int)Math.Round(sizePt));
            if (!fonts.TryGetValue(sizeKey, out var font))
            {
                font = new XFont(CjkFontResolver.FamilyName, sizeKey, XFontStyleEx.Regular);
                fonts[sizeKey] = font;
            }

            // OcrWord coordinates are the top-left of the pixel box; the baseline sits roughly one
            // ascent below that, approximated here so the invisible text tracks the visible glyphs.
            var x = PageGeometry.PixelsToPoints(word.X, dpi);
            var y = PageGeometry.PixelsToPoints(word.Y, dpi) + (sizePt * 0.8);
            gfx.DrawString(word.Text, font, brush, new XPoint(x, y));
        }
    }

    private static void DrawPageNumber(XGraphics gfx, int pageNumber, int total, double widthPt, double heightPt)
    {
        const double footerHeight = 28;
        var font = new XFont(CjkFontResolver.FamilyName, 9, XFontStyleEx.Regular);
        var text = $"第 {pageNumber} / {total} 页";
        gfx.DrawString(
            text,
            font,
            new XSolidBrush(XColors.Black),
            new XRect(0, heightPt - footerHeight, widthPt, footerHeight),
            XStringFormats.BottomCenter);
    }
}
