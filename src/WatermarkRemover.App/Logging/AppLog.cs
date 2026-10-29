using System.Diagnostics;
using System.IO;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Logging;

/// <summary>
/// The app's file logger: one daily file under the app data root, entries appended under a lock
/// so concurrent job threads never interleave. Files older than 7 days are removed on startup.
/// Never throws: logging must not take the app down.
///
/// Lines carry a structured prefix (level, category, operation id, thread) so one user action
/// can be traced end to end — see <see cref="BeginOperation"/>. The minimum level defaults to
/// DEBUG (the detail needed for performance work) and can be overridden per machine with the
/// <c>WMR_LOG_LEVEL</c> environment variable.
/// </summary>
public static class AppLog
{
    private const int FlushAfterLines = 16;

    /// <summary>Plain lines are flushed at least this often, so a hard kill loses at most ~1s.</summary>
    private const long MaxFlushIntervalMs = 1000;

    private static readonly object Sync = new();
    private static readonly AsyncLocal<OperationContext?> Ambient = new();
    private static readonly Process Self = Process.GetCurrentProcess();
    private static StreamWriter? _writer;
    private static DateTime _writerDate;
    private static int _unflushed;
    private static long _lastFlushTicks = Environment.TickCount64;
    private static int _nextOperationId;
    private static LogLevel _minimumLevel = ResolveMinimumLevel();

    /// <summary>Entries below this level are dropped; <see cref="LogLevel.Error"/> always writes.</summary>
    public static LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppFolders.LogDirectory);
            PurgeOldLogs();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (Sync) FlushLocked();
            };
        }
        catch
        {
            // Logging must never break startup.
        }
    }

    public static void Info(string message) => Write(LogLevel.Info, message, exception: null);

    public static void Debug(string message) => Write(LogLevel.Debug, message, exception: null);

    public static void Warn(string message, Exception? exception = null) =>
        Write(LogLevel.Warn, message, exception);

    public static void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, message, exception);

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

    /// <summary>
    /// Starts a correlated operation. A root operation allocates a six-digit id; nested
    /// operations inherit the active id, so every line emitted inside the scope (including
    /// Core trace callbacks) carries the same <c>[op=…]</c> marker. The scope writes a start
    /// line, per-step timings (<see cref="OperationScope.Step"/>) and one completion line.
    /// </summary>
    /// <param name="slowMs">Completion at or above this duration logs as WARN; 0 disables the warning.</param>
    public static OperationScope BeginOperation(
        string category,
        string name,
        string? detail = null,
        LogLevel startLevel = LogLevel.Info,
        long slowMs = 3000) =>
        new(category, name, detail, startLevel, slowMs);

    /// <summary>Flushes buffered lines to disk. Safe to call from any thread, never throws.</summary>
    public static void Flush()
    {
        lock (Sync) FlushLocked();
    }

    /// <summary>Final flush + writer release for a clean shutdown.</summary>
    public static void Shutdown()
    {
        try
        {
            lock (Sync)
            {
                FlushLocked();
                _writer?.Dispose();
                _writer = null;
            }
        }
        catch
        {
            // A shutting-down logger must not throw.
        }
    }

    /// <summary>
    /// Working set / private bytes / managed heap / gen-2 collection snapshot for performance
    /// lines. The process object is refreshed first: a cached one reported the same working set
    /// for hours while the managed heap moved by gigabytes, which made support logs unreadable.
    /// </summary>
    public static string MemorySnapshot()
    {
        try
        {
            Self.Refresh();
            return $"工作集 {Self.WorkingSet64 / 1048576} MB（私有 {Self.PrivateMemorySize64 / 1048576} MB，" +
                   $"托管 {GC.GetTotalMemory(false) / 1048576} MB，GC2 {GC.CollectionCount(2)}）";
        }
        catch
        {
            return "内存不可读";
        }
    }

    /// <summary>Creates a nested or root context for an operation and makes it the ambient one.</summary>
    internal static (OperationContext Context, OperationContext? Previous) EnterOperation(string category)
    {
        OperationContext? previous = Ambient.Value;
        var context = previous is null
            ? new OperationContext(Interlocked.Increment(ref _nextOperationId).ToString("D6"), category)
            : new OperationContext(previous.Id, category);
        Ambient.Value = context;
        return (context, previous);
    }

    /// <summary>Restores the ambient context, but only when this scope is still the active one.</summary>
    internal static void ExitOperation(OperationContext context, OperationContext? previous)
    {
        if (ReferenceEquals(Ambient.Value, context))
            Ambient.Value = previous;
    }

    /// <summary>Writes under an explicit context so completion lines keep their own op id.</summary>
    internal static void WriteFor(
        OperationContext context, LogLevel level, string message, Exception? exception = null) =>
        Write(level, message, exception, context);

    private static LogLevel ResolveMinimumLevel()
    {
        string? raw = Environment.GetEnvironmentVariable("WMR_LOG_LEVEL");
        return Enum.TryParse(raw, ignoreCase: true, out LogLevel parsed) ? parsed : LogLevel.Debug;
    }

    private static void Write(
        LogLevel level, string message, Exception? exception, OperationContext? context = null)
    {
        try
        {
            // Errors survive even the most verbose filter: they are what a crash report needs.
            if (level < _minimumLevel && level != LogLevel.Error) return;

            OperationContext? active = context ?? Ambient.Value;
            string line = LogLine.Format(
                DateTime.Now, level, active?.Category, active?.Id,
                Environment.CurrentManagedThreadId, message);
            if (exception != null)
                line += Environment.NewLine + exception;

            lock (Sync)
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                _unflushed++;

                // Flush errors immediately (they are what survives a crash), and otherwise batch:
                // one cached writer avoids opening/closing the file for every OCR trace line.
                if (exception is not null ||
                    _unflushed >= FlushAfterLines ||
                    Environment.TickCount64 - _lastFlushTicks >= MaxFlushIntervalMs)
                {
                    FlushLocked();
                }
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

        FlushLocked();
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
    private static void FlushLocked()
    {
        if (_writer is null) return;

        try
        {
            _writer.Flush();
            _unflushed = 0;
            _lastFlushTicks = Environment.TickCount64;
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

/// <summary>Ambient correlation state: one operation id plus the category currently writing.</summary>
internal sealed record OperationContext(string Id, string Category);
