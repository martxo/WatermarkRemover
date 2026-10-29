using PdfSharp;
using PdfSharp.Fonts;

namespace WatermarkRemover.Pdf;

/// <summary>
/// One-time process-wide setup for PDFsharp 6.x: the AGPL licence acknowledgement is mandatory
/// before any <c>PdfDocument</c> is created, and the platform-independent build needs a font
/// resolver for CJK text (page numbers, searchable text layers).
/// </summary>
internal static class PdfSharpRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    internal static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;

            PdfSharpLicense.SetAgplLicense();
            GlobalFontSettings.FontResolver ??= new CjkFontResolver();
            _initialized = true;
        }
    }
}
