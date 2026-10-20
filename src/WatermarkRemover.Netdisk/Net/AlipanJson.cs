using System.Globalization;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk.Net;

/// <summary>
/// Tolerant JSON → model mapping for 阿里云盘 open platform responses. The platform renames fields
/// across endpoint versions (id/user_id, updated_at/modified_at …), so every read takes a list of
/// accepted names and treats a miss as "absent" instead of failing the whole mapping.
/// </summary>
internal static class AlipanJson
{
    /// <summary>Codes meaning "this access token is not accepted" — the session rotates once and replays.</summary>
    internal static bool IsTokenRejected(string? code) =>
        code is "AccessTokenInvalid" or "AccessTokenExpired" or "I400JD";

    /// <summary>Codes meaning the account must re-authorize (token dead, rotated away or scope-less).</summary>
    internal static bool IsAuthError(string? code) =>
        IsTokenRejected(code) ||
        code is "Unauthorized" or "RefreshTokenExpired" or "RefreshTokenInvalid" or "InvalidParameter.RefreshToken";

    internal static NetdiskPage ParseListPage(string json) => ParseListPage(ParseRoot(json));

    internal static NetdiskPage ParseListPage(JsonElement root)
    {
        var items = new List<NetdiskFile>();
        if (TryGetProperty(root, out var array, "items") && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                items.Add(ParseFileNode(element));
            }
        }

        var marker = TryGetString(root, "next_marker");
        return new NetdiskPage(items, string.IsNullOrEmpty(marker) ? null : marker);
    }

    internal static NetdiskFile ParseFileNode(JsonElement element)
    {
        var id = TryGetString(element, "file_id", "id") ?? string.Empty;
        var name = TryGetString(element, "name") ?? string.Empty;
        var isFolder = string.Equals(TryGetString(element, "type"), "folder", StringComparison.OrdinalIgnoreCase);
        var size = TryGetInt64(element, "size") ?? 0;
        var modified = TryGetTime(element, "updated_at", "modified_at", "created_at");
        var parent = TryGetString(element, "parent_file_id");
        return new NetdiskFile(id, name, isFolder, size, modified, parent);
    }

    internal static NetdiskQuota ParseSpaceInfo(string json) => ParseSpaceInfo(ParseRoot(json));

    internal static NetdiskQuota ParseSpaceInfo(JsonElement root)
    {
        // getSpaceInfo wraps the numbers in personal_space_info; tolerate a flat shape too.
        var scope = TryGetProperty(root, out var personal, "personal_space_info") && personal.ValueKind == JsonValueKind.Object
            ? personal
            : root;
        return new NetdiskQuota(
            TryGetInt64(scope, "used_size") ?? 0,
            TryGetInt64(scope, "total_size") ?? 0);
    }

    internal static NetdiskAccountInfo ParseUserInfo(string json) => ParseUserInfo(ParseRoot(json));

    internal static NetdiskAccountInfo ParseUserInfo(JsonElement root)
    {
        var id = TryGetString(root, "id", "user_id", "sub") ?? string.Empty;
        var name = TryGetString(root, "name", "nickname", "display_name") ?? id;
        return new NetdiskAccountInfo(id, name);
    }

    /// <summary>Maps a create/getUploadUrl response's part_info_list to part_number → upload_url.</summary>
    internal static IReadOnlyDictionary<int, string> ParsePartInfoList(JsonElement root)
    {
        var result = new Dictionary<int, string>();
        if (!TryGetProperty(root, out var array, "part_info_list") || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in array.EnumerateArray())
        {
            var number = TryGetInt64(element, "part_number");
            var url = TryGetString(element, "upload_url");
            if (number is null || string.IsNullOrEmpty(url))
            {
                continue;
            }

            result[(int)number.Value] = url;
        }

        return result;
    }

    /// <summary>
    /// Maps an error body to the typed exception. OpenFile errors use {code,message}; the OAuth
    /// token endpoint uses {error,error_description}; gateways may return plain text or nothing.
    /// </summary>
    internal static NetdiskException MapError(int statusCode, string? json, string fallbackMessage)
    {
        string? code = null;
        string? message = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                code = TryGetString(doc.RootElement, "code", "error");
                message = TryGetString(doc.RootElement, "message", "error_description");
            }
            catch (JsonException)
            {
                // Non-JSON error bodies: keep short raw text as the message so logs stay useful.
                message = json.Length <= 200 ? json : null;
            }
        }

        if (code is null && statusCode == 401)
        {
            code = "Unauthorized";
        }

        message ??= fallbackMessage;
        code ??= statusCode.ToString(CultureInfo.InvariantCulture);
        return IsAuthError(code)
            ? new NetdiskAuthException(message, code)
            : new NetdiskException(message, code);
    }

    internal static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static string? TryGetString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    internal static long? TryGetInt64(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    internal static bool? TryGetBool(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    internal static DateTimeOffset? TryGetTime(JsonElement element, params string[] names)
    {
        var raw = TryGetString(element, names);
        return raw is not null &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static JsonElement ParseRoot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
