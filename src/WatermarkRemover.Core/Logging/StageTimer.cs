using System.Diagnostics;

namespace WatermarkRemover.Core.Logging;

/// <summary>
/// Per-stage execution timer for performance analysis: each <see cref="Stage"/> call writes one
/// line — 「环节耗时[scope]：环节名 X ms（累计 Y ms，detail）」 — to the injected trace callback.
/// The Core carries no logger dependency (same contract as feature traces): the app wires the
/// callback to its file log. A null callback disables the timer completely (zero overhead), and
/// the built-in timestamp source is replaceable so tests can drive the clock.
/// One instance belongs to one job on one thread; concurrent jobs each hold their own and the
/// app's logger serialises the writes.
/// </summary>
public sealed class StageTimer
{
    private readonly Action<string, Exception?>? _trace;
    private readonly Func<long> _now;
    private readonly long _start;
    private long _last;

    /// <param name="trace">Receives every emitted line; null disables the timer.</param>
    /// <param name="scope">Line prefix identifying the run (file name, pipeline call…).</param>
    /// <param name="timestamp">Tick source; defaults to <see cref="Stopwatch.GetTimestamp"/>.</param>
    public StageTimer(Action<string, Exception?>? trace, string scope, Func<long>? timestamp = null)
    {
        _trace = trace;
        Scope = string.IsNullOrWhiteSpace(scope) ? "执行" : scope;
        _now = timestamp ?? Stopwatch.GetTimestamp;
        _start = _now();
        _last = _start;
    }

    /// <summary>Prefix of every line, e.g. the input file name or 管线调用1.</summary>
    public string Scope { get; }

    /// <summary>False when no callback was supplied — callers may skip building detail strings.</summary>
    public bool Enabled => _trace is not null;

    /// <summary>Total elapsed milliseconds since the timer started.</summary>
    public long ElapsedMs => ToMs(_now() - _start);

    /// <summary>
    /// Emits the stage that ran since the previous mark — 「环节耗时[scope]：name X ms（累计 Y
    /// ms，detail）」 — and moves the mark forward, so consecutive calls chain into a linear
    /// per-stage timeline. Call it right AFTER the work it measures.
    /// </summary>
    public void Stage(string name, string? detail = null)
    {
        if (_trace is null) return;

        long now = _now();
        long stageMs = ToMs(now - _last);
        long totalMs = ToMs(now - _start);
        _last = now;

        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"，{detail}";
        _trace($"环节耗时[{Scope}]：{name} {stageMs} ms（累计 {totalMs} ms{suffix}）", null);
    }

    /// <summary>Emits a free-form line under the same scope prefix (totals, aborts, fall backs).</summary>
    public void Emit(string message)
    {
        if (_trace is null) return;
        _trace($"环节耗时[{Scope}]：{message}", null);
    }

    private static long ToMs(long ticks) => ticks * 1000 / Stopwatch.Frequency;
}
