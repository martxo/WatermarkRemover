using System.Reflection;
using PdfSharpCore;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Shared in-place edit plumbing for the PDF page tools (rotate / delete / insert): open the
/// file for modification, apply one edit, write it to a side file and swap it over the original
/// only after the save succeeded — an interrupted edit can never leave a truncated PDF behind.
/// </summary>
internal static class PdfDocumentEditor
{
    internal static void EditDocument(string pdfPath, Action<PdfDocument> edit)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
            throw new ArgumentException("PDF path must not be empty.", nameof(pdfPath));

        string tempPath = pdfPath + ".edit.tmp";
        try
        {
            PdfDocument? doc = null;
            try
            {
                doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify);
                DisableForeignPageOrientationCorrection(doc);
                edit(doc);
                doc.Save(tempPath);
            }
            finally
            {
                (doc as IDisposable)?.Dispose();
            }

            // Swap only now that the side file is complete and the original is released.
            File.Move(tempPath, pdfPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // A leftover side file is harmless; the PDF itself is already intact.
            }
        }
    }

    /// <summary>
    /// PdfSharpCore flags every page whose explicit /Rotate is 90/270 as "landscape" on open,
    /// and its WriteObject then swaps that page's MediaBox width/height whenever the page is
    /// serialized with an even rotation (0/180) — a correction meant for pages the library
    /// created itself. On foreign pages it is plain corruption: the content stream is copied
    /// unchanged, so the swapped box clips everything that now sits outside it (a portrait
    /// scan loses its top third). Rotating here is metadata-only, and /Rotate is interpreted
    /// by the viewer per the PDF spec — no library-side box compensation is wanted. Reset the
    /// flag so WriteObject never touches the MediaBox of any page.
    /// </summary>
    internal static void DisableForeignPageOrientationCorrection(PdfDocument doc)
    {
        var orientationField = typeof(PdfPage).GetField("_orientation",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (orientationField is null)
            return; // a future PdfSharpCore may rename the field; the swap guard simply degrades

        foreach (var page in doc.Pages)
            orientationField.SetValue(page, PageOrientation.Portrait);
    }
}
