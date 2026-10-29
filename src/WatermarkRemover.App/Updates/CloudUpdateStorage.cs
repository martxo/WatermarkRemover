using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Where the cloud-update machinery keeps its state. One place, so the orchestrator, the apply
/// script writer and the local-index cache agree on the layout without passing paths around.
/// </summary>
internal static class CloudUpdatePaths
{
    public static string State { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WatermarkRemover", "cloud-updates");

    public static string ChunkCache => Path.Combine(State, "chunks");
    public static string Staging => Path.Combine(State, "staging");
    public static string Backup => Path.Combine(State, "backup");
    public static string LocalIndex => Path.Combine(State, "local-index.json");
    public static string History => Path.Combine(State, "history.json");
    public static string Result => Path.Combine(State, "last-apply.json");
}

/// <summary>The one serializer shape for cloud-update state files (camelCase, tolerant reads).</summary>
internal static class CloudUpdateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>SHA-256 helpers shared by the index cache and the staging checks.</summary>
internal static class CloudHash
{
    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256HexFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
