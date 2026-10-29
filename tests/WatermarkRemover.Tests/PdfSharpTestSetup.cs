using System.Runtime.CompilerServices;
using PdfSharp;

namespace WatermarkRemover.Tests;

/// <summary>
/// PDFsharp 6.x refuses to create a <c>PdfDocument</c> until the AGPL licence is acknowledged.
/// The app does that inside WatermarkRemover.Pdf, but tests also build fixtures directly with
/// PDFsharp, so the acknowledgement has to run before the first test executes.
/// </summary>
internal static class PdfSharpTestSetup
{
    [ModuleInitializer]
    internal static void Initialize() => PdfSharpLicense.SetAgplLicense();
}
