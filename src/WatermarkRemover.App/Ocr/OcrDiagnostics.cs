using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage.Streams;

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

        sb.AppendLine($"微信 OCR：{(WeChatOcrEngine.TryLocate(out _, out string wechatReason) ? "可用" : $"不可用（{wechatReason}）")}");

        // File probe only — loading the ONNX models here would burn seconds in a bug report.
        string[] missing = new[] { "ch_PP-OCRv5_det_mobile.onnx", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx", "ch_PP-OCRv5_rec_mobile.onnx", "ppocrv5_dict.txt" }
            .Where(f => !File.Exists(AppAssets.Model(f)))
            .ToArray();
        sb.AppendLine(missing.Length == 0
            ? "PP-OCR：模型就绪（未加载）"
            : $"PP-OCR：模型文件缺失（{string.Join("、", missing)}）");

        sb.AppendLine($"Windows 原生 OCR：{DescribeWindowsOcr()}");

        // The PDF renderer used to go through WinRT too; record whether the OS PDF stack still
        // answers — that activation is what threw REGDB_E_CLASSNOTREG before the pdfium switch.
        sb.AppendLine($"Windows.Data.Pdf 组件：{(IsWinRtPdfAvailable() ? "可用" : "缺失/未注册（已由内置 PDFium 顶替）")}");

        return sb.ToString();
    }

    private static string DescribeWindowsOcr()
    {
        try
        {
            var engine = new WindowsMediaOcrEngine();
            return engine.IsAvailable ? "可用" : "不可用（缺少 OCR 语言包）";
        }
        catch (Exception ex)
        {
            return $"初始化失败（{ex.Message}）";
        }
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
