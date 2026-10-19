using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage.Streams;
using WatermarkRemover.App.Layout;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// One-shot probe of the OCR engine chain, appended to bug reports. When a user copies a failure
/// this tells us which engines were usable on their machine and why the others were skipped —
/// exactly what is needed to tell "no WeChat installed" apart from "PP-OCR models missing" apart
/// from "Windows OCR components broken" (REGDB_E_CLASSNOTREG).
/// </summary>
public static class OcrDiagnostics
{
    /// <summary>REGDB_E_CLASSNOTREG: the WinRT/COM class is not registered on this system.</summary>
    private const int ClassNotRegistered = unchecked((int)0x80040154);

    private static string? _cached;

    /// <summary>Human-readable engine availability report. Resolved once per process.</summary>
    public static string Current => _cached ??= Build();

    private static string Build()
    {
        var sb = new StringBuilder();

        // The composed pipeline the jobs actually use (layout block × text engine).
        sb.AppendLine($"当前管线：{AppOcrEngineChain.Current.DisplayName}");

        // The engine chain was already probed at startup — read the cached result instead of
        // re-probing, so the report always matches what the job actually used.
        sb.AppendLine(AppOcrEngineChain.Instance.Describe());

        // Layout blocks probe on demand: the report is only built for bug reports, so loading
        // the model files here to show every block's availability is acceptable.
        AppLayoutStageChain.Instance.EnsureAllProbed();
        sb.AppendLine(AppLayoutStageChain.Instance.Describe());

        // The PDF renderer used to go through WinRT too; record whether the OS PDF stack still
        // answers — that activation is what threw REGDB_E_CLASSNOTREG before the pdfium switch.
        sb.AppendLine($"Windows.Data.Pdf 组件：{(IsWinRtPdfAvailable() ? "可用" : "缺失/未注册（已由内置 PDFium 顶替）")}");

        return sb.ToString();
    }

    /// <summary>
    /// Activates the WinRT PDF class for real: loading from an empty stream must throw a content
    /// error, while a missing registration throws REGDB_E_CLASSNOTREG at activation time.
    /// </summary>
    private static bool IsWinRtPdfAvailable()
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(stream).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            return !IsClassNotRegistered(ex);
        }
    }

    private static bool IsClassNotRegistered(Exception ex) =>
        ex.HResult == ClassNotRegistered ||
        (ex is COMException com && com.HResult == ClassNotRegistered);
}
