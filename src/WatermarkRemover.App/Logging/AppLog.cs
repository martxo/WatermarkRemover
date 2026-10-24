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
    private static StreamWriter? _writer;
    private static DateTime _writerDate;
    private static int _unflushed;

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppFolders.LogDirectory);
            PurgeOldLogs();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (Sync) Flush();
            };
        }
        catch
        {
            // Logging must never break startup.
        }
    }

    public static void Info(string message) => Write("INFO", message, exception: null);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", message, exception);

    /// <summary>
    /// Adapter for Core trace callbacks (<c>Action&lt;string, Exception?&gt;</c>): non-exception
    /// lines land at INFO, failures at ERROR with their details — wired as one shared lambda by
    /// every feature and pipeline that records execution traces.
    /// </summary>
    public static void Trace(string message, Exception? exception = null)
    {
        if (exception is null) Info(message);
        else Error(message, exception);
    }

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (exception != null)
                line += Environment.NewLine + exception;

            lock (Sync)
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                _unflushed++;

                // Flush errors immediately (they are what survives a crash), and otherwise batch:
                // one cached writer avoids opening/closing the file for every OCR trace line.
                if (exception is not null || _unflushed >= 16) Flush();
            }
        }
        catch
        {
            // Swallow: a failing logger must not take the app down.
        }
    }

    /// <summary>Caller holds <see cref="Sync"/>. Opens (or rolls) the daily file once.</summary>
    private static void EnsureWriter()
    {
        DateTime today = DateTime.Today;
        if (_writer is not null && _writerDate == today) return;

        Flush();
        _writer?.Dispose();
        string path = Path.Combine(AppFolders.LogDirectory, $"app-{today:yyyyMMdd}.log");
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
        _writerDate = today;
        _unflushed = 0;
    }

    /// <summary>Caller holds <see cref="Sync"/>.</summary>
    private static void Flush()
    {
        if (_writer is null) return;

        try
        {
            _writer.Flush();
            _unflushed = 0;
        }
        catch
        {
            // A broken flush must never throw from a log call.
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
