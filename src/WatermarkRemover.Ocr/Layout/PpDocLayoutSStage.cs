using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Ocr.Layout;

/// <summary>
/// Lightweight layout block on the PP-DocLayout-S ONNX export (4.7 MB, bundled in <c>Models\</c>).
/// Preprocessing mirrors the PaddleX contract: direct 480×480 resize, [0,1] + ImageNet
/// mean/std normalisation, <c>scale_factor=[480/h, 480/w]</c>; NMS is baked into the graph, so
/// parsing just walks <c>Detections</c> up to <c>num_dets</c> (rows of cls,score,x1,y1,x2,y2 in
/// original-image pixels). Probing validates the input/output contract — a community export that
/// drifts is registered as unavailable instead of breaking the chain (decision gate).
/// </summary>
public sealed class PpDocLayoutSStage : ILayoutStage
{
    public const string ModelFile = "pp_doclayout_s.onnx";
    private const int InputSize = 480;

    // ImageNet normalisation, RGB order (PaddleX NormalizeImage).
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>One session — recognising calls must not overlap.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly InferenceSession _session;
    private readonly string _imageInput;
    private readonly string _scaleInput;
    private readonly string _detectionsOutput;
    private readonly string _numDetsOutput;

    private PpDocLayoutSStage(
        InferenceSession session,
        string imageInput,
        string scaleInput,
        string detectionsOutput,
        string numDetsOutput)
    {
        _session = session;
        _imageInput = imageInput;
        _scaleInput = scaleInput;
        _detectionsOutput = detectionsOutput;
        _numDetsOutput = numDetsOutput;
    }

    public string Name => "PP-DocLayout-S 轻量版面";

