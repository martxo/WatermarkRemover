using System.IO;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors; // ORT vendors DenseTensor here — not System.Numerics.Tensors
using SkiaSharp;
using WatermarkRemover.Core.Features.Layout;

namespace WatermarkRemover.Ocr.Layout;

/// <summary>
/// Generic document layout block powered by the DocLayout-YOLO ONNX model (DocStructBench
/// classes, ~75 MB, bundled in <c>Models\</c>). Detects titles / text / tables / figures and
/// returns them as regions; the pipeline then recognises each crop with the chosen text engine.
/// Probing only checks the model file so the startup chain stays cheap; the session loads
/// eagerly when the block is actually selected or the settings page probes it.
/// </summary>
public sealed class DocLayoutYoloStage : ILayoutStage
{
    public const string ModelFile = "doclayout_yolo_docstructbench_imgsz1024.onnx";

    /// <summary>One session — recognising calls must not overlap.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly InferenceSession _session;
    private readonly IReadOnlyDictionary<int, string> _names;

    private DocLayoutYoloStage(InferenceSession session, IReadOnlyDictionary<int, string> names)
    {
        _session = session;
        _names = names;
    }

    public string Name => "DocLayout-YOLO 通用版面";

    /// <summary>
    /// Loads the bundled model (resolved via <see cref="AppAssets"/>) and reads its class-name
    /// metadata. Returns false when the file is missing or ONNX Runtime rejects it — the chain
    /// then falls through to the next block.
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

        try
        {
            var session = new InferenceSession(path);
            stage = new DocLayoutYoloStage(session, ParseNames(session.ModelMetadata));
            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"DocLayout-YOLO 模型加载失败：{ex.Message}";
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

                YoloPrePost.Letterboxed geo = YoloPrePost.Letterbox(bitmap);
                (float[] preds, int rows, int cols) = RunSession(geo);
                List<LayoutRegion> regions = YoloPrePost.ParseRegions(preds, rows, cols, geo, _names);

                // Region dump for manual box verification during real-sample validation.
                OcrLog.Write($"[版面] DocLayout-YOLO 检出 {regions.Count} 区：" +
                    string.Join("；", regions.Take(12).Select(r => $"{r.Kind}({r.Bounds})")));
                return regions;
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private (float[] Preds, int Rows, int Cols) RunSession(YoloPrePost.Letterboxed geo)
    {
        var inputTensor = new DenseTensor<float>(
            geo.Tensor, new[] { 1, 3, geo.TensorHeight, geo.TensorWidth });
        var input = NamedOnnxValue.CreateFromTensor(
            _session.InputMetadata.Keys.First(), inputTensor);
        using var results = _session.Run(new[] { input });
        var output = results.First().AsTensor<float>();

        // Expected layout [1, N, 6] = x1,y1,x2,y2,conf,cls; accept [N, 6] as well.
        List<int> shape = output.Dimensions.ToArray().ToList();
        while (shape.Count > 2 && shape[0] == 1) shape.RemoveAt(0);
        if (shape.Count != 2)
            throw new InvalidDataException(
                $"版面模型输出形状异常：[{string.Join(",", output.Dimensions.ToArray())}]");

        var preds = new float[shape[0] * shape[1]];
        int i = 0;
        foreach (float v in output) preds[i++] = v;
        return (preds, shape[0], shape[1]);
    }

    /// <summary>Model metadata "names" arrives as a Python dict literal, e.g. {0: 'title', …}.</summary>
    private static IReadOnlyDictionary<int, string> ParseNames(ModelMetadata? metadata)
    {
        var names = new Dictionary<int, string>();
        if (metadata?.CustomMetadataMap?.TryGetValue("names", out string? raw) != true || raw is null)
            return names;

        foreach (Match m in Regex.Matches(raw, @"(\d+)\s*:\s*['""]([^'""]*)['""]"))
            names[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        return names;
    }
}
