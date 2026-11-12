using System.Collections.Concurrent;
using System.IO;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Watches the directories where record files live, so external Explorer operations reach the
/// record rows the way a code editor follows renames: a rename updates the row's paths and name,
/// a delete marks the row, a re-created file clears the mark. One <see cref="FileSystemWatcher"/>
/// per directory (no subdirectories — record files sit directly in their output folders).
///
/// Driver events arrive in bursts (one Explorer rename can surface several); a short debounce
/// collapses them, then the debounced events are raised as three plain callbacks. They fire on
/// timer-pool threads — the owner marshals onto its own context.
/// </summary>
public sealed class OutputFileWatcher : IDisposable
{
    /// <summary>Events inside this window collapse into one dispatch.</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(400);

    private readonly Action<string>? _trace;
    private readonly Dictionary<string, FileSystemWatcher> _watchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WatchEvent> _queue = new();
    private readonly System.Timers.Timer _flush;
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>The file moved: (old absolute path, new absolute path).</summary>
    public event Action<string, string>? FileRenamed;

    /// <summary>The file disappeared from a watched directory.</summary>
    public event Action<string>? FileDeleted;

    /// <summary>A file (re-)appeared in a watched directory.</summary>
    public event Action<string>? FileCreated;

    public OutputFileWatcher(Action<string>? trace = null)
    {
        _trace = trace;
        _flush = new System.Timers.Timer(DebounceWindow.TotalMilliseconds) { AutoReset = false };
        _flush.Elapsed += (_, _) => Flush();
    }

    /// <summary>
    /// Re-points the watcher set at these directories: missing ones are added, stale ones are
    /// disposed. Non-existent directories are skipped (records whose file is gone watch nothing).
    /// </summary>
    public void SetDirectories(IEnumerable<string> directories)
    {
        List<string> wanted = directories
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_sync)
        {
            if (_disposed) return;

            foreach (string dir in _watchers.Keys
                         .Where(d => !wanted.Contains(d, StringComparer.OrdinalIgnoreCase))
                         .ToList())
            {
                _watchers[dir].Dispose();
                _watchers.Remove(dir);
                _trace?.Invoke($"文件监视：已移除 {dir}");
            }

            foreach (string dir in wanted)
            {
                if (_watchers.ContainsKey(dir)) continue;
                try
                {
                    var watcher = new FileSystemWatcher(dir)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                        IncludeSubdirectories = false,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Created += OnCreated;
                    watcher.Deleted += OnDeleted;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += (_, e) =>
                        _trace?.Invoke($"文件监视出错（{dir}）：{e.GetException().Message}");
                    watcher.EnableRaisingEvents = true;
                    _watchers[dir] = watcher;
                    _trace?.Invoke($"文件监视：已开始监视 {dir}");
                }
                catch (Exception ex)
                {
                    _trace?.Invoke($"文件监视：无法监视 {dir}（{ex.Message}）");
                }
            }
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath) || string.IsNullOrEmpty(e.OldFullPath)) return;
        Enqueue(new WatchEvent("renamed", e.OldFullPath, e.FullPath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath)) return;
        Enqueue(new WatchEvent("deleted", string.Empty, e.FullPath));
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath)) return;
        Enqueue(new WatchEvent("created", string.Empty, e.FullPath));
    }

    private void Enqueue(WatchEvent item)
    {
        lock (_queue)
        {
            _queue.Add(item);
            _flush.Stop();
            _flush.Start();
        }
    }

    private void Flush()
    {
        WatchEvent[] events;
        lock (_queue)
        {
            if (_queue.Count == 0) return;
            events = _queue.ToArray();
            _queue.Clear();
        }

        // 原子替换（File.Move(temp, 目标, overwrite) / File.Replace）会同时发出
        // Deleted(目标) 和 Renamed(临时→目标) / Created(目标)。同窗口内"目标被删又回来"
        // 是一次内容替换，不是删除——不能让记录行挂上「已被移走」的误报
        // （1.16.137：预览条每次保存都会触发，扫描记录全部被误标）。
        var replaced = events
            .Where(e => e.Kind != "deleted")
            .Select(e => e.NewPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in events)
        {
            switch (item.Kind)
            {
                case "renamed":
                    FileRenamed?.Invoke(item.OldPath, item.NewPath);
                    break;
                case "deleted":
                    if (replaced.Contains(item.NewPath)) continue; // 替换写入，不是删除
                    FileDeleted?.Invoke(item.NewPath);
                    break;
                case "created":
                    FileCreated?.Invoke(item.NewPath);
                    break;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _flush.Stop();
        _flush.Dispose();
        lock (_watchers)
        {
            foreach (FileSystemWatcher watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
        }
    }

    private sealed record WatchEvent(string Kind, string OldPath, string NewPath);
}
