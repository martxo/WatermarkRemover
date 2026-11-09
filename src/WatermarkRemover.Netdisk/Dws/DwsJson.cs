using System.Text.Json;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// Tolerant readers for dws JSON. The CLI's shortcut commands normalize platform fields but keep
/// several alias keys (nodeId/dentryUuid/id, name/fileName …), and some envelopes nest the payload
/// under "data" — every reader walks the documented aliases instead of pinning one shape.
/// </summary>
public static class DwsJson
{
    /// <summary>Descends into the "data" wrapper when the envelope has one.</summary>
    public static JsonElement Payload(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            return data;
        }

        return root;
    }

    public static string? FirstString(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    public static long? FirstLong(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static bool FirstBool(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (value.ValueKind == JsonValueKind.String &&
                    bool.TryParse(value.GetString(), out var parsed) && parsed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The first array under any of the keys, from an object (or its "data" wrapper).</summary>
    public static JsonElement? FirstArray(JsonElement element, params string[] keys)
    {
        var payload = Payload(element);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Node id out of a dws object: nodeId/dentryUuid/fileId/id aliases, one level deep.</summary>
    public static string? NodeId(JsonElement element) =>
        FirstString(element, "nodeId", "dentryUuid", "fileId", "dentryId", "id");

    /// <summary>True when the entry is a folder under any of dws's type spellings.</summary>
    public static bool IsFolder(JsonElement item)
    {
        var type = FirstString(item, "type", "nodeType", "fileType")?.ToLowerInvariant();
        return type is "folder" or "dir" or "directory";
    }

    /// <summary>Modification time out of the millisecond-epoch (or ISO) spellings dws uses.</summary>
    public static DateTimeOffset? ModifiedAt(JsonElement item)
    {
        var ms = FirstLong(item, "modifiedTime", "modifiedAt", "updateTime", "lastModifiedTime");
        if (ms is > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
        }

        if (FirstString(item, "modifiedTime", "modifiedAt", "updateTime") is { } iso &&
            DateTimeOffset.TryParse(iso, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