    /// <summary>
    /// Loads the bundled model and validates its contract: two inputs (4-D image + 2-D
    /// scale_factor) and two outputs (rank-3 detections + a count). Anything else — a re-export
    /// with different tensor shapes — is reported as unavailable so the chain degrades cleanly.
    /// </summary>
    public static bool TryCreate(out ILayoutStage? stage, out string reason)
    {
        stage = null;
        string path = OcrAssets.Model(ModelFile);
        if (!File.Exists(path))
        {
            reason = $"模型文件缺失：{ModelFile}";
            return false;
        }

        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(path);

            string? image = session.InputMetadata
                .FirstOrDefault(kv => kv.Value.Dimensions.Length == 4).Key;
            string? scale = session.InputMetadata
                .FirstOrDefault(kv => kv.Value.Dimensions.Length == 2).Key;
            string? detections = session.OutputMetadata
                .FirstOrDefault(kv => kv.Value.Dimensions.Length >= 2).Key;
            string? numDets = session.OutputMetadata
                .FirstOrDefault(kv => kv.Value.Dimensions.Length <= 1).Key;

            if (image is null || scale is null || detections is null || numDets is null)
            {
                reason = "模型输入输出契约不符，已停用";
                session.Dispose();
                return false;
            }

            stage = new PpDocLayoutSStage(session, image, scale, detections, numDets);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            reason = $"PP-DocLayout-S 模型加载失败：{ex.Message}";
            return false;
        }
    }

    public async Task<IReadOnlyList<LayoutRegion>> DetectRegionsAsync(byte[] imageBytes, IRegionProbe probe)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run<IReadOnlyList<LayoutRegion>>(() =>
            {
                using var bitmap = SKBitmap.Decode(imageBytes)
                    ?? throw new InvalidOperationException("无法解码输入图像。");
                int origW = bitmap.Width, origH = bitmap.Height;

                float[] image = BuildImageInput(bitmap);
                float[] scale = { (float)InputSize / origH, (float)InputSize / origW };
                (float[] preds, int rows, int cols, int count) = RunSession(image, scale);

                List<LayoutRegion> regions = ParseRegions(preds, rows, cols, count, origW, origH);
                OcrLog.Write($"[版面] PP-DocLayout-S 检出 {regions.Count} 区：" +
                    string.Join("；", regions.Take(12).Select(r => $"{r.Kind}({r.Bounds})")));
                return regions;
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>480×480 direct resize (aspect ignored, as in the PaddleX contract), RGB /255 then mean/std.</summary>
    private static float[] BuildImageInput(SKBitmap source)
    {
        using var resized = new SKBitmap(
            new SKImageInfo(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear)))
            throw new InvalidOperationException("图像缩放失败（不支持的像素格式）。");

        var image = new float[3 * InputSize * InputSize];
        int plane = InputSize * InputSize;
        ReadOnlySpan<byte> px = resized.GetPixelSpan();
        int rowBytes = resized.RowBytes;
        for (int y = 0; y < InputSize; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < InputSize; x++)
            {
                int p = row + x * 4; // RGBA
                int i = y * InputSize + x;
                image[i] = (px[p] / 255f - Mean[0]) / Std[0];             // R
                image[plane + i] = (px[p + 1] / 255f - Mean[1]) / Std[1]; // G
                image[2 * plane + i] = (px[p + 2] / 255f - Mean[2]) / Std[2]; // B
            }
        }

        return image;
    }

    private (float[] Preds, int Rows, int Cols, int Count) RunSession(float[] image, float[] scale)
    {
        var imageTensor = new DenseTensor<float>(image, new[] { 1, 3, InputSize, InputSize });
        var scaleTensor = new DenseTensor<float>(scale, new[] { 1, 2 });
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor(_imageInput, imageTensor),
            NamedOnnxValue.CreateFromTensor(_scaleInput, scaleTensor),
        };

        using var results = _session.Run(inputs);

        var detections = results.First(r => r.Name == _detectionsOutput).AsTensor<float>();
        List<int> shape = detections.Dimensions.ToArray().ToList();
        while (shape.Count > 2 && shape[0] == 1) shape.RemoveAt(0);
        if (shape.Count != 2 || shape[1] < 6)
            throw new InvalidDataException(
                $"版面模型输出形状异常：[{string.Join(",", detections.Dimensions.ToArray())}]");

        var preds = new float[shape[0] * shape[1]];
        int i = 0;
        foreach (float v in detections) preds[i++] = v;

        // num_dets bounds the valid rows — entries past it are NMS padding.
        // This export types the count as Int32; other exports may keep it float, so accept both.
        int count = shape[0];
        var numDets = results.First(r => r.Name == _numDetsOutput);
        if (numDets.AsTensor<int>() is { } countInt)
            count = Math.Clamp(countInt.Length > 0 ? countInt.GetValue(0) : count, 0, shape[0]);
        else if (numDets.AsTensor<float>() is { } countFloat && countFloat.Length > 0)
            count = Math.Clamp((int)countFloat.GetValue(0), 0, shape[0]);

        return (preds, shape[0], shape[1], count);
    }

    /// <summary>23 layout classes, ordered to match the upstream inference.yml (model card).</summary>
    private static readonly string[] ClassNames =
    {
        "paragraph_title", "image", "text", "number", "abstract", "content",
        "figure_title", "formula", "table", "table_title", "reference", "doc_title",
        "footnote", "header", "algorithm", "footer", "seal", "chart_title", "chart",
        "formula_number", "header_image", "footer_image", "aside_text",
    };

    /// <summary>Rows are cls,score,x1,y1,x2,y2 already in original-image pixels.</summary>
    private static List<LayoutRegion> ParseRegions(
        float[] preds, int rows, int cols, int count, int origW, int origH)
    {
        var regions = new List<LayoutRegion>();
        int limit = Math.Min(count, rows);
        OcrLog.Write($"[版面] PP-DocLayout-S 原始行：" + string.Join("；",
            Enumerable.Range(0, limit)
                .Select(i => $"cls{(int)preds[i * cols]}@{preds[i * cols + 1]:0.00}({preds[i * cols + 2]:0},{preds[i * cols + 3]:0},{preds[i * cols + 4]:0},{preds[i * cols + 5]:0})")
                .Take(30)));
        for (int i = 0; i < limit; i++)
        {
            int b = i * cols;
            float score = preds[b + 1];
            // The graph's NMS runs at score_threshold=0.3; rows below it are padding noise.
            if (score < 0.3f) continue;

            double x1 = Math.Clamp(preds[b + 2], 0, origW);
            double y1 = Math.Clamp(preds[b + 3], 0, origH);
            double x2 = Math.Clamp(preds[b + 4], 0, origW);
            double y2 = Math.Clamp(preds[b + 5], 0, origH);
            if (x2 - x1 < 1 || y2 - y1 < 1) continue;

            int cls = (int)preds[b];
            regions.Add(new LayoutRegion(
                new RectD(x1 / origW, y1 / origH, (x2 - x1) / origW, (y2 - y1) / origH),
                cls >= 0 && cls < ClassNames.Length ? ClassNames[cls] : "text",
                score));
        }

        return regions;
    }
}
