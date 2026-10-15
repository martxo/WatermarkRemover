using System.IO;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Logging;

/// <summary>
/// Minimal file logger: one daily file under the app data root, entries appended under a lock so
/// concurrent job threads never interleave. Files older than 7 days are removed on startup.
/// Never throws: logging must not take the app down.
/// </summary>
public static class AppLog
{
    private static readonly object Sync = new();

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppFolders.LogDirectory);
            PurgeOldLogs();
        }
        catch
        {
            // Logging must never break startup.
        }
    }

    public static void Info(string message) => Write("INFO", message, exception: null);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (exception != null)
                line += Environment.NewLine + exception;

            lock (Sync)
            {
                string path = Path.Combine(
                    AppFolders.LogDirectory,
                    $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Swallow: a failing logger must not take the app down.
        }
    }

    private static void PurgeOldLogs()
    {
        foreach (string file in Directory.EnumerateFiles(AppFolders.LogDirectory, "app-*.log"))
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(file); // app-yyyyMMdd
                if (!DateTime.TryParseExact(
                        name.Length >= 4 ? name[4..] : string.Empty,
                        "yyyyMMdd",
                        null,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime date))
                    continue;

                if (date < DateTime.Today.AddDays(-7))
                    File.Delete(file);
            }
            catch
            {
                // Best effort per file.
            }
        }
    }
}
