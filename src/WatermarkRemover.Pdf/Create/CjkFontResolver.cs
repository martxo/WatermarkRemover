using PdfSharp.Fonts;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Font resolver for PDFsharp. The platform independent PDFsharp build only knows the base-14 Latin
/// fonts (Helvetica/Times/Courier) and cannot see Windows fonts, so without a resolver no CJK glyph
/// can be drawn - including the Chinese page-number stamp "第 n / N 页". This resolver serves the
/// first available CJK-capable TrueType file from the Windows font folder for every requested face.
/// </summary>
internal sealed class CjkFontResolver : IFontResolver
{
    /// <summary>Logical family name passed to <c>XFont</c>; the resolver maps it to an embedded face.</summary>
    public const string FamilyName = "WatermarkRemover CJK";

    /// <summary>Physical face name reported back to PDFsharp; also the key for <see cref="GetFont"/>.</summary>
    public const string FaceName = "WatermarkRemoverCjk";

    // Ordered by preference: real CJK fonts first, a Latin font as an absolute last resort so PDF
    // generation never hard-fails on a machine without the usual Chinese fonts (Latin page text then
    // still works, CJK glyphs would be missing).
    private static readonly string[] PreferredFiles =
    {
        "simhei.ttf",
        "Deng.ttf",
        "simfang.ttf",
        "simkai.ttf",
        "simsunb.ttf",
        "arial.ttf",
        "segoeui.ttf",
    };

    private readonly object _gate = new();
    private byte[]? _fontBytes;

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) => new(FaceName);

    public byte[]? GetFont(string faceName)
    {
        lock (_gate)
        {
            if (_fontBytes is not null)
            {
                return _fontBytes;
            }

            var fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var fileName in PreferredFiles)
            {
                var path = Path.Combine(fontsDirectory, fileName);
                if (File.Exists(path))
                {
                    _fontBytes = File.ReadAllBytes(path);
                    return _fontBytes;
                }
            }

            return null;
        }
    }
}
