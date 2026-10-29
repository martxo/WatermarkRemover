using System.IO;

namespace WatermarkRemover.Ocr;

/// <summary>
/// Where the OCR models live: a <c>Models</c> folder beside the host executable, the same
/// deploy-layout contract the app ships. Library consumers that bundle their own models can
/// point <see cref="ModelsDirectory"/> at another folder before the first probe.
/// </summary>
public static class OcrAssets
{
    public const string ModelsFolderName = "Models";

    public static string ModelsDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, ModelsFolderName);

    public static string Model(string fileName) => Path.Combine(ModelsDirectory, fileName);
}
