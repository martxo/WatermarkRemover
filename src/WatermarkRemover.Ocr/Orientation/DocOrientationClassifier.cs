using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace WatermarkRemover.Ocr.Orientation;

/// <summary>
/// 整页方向分类器（盲扫主判，v1.16.83）：PP-LCNet_x1_0_doc_ori ONNX 导出（≈7 MB，模型动物园
/// 获取后放入 <c>Models\</c>）。输入 1×3×224×224（ImageNet mean/std），输出 4 类角度
/// logits；softmax 后取 top-1 与置信度。加载即验证输入输出契约——社区再导出若漂移，按
/// 「不可用」注册并回退整份同转，绝不因模型问题让扫描失败。
/// 一次性 InferenceSession；<c>Run</c> 线程安全（ORT 保证），采集队列深度 2 下最多两个并发推理。
/// </summary>
public sealed class DocOrientationClassifier : IDisposable
{
    /// <summary>PaddleX 官方 PP-LCNet_x1_0_doc_ori 的 ONNX 导出（7 MB 量级）。</summary>
    public const string ModelFile = "PP-LCNet_x1_0_doc_ori.onnx";

    private const int InputSize = 224;

    // ImageNet normalisation（PaddleX 分类预处理契约，RGB 顺序）。
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly InferenceSession _session;
    private readonly string _imageInput;
    private readonly string _logitsOutput;

    private DocOrientationClassifier(InferenceSession session, string imageInput, string logitsOutput)
    {
        _session = session;
        _imageInput = imageInput;
        _logitsOutput = logitsOutput;
    }

    public string Name => "PP-LCNet 文档方向分类";

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// Loads the bundled model and validates its contract: one 4-D image input and one rank-2
    /// output with exactly 4 columns (0°/90°/180°/270°). Anything else reports unavailable so
    /// the pipeline degrades to the whole-document vote instead of failing.
    /// </summary>
    public static bool TryCreate(out DocOrientationClassifier? classifier, out string reason)
    {
        classifier = null;
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
            string? logits = session.OutputMetadata
                .FirstOrDefault(kv => kv.Value.Dimensions.Length == 2 &&
                                      kv.Value.Dimensions[^1] == 4).Key
                ?? session.OutputMetadata
                    .FirstOrDefault(kv => kv.Value.Dimensions.Length == 2).Key;

            if (image is null || logits is null)
            {
                reason = "模型输入输出契约不符（需要 4 维图像输入 + 4 类输出）";
                session.Dispose();
                return false;
            }

            classifier = new DocOrientationClassifier(session, image, logits);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            session?.Dispose();
            reason = $"方向分类模型加载失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>One page's vote: the clockwise quarter turns that read upright, and how sure.</summary>
    public readonly record struct Vote(int QuarterTurns, float Confidence);

    /// <summary>
    /// Classifies one page PNG. Throws only for decode failures — callers treat that as an
    /// abstain for the page, never as a scan failure.
    /// </summary>
    public Vote Classify(byte[] pagePng)
    {
        using var bitmap = SKBitmap.Decode(pagePng)
            ?? throw new InvalidOperationException("方向分类输入图像解码失败。");
        float[] logits = Run(BuildImageInput(bitmap));
        return Parse(logits);
    }

    /// <summary>校准探针专用：预处理变体开关（通道顺序 / 是否走官方 resize+crop 契约 / 是否先降采样）。</summary>
    internal Vote TestClassify(SKBitmap source, bool bgr, bool directResize, bool downscale)
    {
        using var probe = downscale ? DownscaleLongEdge(source, 1500) : null;
        float[] logits = Run(BuildImageInputVariant(probe ?? source, bgr, directResize));
        return Parse(logits);
    }

    /// <summary>需要缩放时返回新位图（由调用方释放）；无需缩放返回 null（沿用原位图）。</summary>
    private static SKBitmap? DownscaleLongEdge(SKBitmap source, int maxEdge)
    {
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= maxEdge) return null;
        double factor = maxEdge / (double)longest;
        var target = new SKBitmap(
            (int)Math.Round(source.Width * factor), (int)Math.Round(source.Height * factor),
            source.ColorType, source.AlphaType);
        source.ScalePixels(target, new SKSamplingOptions(SKFilterMode.Linear));
        return target;
    }

    private float[] BuildImageInputVariant(SKBitmap source, bool bgr, bool directResize)
    {
        int resizedW, resizedH, cropX, cropY;
        if (directResize)
        {
            resizedW = InputSize;
            resizedH = InputSize;
            cropX = 0;
            cropY = 0;
        }
        else
        {
            int shortSide = Math.Min(source.Width, source.Height);
            double scale = 256.0 / shortSide;
            resizedW = Math.Max(InputSize, (int)Math.Round(source.Width * scale));
            resizedH = Math.Max(InputSize, (int)Math.Round(source.Height * scale));
            cropX = (resizedW - InputSize) / 2;
            cropY = (resizedH - InputSize) / 2;
        }

        using var resized = new SKBitmap(
            new SKImageInfo(resizedW, resizedH, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear)))
            throw new InvalidOperationException("方向分类输入缩放失败。");

