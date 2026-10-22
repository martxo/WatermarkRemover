using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WatermarkRemover.Index.Contracts;

/// <summary>One request over the index pipe. Exactly one round-trip per connection.</summary>
public sealed record IndexRequest
{
    public string Cmd { get; init; } = "status";

    public string? Query { get; init; }

    public int Max { get; init; } = 200;
}

public sealed record IndexStatusDto(
    bool Ready,
    bool Scanning,
    int Files,
    int Folders,
    string[] Volumes,
    string? Error);

public sealed record IndexHitDto(
    string Path,
    bool IsFolder,
    long Size,
    DateTime? ModifiedAt);

public sealed record IndexResponse
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public IndexStatusDto? Status { get; init; }

    public IReadOnlyList<IndexHitDto>? Results { get; init; }

    public static IndexResponse Failure(string error) => new() { Ok = false, Error = error };
}

public static class IndexJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Paths are usually non-ASCII here; escaping every CJK char to \uXXXX would quintuple
        // the payload for no benefit on a local pipe. Quotes/control chars stay escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
