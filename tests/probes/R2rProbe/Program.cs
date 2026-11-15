using System.Diagnostics;
using PdfSharp;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

// Verifies the PDFsharp 6.5 AGPL licence probe under the shipped app's exact publish flags.
// PASS requires: licence acknowledged AND a real document round-trip through the reader.

PdfSharpLicense.SetAgplLicense();
Console.WriteLine($"PdfSharp loaded from: {typeof(PdfSharpLicense).Assembly.Location}");
string temp = Path.Combine(Path.GetTempPath(), "r2r-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
long start = Stopwatch.GetTimestamp();
try
{
    using (var doc = new PdfDocument())
    {
        doc.AddPage();
        string pdf = Path.Combine(temp, "probe.pdf");
        doc.Save(pdf);
        using PdfDocument read = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        Console.WriteLine($"pages={read.PageCount}");
    }

    Console.WriteLine($"PASS in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { }
}
