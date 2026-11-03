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

    /// <summary>224×224 direct resize (contract ignores aspect), /255 then ImageNet mean/std, RGB.</summary>
    private static float[] BuildImageInput(SKBitmap source)
    {
        using var resized = new SKBitmap(
            new SKImageInfo(InputSize, InputSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear)))
            throw new InvalidOperationException("方向分类输入缩放失败。");

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
                image[i] = (px[p] / 255f - Mean[0]) / Std[0];               // R
                image[plane + i] = (px[p + 1] / 255f - Mean[1]) / Std[1];   // G
                image[2 * plane + i] = (px[p + 2] / 255f - Mean[2]) / Std[2]; // B
            }
        }

        return image;
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

    /// <summary>Class index → clockwise quarter turns. The doc_ori classes are 0/90/180/270
    /// clockwise; the mapping is pinned by the layered evaluation harness (评测夹具) — a re-export
    /// that flips the convention shows up there as a systematic 误翻 and must be remapped here.</summary>
    internal static Vote Parse(float[] logits)
    {
        float max = float.NegativeInfinity;
        int maxIndex = 0;
        double sum = 0;
        var exp = new double[logits.Length];
        for (int i = 0; i < logits.Length; i++)
        {
            exp[i] = Math.Exp(logits[i]);
            sum += exp[i];
            if (logits[i] > max)
            {
                max = logits[i];
                maxIndex = i;
            }
        }

        return new Vote(maxIndex % 4, (float)(exp[maxIndex] / Math.Max(sum, double.Epsilon)));
    }
}
