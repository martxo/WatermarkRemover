namespace WatermarkRemover.Core.Updates;

/// <summary>Parsing and comparison of release versions used by the update channel.</summary>
public static class UpdateVersion
{
    /// <summary>
    /// Parses a release tag such as "v1.2.3" or "1.2.3". Prerelease suffixes ("v1.2.3-beta") and
    /// unparseable tags return <c>null</c> — the update channel never offers a version it cannot
    /// compare reliably.
    /// </summary>
    public static Version? ParseReleaseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        string text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out Version? version)
            ? new Version(version.Major, version.Minor, Math.Max(version.Build, 0)) // normalize: "v1.2" → 1.2.0
            : null;
    }

    /// <summary>True when the candidate release is strictly newer than the running version.</summary>
    public static bool IsNewer(Version current, Version candidate) => candidate > current;
}
