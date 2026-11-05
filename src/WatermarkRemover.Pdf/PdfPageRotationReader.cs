using PdfSharp.Pdf.IO;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Reads every page's final <c>/Rotate</c> back from a PDF on disk. The scan dataset exporter
/// uses it as the truth source: the training label must be what the file actually holds after
/// the write, never an in-memory turn that a later edit might have changed.
/// </summary>
public static class PdfPageRotationReader
{
    /// <summary>Per-page <c>/Rotate</c> in page order, normalised to 0 / 90 / 180 / 270.</summary>
    public static IReadOnlyList<int> Read(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        PdfSharpRuntime.EnsureInitialized();

        using var document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        var rotations = new int[document.PageCount];
        for (var index = 0; index < rotations.Length; index++)
            rotations[index] = Normalize(document.Pages[index].Rotate);
        return rotations;
    }

    private static int Normalize(int rotation) => ((rotation % 360) + 360) % 360;
}