        var image = new float[3 * InputSize * InputSize];
        int plane = InputSize * InputSize;
        ReadOnlySpan<byte> px = resized.GetPixelSpan();
        int rowBytes = resized.RowBytes;
        for (int y = 0; y < InputSize; y++)
        {
            int row = (y + cropY) * rowBytes;
            for (int x = 0; x < InputSize; x++)
            {
                int p = row + (x + cropX) * 4; // RGBA
                int i = y * InputSize + x;
                float r = px[p] / 255f, g = px[p + 1] / 255f, b = px[p + 2] / 255f;
                if (bgr) (r, b) = (b, r);
                image[i] = (r - Mean[0]) / Std[0];
                image[plane + i] = (g - Mean[1]) / Std[1];
                image[2 * plane + i] = (b - Mean[2]) / Std[2];
            }
        }

        return image;
    }

    /// <summary>官方预处理契约（inference.yml）：ResizeImage resize_short=256 → CropImage 224
    /// （短边缩放后中心裁方）→ /255 + ImageNet mean/std → CHW。</summary>
    private static float[] BuildImageInput(SKBitmap source)
    {
        int shortSide = Math.Min(source.Width, source.Height);
        double scale = 256.0 / shortSide;
        int resizedW = Math.Max(224, (int)Math.Round(source.Width * scale));
        int resizedH = Math.Max(224, (int)Math.Round(source.Height * scale));
        int cropX = (resizedW - 224) / 2;
        int cropY = (resizedH - 224) / 2;

        using var resized = new SKBitmap(
            new SKImageInfo(resizedW, resizedH, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear)))
            throw new InvalidOperationException("方向分类输入缩放失败。");

        var image = new float[3 * InputSize * InputSize];
        int plane = InputSize * InputSize;
        ReadOnlySpan<byte> px = resized.GetPixelSpan();
        int rowBytes = resized.RowBytes;
        for (int y = 0; y < InputSize; y++)
        {
            int row = (y + cropY) * rowBytes;
            for (int x = 0; x < InputSize; x++)
            {
                int p = row + (x + cropX) * 4; // RGBA
                int i = y * InputSize + x;
                image[i] = (px[p] / 255f - Mean[0]) / Std[0];               // R
                image[plane + i] = (px[p + 1] / 255f - Mean[1]) / Std[1];   // G
                image[2 * plane + i] = (px[p + 2] / 255f - Mean[2]) / Std[2]; // B
            }
        }

        return image;
    }

    /// <summary>校准探针专用：返回网络原始输出（判定其是否已是概率分布）。</summary>
    internal float[] TestRawLogits(SKBitmap source, bool bgr, bool directResize, bool downscale)
    {
        using var probe = downscale ? DownscaleLongEdge(source, 1500) : null;
        return Run(BuildImageInputVariant(probe ?? source, bgr, directResize));
    }

    private float[] Run(float[] image)
    {
        var tensor = new DenseTensor<float>(image, new[] { 1, 3, InputSize, InputSize });
        using var results = _session.Run(new[]
            { NamedOnnxValue.CreateFromTensor(_imageInput, tensor) });
        var output = results.First(r => r.Name == _logitsOutput).AsTensor<float>();
        var logits = new float[4];
        // Walk the tensor so odd batch/column layouts still land in the right slots.
        int i = 0;
        foreach (float v in output)
        {
            if (i < 4) logits[i] = v;
            i++;
        }
        return logits;
    }

    /// <summary>
    /// 解析网络输出。实测（2026-11-03，探针校准）：
    /// ① ONNX 图内已含 softmax——输出是概率分布（官方 180° demo 图 raw = [0.04, 0.04, 0.88, 0.04]，
    ///    sum=1.000），这里直接取 max 作置信度，绝不能再 softmax（会压平成 ~0.43）；
    /// ② 类别语义 = 图片「当前朝向」而非「需转角度」——倒置 demo 图 top=类2（180°）、
    ///    正向真实扫描页 top=类0、人工顺时针转 90° 后 top=类1、转 270° 后 top=类3。
    ///    因此需转的顺时针四分之一转数 = (4 − 类别) % 4。
    /// 两点均由 eval/orientation/calibration 的可验证夹具钉住（MappingCalibrationTests），
    /// 换模型再导出时必须重跑该校准——系统性镜像会在这里现形。
    /// </summary>
    internal static Vote Parse(float[] probabilities)
    {
        int maxIndex = 0;
        for (int i = 1; i < probabilities.Length; i++)
        {
            if (probabilities[i] > probabilities[maxIndex]) maxIndex = i;
        }

        return new Vote((4 - maxIndex % 4) % 4, probabilities[maxIndex]);
    }
}
