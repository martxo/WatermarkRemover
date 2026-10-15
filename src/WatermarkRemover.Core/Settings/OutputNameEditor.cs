namespace WatermarkRemover.Core.Settings;

/// <summary>
/// Pure file-name editing rules used by the job list: renaming a generated file and toggling
/// quick suffixes. Deliberately free of IO so every rule is unit-testable; callers compute the
/// target name here and perform the actual file move themselves.
/// </summary>
public static class OutputNameEditor
{
    /// <summary>
    /// Computes the new file name for a user-typed name. The current extension is always kept —
    /// the output must remain the same document type, so a typed extension like ".txt" is dropped.
    /// Invalid file-name characters become underscores; trailing dots and spaces are trimmed
    /// (Windows forbids them). Returns null when nothing usable remains.
    /// </summary>
    public static string? BuildRenamedName(string currentFileName, string desiredName)
    {
        if (string.IsNullOrWhiteSpace(currentFileName) || string.IsNullOrWhiteSpace(desiredName))
            return null;

        string extension = Path.GetExtension(currentFileName);
        string stem = SanitizeStem(Path.GetFileNameWithoutExtension(desiredName.Trim()));

        return stem.Length == 0 ? null : stem + extension;
    }

    /// <summary>
    /// Returns <paramref name="currentFileName"/> with <paramref name="suffix"/> toggled: appended
    /// to the stem when absent, removed when already present (so a mis-click is reversible).
    /// </summary>
    public static string WithSuffix(string currentFileName, string suffix)
    {
        string token = SanitizeSuffixToken(suffix);
        if (token.Length == 0 || string.IsNullOrWhiteSpace(currentFileName))
            return currentFileName;

        string stem = Path.GetFileNameWithoutExtension(currentFileName);
        string extension = Path.GetExtension(currentFileName);
        stem = StemEndsWithSuffix(stem, token)
            ? stem[..^token.Length].TrimEnd()
            : stem + token;

        return stem + extension;
    }

    /// <summary>Whether the file name stem already ends with the given suffix.</summary>
    public static bool IsSuffixApplied(string currentFileName, string suffix)
    {
        string token = SanitizeSuffixToken(suffix);
        if (token.Length == 0 || string.IsNullOrWhiteSpace(currentFileName)) return false;

        string stem = Path.GetFileNameWithoutExtension(currentFileName);
        return StemEndsWithSuffix(stem, token);
    }

    /// <summary>Strips characters that are invalid in file names for a quick-suffix chip.</summary>
    public static string SanitizeSuffixToken(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = suffix.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    private static string SanitizeStem(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = stem.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string result = new string(chars).Trim().TrimEnd('.', ' ');

        // A stem made entirely of underscores means the user typed nothing but illegal characters.
        return result.All(c => c == '_') ? string.Empty : result;
    }

    private static bool StemEndsWithSuffix(string stem, string token) =>
        stem.EndsWith(token, StringComparison.Ordinal) && stem.Length > token.Length;
}
