using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

internal static class AutomaticBackupFileFilter
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".zip", ".rar",
    };

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".tmp", ".temp",
    };

    public static bool IsSupported(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        string name = Path.GetFileName(fullPath);
        if (name.StartsWith("~$", StringComparison.Ordinal) ||
            name.StartsWith("._", StringComparison.Ordinal))
            return false;

        string extension = Path.GetExtension(name);
        return !ExcludedExtensions.Contains(extension) && AllowedExtensions.Contains(extension);
    }

    public static string NormalizeRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string trimmed = rootDirectory.Trim();
        if (!Path.IsPathRooted(trimmed))
            throw new ArgumentException("备份根目录必须是绝对路径。", nameof(rootDirectory));
        string root = Path.GetFullPath(trimmed);
        return Path.TrimEndingDirectorySeparator(root);
    }

    public static bool TryGetRelativePath(string rootDirectory, string fullPath, out string relativePath)
    {
        relativePath = string.Empty;
        try
        {
            string root = NormalizeRoot(rootDirectory);
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
            string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (relative.Length == 0 || relative == "." || Path.IsPathRooted(relative) ||
                relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                return false;

            relativePath = NormalizeRelativePath(relative);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryGetSupportedRelativePath(
        string rootDirectory, string fullPath, out string relativePath)
    {
        relativePath = string.Empty;
        return IsSupported(fullPath) &&
               TryGetRelativePath(rootDirectory, fullPath, out relativePath);
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string value = relativePath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(value) || value.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("相对路径不能指向根目录之外。", nameof(relativePath));

        var parts = new List<string>();
        foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
                throw new ArgumentException("相对路径不能包含上级目录跳转。", nameof(relativePath));
            parts.Add(part);
        }

        if (parts.Count == 0) throw new ArgumentException("相对路径不能为空。", nameof(relativePath));
        return string.Join('/', parts);
    }

    public static string GetRelativeDirectory(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        int separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    public static string CombineRemoteDirectory(string remoteBase, string relativeDirectory)
    {
        string normalizedBase = NormalizeRemoteDirectory(remoteBase);
        string normalizedRelative = NormalizeRelativeDirectory(relativeDirectory);
        if (normalizedRelative.Length == 0) return normalizedBase;
        if (normalizedBase.Length == 0) return normalizedRelative;
        return normalizedBase.TrimEnd('/') + "/" + normalizedRelative;
    }

    public static string ConsentFingerprint(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string material = string.Join('\n',
            "automatic-backup-consent-v1",
            settings.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            NormalizeRoot(settings.AutomaticBackupRootDirectory).Replace('\\', '/'),
            NormalizeConsentValue(settings.AutomaticBackupProviderId),
            NormalizeConsentValue(settings.AutomaticBackupAccountName),
            NormalizeConsentValue(settings.AutomaticBackupDrive),
            NormalizeRemoteDirectory(settings.AutomaticBackupRemoteDirectory));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public static bool HasValidConsent(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.AutomaticBackupEnabled || !settings.AutomaticBackupConsentedAtUtc.HasValue ||
            string.IsNullOrWhiteSpace(settings.AutomaticBackupRootDirectory) ||
            string.IsNullOrWhiteSpace(settings.AutomaticBackupProviderId) ||
            string.IsNullOrWhiteSpace(settings.AutomaticBackupAccountName) ||
            string.IsNullOrWhiteSpace(settings.AutomaticBackupDrive) ||
            string.IsNullOrWhiteSpace(settings.AutomaticBackupRemoteDirectory) ||
            string.IsNullOrWhiteSpace(settings.AutomaticBackupConsentFingerprint))
            return false;

        try
        {
            return string.Equals(
                settings.AutomaticBackupConsentFingerprint.Trim(),
                ConsentFingerprint(settings),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string NormalizeConsentValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private static string NormalizeRelativeDirectory(string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory)) return string.Empty;
        return NormalizeRelativePath(relativeDirectory);
    }

    private static string NormalizeRemoteDirectory(string remoteDirectory)
    {
        if (string.IsNullOrWhiteSpace(remoteDirectory)) return string.Empty;
        string value = remoteDirectory.Trim().Replace('\\', '/');
        bool rooted = value.StartsWith("/", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    throw new ArgumentException("网盘目录不能跳到根目录之外。", nameof(remoteDirectory));
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        string joined = string.Join('/', parts);
        return rooted ? "/" + joined : joined;
    }
}
