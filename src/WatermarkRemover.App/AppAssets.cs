using System.IO;

namespace WatermarkRemover.App;

/// <summary>
/// The deploy-layout contract: where every asset that ships beside the executable lives.
/// Single-file publishing silently decides whether an asset ends up inside the exe or next to it,
/// which has repeatedly broken runtime loading (wcocr.dll, PP-OCR models, dev artifacts). Asset
/// paths are therefore resolved in exactly one place — here — and the <c>VerifyPublishLayout</c>
/// gate in the csproj fails the build when the published tree stops matching the manifest.
/// Adding a new sidecar asset touches three places, nothing else: the csproj copy item, the csproj
/// <c>SidecarAsset</c> manifest, and a property in this class.
/// </summary>
public static class AppAssets
{
    /// <summary>Folder beside the exe that holds the PP-OCR model set.</summary>
    public const string ModelsFolderName = "Models";

    public static string ModelsDirectory => Path.Combine(AppContext.BaseDirectory, ModelsFolderName);

    /// <summary>Resolves one file inside <see cref="ModelsDirectory"/>.</summary>
    public static string Model(string fileName) => Path.Combine(ModelsDirectory, fileName);
}
