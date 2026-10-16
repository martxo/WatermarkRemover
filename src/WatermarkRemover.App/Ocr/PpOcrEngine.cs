using System.IO;
using RapidOcrNet;
using SkiaSharp;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.App.Ocr;

/// <summary>
/// Text recognition through PP-OCR (PaddleOCR) ONNX models running fully offline on CPU — the
/// fallback for machines that have no WeChat install. The Chinese PP-OCRv5 mobile model set
/// (det + cls + rec + dict, ~21.5 MB) ships in <c>Models\</c> next to the executable; nothing is
/// downloaded at runtime and no OS OCR component is required.
/// </summary>
public sealed class PpOcrEngine : IOcrEngine, IDisposable
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

        string det = AppAssets.Model(DetModelFile);
        string cls = AppAssets.Model(ClsModelFile);
        string rec = AppAssets.Model(RecModelFile);
        string keys = AppAssets.Model(KeysFile);

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

    public async Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var bitmap = SKBitmap.Decode(imageBytes)
                ?? throw new InvalidOperationException("无法解码输入图像。");

            OcrResult result = await _ocr
                .DetectAsync(bitmap, RapidOcrOptions.Default, CancellationToken.None)
                .ConfigureAwait(false);

            return result.TextBlocks
                .Select(block => block.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _ocr.Dispose();
        _gate.Dispose();
    }
}
