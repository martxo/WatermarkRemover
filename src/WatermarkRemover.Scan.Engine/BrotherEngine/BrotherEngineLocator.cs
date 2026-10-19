namespace WatermarkRemover.Scan.Engine.BrotherEngine;

/// <summary>
/// Resolves the two on-disk pieces of the Brother engine channel: the x86 bridge process
/// (built with this solution) and the official 32-bit ScanCoreApi.dll (installed with
/// Brother iPrint&amp;Scan). Either missing means the channel is unavailable and the WIA
/// source stays in charge, so every lookup stays quiet and returns null.
/// </summary>
public static class BrotherEngineLocator
{
    /// <summary>Environment override for tests and exotic layouts.</summary>
    private const string HelperOverrideVariable = "WATERMARK_REMOVER_BROTHER_ENGINE_EXE";

    private const string HelperName = "WatermarkRemover.Scan.Bridge.exe";

    public static string? FindHelperExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable(HelperOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        var baseDirectory = AppContext.BaseDirectory;

        // Publish layout: the bridge sits beside the app (flat or in a BrotherEngine folder).
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDirectory, HelperName),
                     Path.Combine(baseDirectory, "BrotherEngine", HelperName),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Dev layout: walk up from bin/.../net8.0-windows to the repo root and into the
        // bridge project's own output tree (x86 build lands in the same configuration).
        var current = new DirectoryInfo(baseDirectory);
        for (var depth = 0; current is not null && depth < 6; depth++)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    current.FullName, "src", "WatermarkRemover.Scan.Bridge", "bin", configuration,
                    "net8.0-windows", HelperName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>Directory that ships the official native engine, or null when the app is absent.</summary>
    public static string? FindOfficialEngineDirectory()
    {
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                 })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var path = Path.Combine(root, "Brother", "iPrint&Scan", "ScanCoreApi.dll");
            if (File.Exists(path))
            {
                return Path.GetDirectoryName(path);
            }
        }

        return null;
    }

    public static bool IsChannelPresent() =>
        FindHelperExecutable() is not null && FindOfficialEngineDirectory() is not null;
}
