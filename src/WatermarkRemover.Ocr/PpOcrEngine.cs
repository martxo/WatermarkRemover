using System.IO;
using RapidOcrNet;
using SkiaSharp;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Ocr;

/// <summary>
/// Text recognition through PP-OCR (PaddleOCR) ONNX models running fully offline on CPU — the
/// fallback for machines that have no WeChat install. The Chinese PP-OCRv5 mobile model set
/// (det + cls + rec + dict, ~21.5 MB) ships in <c>Models\</c> next to the executable; nothing is
/// downloaded at runtime and no OS OCR component is required.
/// </summary>
public sealed class PpOcrEngine : IOcrEngine, IBoundedOcrEngine, IDisposable
{
    private const string DetModelFile = "ch_PP-OCRv5_det_mobile.onnx";
    private const string ClsModelFile = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";
    private const string RecModelFile = "ch_PP-OCRv5_rec_mobile.onnx";
    private const string KeysFile = "ppocrv5_dict.txt";

    private readonly RapidOcr _ocr;

    /// <summary>One ONNX session set only — recognition calls must not overlap.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PpOcrEngine(RapidOcr ocr) => _ocr = ocr;

    public string DisplayName => "PP-OCR";

    /// <summary>
    /// Loads the bundled Chinese model set (resolved via <see cref="AppAssets"/>) and prepares the
    /// pipeline. Returns false when the model files are missing (e.g. an exe-only upgrade left no
    /// <c>Models\</c> folder) or the ONNX runtime rejects them — the caller then keeps falling
    /// back down the engine chain.
    /// </summary>
    public static bool TryLocate(out PpOcrEngine? engine, out string reason)
    {
        engine = null;

        string det = OcrAssets.Model(DetModelFile);
        string cls = OcrAssets.Model(ClsModelFile);
        string rec = OcrAssets.Model(RecModelFile);
        string keys = OcrAssets.Model(KeysFile);

        string[] missing = new[] { det, cls, rec, keys }.Where(p => !File.Exists(p)).ToArray();
        if (missing.Length > 0)
        {
            reason = $"模型文件缺失：{string.Join("、", missing.Select(Path.GetFileName))}";
            return false;
        }

        try
        {
            var ocr = new RapidOcr();
            ocr.InitModels(det, cls, rec, keys, numThread: 0);
            engine = new PpOcrEngine(ocr);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"PP-OCR 模型加载失败：{ex.Message}";
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
        (await RecognizeCoreAsync(imageBytes).ConfigureAwait(false)).Select(l => l.Text).ToList();

    /// <summary>Line bounds come from each block's detection quad (<c>BoxPoints</c>), normalized
    /// to the source image — this is what drives the anchor-based layout blocks.</summary>
    public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes) =>
        RecognizeCoreAsync(imageBytes);

    private async Task<IReadOnlyList<RecognizedLine>> RecognizeCoreAsync(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var bitmap = SKBitmap.Decode(imageBytes)
                ?? throw new InvalidOperationException("无法解码输入图像。");
            double imgW = bitmap.Width, imgH = bitmap.Height;

            OcrResult result = await _ocr
                .DetectAsync(bitmap, RapidOcrOptions.Default, CancellationToken.None)
                .ConfigureAwait(false);

            var lines = new List<RecognizedLine>();
            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                lines.Add(new RecognizedLine(block.Text, NormalizeQuad(block.BoxPoints, imgW, imgH)));
            }

            return lines;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bounding box of the detection quad in normalized (0–1) image coordinates.</summary>
    private static RectD? NormalizeQuad(SKPointI[]? quad, double imgW, double imgH)
    {
        if (quad is null || quad.Length == 0 || imgW <= 0 || imgH <= 0) return null;

        double minX = quad.Min(p => p.X), maxX = quad.Max(p => p.X);
        double minY = quad.Min(p => p.Y), maxY = quad.Max(p => p.Y);
        double x = Math.Clamp(minX / imgW, 0, 1);
        double y = Math.Clamp(minY / imgH, 0, 1);
        double w = Math.Clamp((maxX - minX) / imgW, 0, 1 - x);
        double h = Math.Clamp((maxY - minY) / imgH, 0, 1 - y);
        return w <= 0 || h <= 0 ? null : new RectD(x, y, w, h);
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _gate.Dispose();
    }
}
