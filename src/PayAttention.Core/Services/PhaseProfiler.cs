using System.Diagnostics;
using System.Text;
using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

/// <summary>
/// Accumulates wall-clock samples by phase name and formats a ranked summary for debug logs.
/// Thread-safe; intended for capture, OCR, and save paths that share one instance.
/// </summary>
public sealed class PhaseProfiler
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PhaseStats> _phases = new(StringComparer.Ordinal);
    private long _windowStartTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Records elapsed time for <paramref name="phase"/> when <paramref name="logger"/>
    /// is at Debug; no-op at Info so release runs stay quiet by default.
    /// </summary>
    public void Record(string phase, long startTimestamp, AppLogger logger) =>
        RecordElapsed(phase, Stopwatch.GetTimestamp() - startTimestamp, logger);

    /// <summary>
    /// Records a known duration for <paramref name="phase"/> when Debug (used by tests).
    /// </summary>
    public void RecordElapsed(string phase, TimeSpan elapsed, AppLogger logger) =>
        RecordElapsed(phase, (long)(elapsed.TotalSeconds * Stopwatch.Frequency), logger);

    private void RecordElapsed(string phase, long elapsedTicks, AppLogger logger)
    {
        if (logger.MinLevel != LogLevel.Debug || elapsedTicks < 0)
            return;

        lock (_lock)
        {
            if (!_phases.TryGetValue(phase, out var stats))
            {
                stats = new PhaseStats();
                _phases[phase] = stats;
            }

            stats.TotalTicks += elapsedTicks;
            stats.Count++;
            if (elapsedTicks > stats.MaxTicks)
                stats.MaxTicks = elapsedTicks;
        }
    }

    /// <summary>
    /// Compact capture context for Profile lines, e.g.
    /// <c>mon=all scope=monitor interval=500ms pending=0</c>.
    /// </summary>
    public static string FormatCaptureContext(AppConfig config, int pendingBehind)
    {
        var mon = config.MonitorSelection switch
        {
            MonitorSelection.All => "all",
            MonitorSelection.Specific => $"specific{config.SpecificMonitorIndex}",
            _ => "primary"
        };
        var scope = config.CaptureScope == CaptureScope.ForegroundWindow ? "foreground" : "monitor";
        var pending = Math.Max(0, pendingBehind);
        return $"mon={mon} scope={scope} interval={config.OcrIntervalMs}ms pending={pending}";
    }

    /// <summary>
    /// Builds a ranked summary line and clears the window. Returns null when empty.
    /// </summary>
    public string? FormatAndReset(string? windowLabel = null, string? context = null)
    {
        Dictionary<string, PhaseStats> snapshot;
        double windowSeconds;
        lock (_lock)
        {
            if (_phases.Count == 0)
            {
                _windowStartTimestamp = Stopwatch.GetTimestamp();
                return null;
            }

            snapshot = new Dictionary<string, PhaseStats>(_phases, StringComparer.Ordinal);
            _phases.Clear();
            var now = Stopwatch.GetTimestamp();
            windowSeconds = TicksToSeconds(now - _windowStartTimestamp);
            _windowStartTimestamp = now;
        }

        var totalTicks = snapshot.Values.Sum(s => s.TotalTicks);
        if (totalTicks <= 0)
            return null;

        var label = windowLabel ?? FormatWindowLabel(windowSeconds);
        var ranked = snapshot
            .OrderByDescending(kv => kv.Value.TotalTicks)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.Append("Profile ").Append(label);
        if (!string.IsNullOrWhiteSpace(context))
            sb.Append(" [").Append(context).Append(']');
        sb.Append(':');
        var first = true;
        foreach (var (name, stats) in ranked)
        {
            if (!first)
                sb.Append(';');
            first = false;

            var totalMs = TicksToMilliseconds(stats.TotalTicks);
            var maxMs = TicksToMilliseconds(stats.MaxTicks);
            var pct = 100.0 * stats.TotalTicks / totalTicks;
            sb.Append(' ')
                .Append(name)
                .Append(' ')
                .Append(FormatMs(totalMs))
                .Append("ms (")
                .Append(FormatPercent(pct))
                .Append("%, n=")
                .Append(stats.Count)
                .Append(", max ")
                .Append(FormatMs(maxMs))
                .Append("ms)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Flushes a ranked line to <paramref name="logger"/> when Debug and samples exist.
    /// </summary>
    public void FlushTo(AppLogger logger, string? windowLabel = null, string? context = null)
    {
        if (logger.MinLevel != LogLevel.Debug)
        {
            // Drop samples so a later Debug session does not include Info-era noise.
            lock (_lock)
            {
                _phases.Clear();
                _windowStartTimestamp = Stopwatch.GetTimestamp();
            }
            return;
        }

        var line = FormatAndReset(windowLabel, context);
        if (line is not null)
            logger.Debug(line);
    }

    private static string FormatWindowLabel(double windowSeconds)
    {
        if (windowSeconds < 1)
            return $"{Math.Max(0, (int)Math.Round(windowSeconds * 1000))}ms";
        return $"{Math.Max(1, (int)Math.Round(windowSeconds))}s";
    }

    private static double TicksToSeconds(long ticks) =>
        ticks / (double)Stopwatch.Frequency;

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private static string FormatMs(double ms) =>
        ms < 10 ? ms.ToString("0.#") : ((long)Math.Round(ms)).ToString();

    private static string FormatPercent(double pct) =>
        pct < 10 ? pct.ToString("0.#") : ((long)Math.Round(pct)).ToString();

    private sealed class PhaseStats
    {
        public long TotalTicks;
        public long MaxTicks;
        public int Count;
    }
}
