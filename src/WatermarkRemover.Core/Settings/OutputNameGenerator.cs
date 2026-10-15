namespace WatermarkRemover.Core.Settings;

/// <summary>Computes output file paths from input paths and settings.</summary>
public static class OutputNameGenerator
{
    /// <summary>
    /// Returns the output path for <paramref name="inputPath"/>. The suffix is inserted before the
    /// extension, e.g. <c>scan.pdf</c> → <c>scan_no-watermark.pdf</c>. If that name already exists,
    /// a counter is appended (<c>scan_no-watermark (2).pdf</c>) so no work is ever overwritten.
    /// </summary>
    public static string GetOutputPath(string inputPath, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Input path must not be empty.", nameof(inputPath));

        string directory = settings.OutputMode switch
        {
            OutputMode.CustomDirectory when !string.IsNullOrWhiteSpace(settings.OutputDirectory) =>
                settings.OutputDirectory,
            _ => Path.GetDirectoryName(inputPath) ?? string.Empty,
        };

        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string extension = Path.GetExtension(inputPath);
        string suffix = SanitizeSuffix(settings.OutputSuffix);

        string candidate = Path.Combine(directory, stem + suffix + extension);
        if (!File.Exists(candidate)) return candidate;

        for (int n = 2; n < 1000; n++)
        {
            candidate = Path.Combine(directory, $"{stem}{suffix} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem}{suffix} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>Strips characters that are invalid in file names.</summary>
    public static string SanitizeSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return "_no-watermark";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = suffix.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string result = new(chars);
        return result.Trim().Length == 0 ? "_no-watermark" : result;
    }
}
