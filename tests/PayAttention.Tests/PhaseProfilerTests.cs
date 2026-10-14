using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public sealed class PhaseProfilerTests
{
    [Fact]
    public void FormatAndReset_returns_null_when_empty()
    {
        var profiler = new PhaseProfiler();
        Assert.Null(profiler.FormatAndReset());
    }

    [Fact]
    public void Record_ignored_when_log_level_is_info()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_prof_" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(dir) { MinLevel = LogLevel.Info };
            var profiler = new PhaseProfiler();
            profiler.RecordElapsed("grab", TimeSpan.FromMilliseconds(50), logger);
            Assert.Null(profiler.FormatAndReset());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FormatAndReset_ranks_by_total_time_with_percent_count_and_max()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_prof_" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(dir) { MinLevel = LogLevel.Debug };
            var profiler = new PhaseProfiler();

            // grab: 100ms + 50ms = 150ms total, max 100, n=2
            profiler.RecordElapsed("grab", TimeSpan.FromMilliseconds(100), logger);
            profiler.RecordElapsed("grab", TimeSpan.FromMilliseconds(50), logger);
            // ocr.tiles: 300ms once — should rank first
            profiler.RecordElapsed("ocr.tiles", TimeSpan.FromMilliseconds(300), logger);
            // match: 50ms once
            profiler.RecordElapsed("match", TimeSpan.FromMilliseconds(50), logger);

            var line = profiler.FormatAndReset("30s");
            Assert.NotNull(line);
            Assert.Equal(
                "Profile 30s: ocr.tiles 300ms (60%, n=1, max 300ms); grab 150ms (30%, n=2, max 100ms); match 50ms (10%, n=1, max 50ms)",
                line);

            // Window cleared
            Assert.Null(profiler.FormatAndReset());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FlushTo_writes_debug_line_when_samples_exist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_prof_" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(dir) { MinLevel = LogLevel.Debug };
            var profiler = new PhaseProfiler();
            profiler.RecordElapsed("write", TimeSpan.FromMilliseconds(25), logger);
            profiler.FlushTo(logger, "stop");

            var files = Directory.GetFiles(dir, "app-*.log");
            Assert.Single(files);
            var text = File.ReadAllText(files[0]);
            Assert.Contains("[DEBUG]", text);
            Assert.Contains("Profile stop:", text);
            Assert.Contains("write", text);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FormatAndReset_includes_capture_context_bracket()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_prof_" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(dir) { MinLevel = LogLevel.Debug };
            var profiler = new PhaseProfiler();
            profiler.RecordElapsed("enqueue.hash", TimeSpan.FromMilliseconds(40), logger);
            profiler.RecordElapsed("enqueue.clone", TimeSpan.FromMilliseconds(10), logger);

            var context = PhaseProfiler.FormatCaptureContext(
                new AppConfig
                {
                    MonitorSelection = MonitorSelection.All,
                    CaptureScope = CaptureScope.Monitor,
                    OcrIntervalMs = 500
                },
                pendingBehind: 2);

            Assert.Equal("mon=all scope=monitor interval=500ms pending=2", context);

            var line = profiler.FormatAndReset("30s", context);
            Assert.Equal(
                "Profile 30s [mon=all scope=monitor interval=500ms pending=2]: enqueue.hash 40ms (80%, n=1, max 40ms); enqueue.clone 10ms (20%, n=1, max 10ms)",
                line);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FormatCaptureContext_formats_specific_monitor_and_foreground()
    {
        var context = PhaseProfiler.FormatCaptureContext(
            new AppConfig
            {
                MonitorSelection = MonitorSelection.Specific,
                SpecificMonitorIndex = 1,
                CaptureScope = CaptureScope.ForegroundWindow,
                OcrIntervalMs = 250
            },
            pendingBehind: 0);

        Assert.Equal("mon=specific1 scope=foreground interval=250ms pending=0", context);
    }
}
