using System.Diagnostics;
using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.App.Logging;

/// <summary>
/// One measured operation (a scan, a naming pass, one page render/edit, one job). Writes:
/// a start line, per-step timings via <see cref="Step"/>, and one completion line carrying the
/// total duration, outcome and a memory snapshot. Slow operations are escalated to WARN
/// automatically, which is how a 70-document day is triaged without re-instrumenting.
///
/// Disposing without an explicit completion still emits a DEBUG line, so a missed branch never
/// loses the duration.
/// </summary>
public sealed class OperationScope : IDisposable
{
    private readonly string _category;
    private readonly string _name;
    private readonly long _slowMs;
    private readonly long _startTicks;
    private readonly OperationContext _context;
    private readonly OperationContext? _previous;
    private long _lastTicks;
    private long _excludedMs;
    private bool _finished;

    internal OperationScope(
        string category,
        string name,
        string? detail,
        LogLevel startLevel,
        long slowMs)
    {
        _category = string.IsNullOrWhiteSpace(category) ? "APP" : category;
        _name = string.IsNullOrWhiteSpace(name) ? "操作" : name;
        _slowMs = slowMs;
        _startTicks = Stopwatch.GetTimestamp();
        _lastTicks = _startTicks;

        (_context, _previous) = AppLog.EnterOperation(_category);

        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"（{detail}）";
        AppLog.WriteFor(_context, startLevel, $"操作开始[{_category}]：{_name}{suffix}");
    }

    /// <summary>Correlation id shared by every nested operation and line of this scope.</summary>
    public string Id => _context.Id;

    /// <summary>Milliseconds since the operation started.</summary>
    public long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

    /// <summary>
    /// Removes time this operation spent waiting on something else (an open preview strip finishing
    /// its own passes, a lock held by another program) from the slow-operation verdict. The waiting
    /// is still reported, but separately — otherwise a naming pass that only waited 5 s reads as a
    /// 5 s OCR pass and hides the real bottleneck.
    /// </summary>
    public void Exclude(long milliseconds)
    {
        if (milliseconds > 0) Interlocked.Add(ref _excludedMs, milliseconds);
    }

    /// <summary>
    /// Closes the previous step and emits its duration plus the running total. Call it right
    /// after the work being measured.
    /// </summary>
    public void Step(string name, string? detail = null)
    {
        if (_finished) return;

        long now = Stopwatch.GetTimestamp();
        long stageMs = (long)Stopwatch.GetElapsedTime(_lastTicks, now).TotalMilliseconds;
        _lastTicks = now;

        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"，{detail}";
        AppLog.WriteFor(
            _context, LogLevel.Debug,
            $"操作步骤[{_category}]：{name} {stageMs} ms（累计 {ElapsedMs} ms{suffix}）");
    }

    /// <summary>A free-form line under this operation's category and id.</summary>
    public void Note(string message, LogLevel level = LogLevel.Debug) =>
        AppLog.WriteFor(_context, level, message);

    /// <summary>Normal end: duration, outcome and memory. Over the slow threshold it becomes a WARN.</summary>
    public void Complete(string? outcome = null)
    {
        if (_finished) return;
        _finished = true;

        long totalMs = ElapsedMs;
        long waitedMs = Interlocked.Read(ref _excludedMs);
        long measuredMs = Math.Max(0, totalMs - waitedMs);
        string waitNote = waitedMs > 0 ? $"，已剔除等待 {waitedMs} ms（实际 {totalMs} ms）" : string.Empty;
        string result = string.IsNullOrWhiteSpace(outcome) ? "完成" : outcome!;
        string memory = AppLog.MemorySnapshot();

        if (_slowMs > 0 && measuredMs >= _slowMs)
        {
            AppLog.WriteFor(
                _context, LogLevel.Warn,
                $"慢操作[{_category}]：{_name} {measuredMs} ms" +
                $"（慢操作阈值 {_slowMs} ms；{result}{waitNote}；内存 {memory}）");
            return;
        }

        AppLog.WriteFor(
            _context, LogLevel.Info,
            $"操作完成[{_category}]：{_name} {measuredMs} ms（{result}{waitNote}；内存 {memory}）");
    }

    /// <summary>Failed end: ERROR with the exception attached, duration and memory included.</summary>
    public void Fail(string message, Exception? exception = null)
    {
        if (_finished) return;
        _finished = true;

        AppLog.WriteFor(
            _context, LogLevel.Error,
            $"操作失败[{_category}]：{_name} {ElapsedMs} ms（{message}；内存 {AppLog.MemorySnapshot()}）",
            exception);
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _finished = true;
            AppLog.WriteFor(
                _context, LogLevel.Debug,
                $"操作未标记结束[{_category}]：{_name} {ElapsedMs} ms（内存 {AppLog.MemorySnapshot()}）");
        }

        AppLog.ExitOperation(_context, _previous);
    }
}
