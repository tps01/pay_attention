using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public sealed class AppLoggerTests
{
    [Fact]
    public void Info_writes_when_min_level_is_info_but_debug_does_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(dir) { MinLevel = LogLevel.Info };
            logger.Debug("debug-hidden");
            logger.Info("info-visible");

            var files = Directory.GetFiles(dir, "app-*.log");
            Assert.Single(files);
            var text = File.ReadAllText(files[0]);
            Assert.Contains("info-visible", text);
            Assert.DoesNotContain("debug-hidden", text);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Debug_writes_when_min_level_is_debug()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            var logger = new AppLogger(dir) { MinLevel = LogLevel.Debug };
            logger.Debug("debug-visible");
            logger.Info("info-visible");

            var files = Directory.GetFiles(dir, "app-*.log");
            Assert.Single(files);
            var text = File.ReadAllText(files[0]);
            Assert.Contains("debug-visible", text);
            Assert.Contains("info-visible", text);
            Assert.Contains("[DEBUG]", text);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnforceRetention_deletes_logs_older_than_five_days()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var today = DateTime.Today;
            var oldName = $"app-{today.AddDays(-6):yyyyMMdd}.log";
            var midName = $"app-{today.AddDays(-5):yyyyMMdd}.log";
            var recentName = $"app-{today.AddDays(-1):yyyyMMdd}.log";
            var todayName = $"app-{today:yyyyMMdd}.log";

            File.WriteAllText(Path.Combine(dir, oldName), "old\n");
            File.WriteAllText(Path.Combine(dir, midName), "mid\n");
            File.WriteAllText(Path.Combine(dir, recentName), "recent\n");
            File.WriteAllText(Path.Combine(dir, todayName), "today\n");

            var logger = new AppLogger(dir) { MinLevel = LogLevel.Info };
            var deleted = logger.EnforceRetention(5);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(Path.Combine(dir, oldName)));
            Assert.True(File.Exists(Path.Combine(dir, midName)));
            Assert.True(File.Exists(Path.Combine(dir, recentName)));
            Assert.True(File.Exists(Path.Combine(dir, todayName)));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnforceRetention_zero_days_deletes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var oldName = $"app-{DateTime.Today.AddDays(-30):yyyyMMdd}.log";
            File.WriteAllText(Path.Combine(dir, oldName), "old\n");

            var logger = new AppLogger(dir);
            Assert.Equal(0, logger.EnforceRetention(0));
            Assert.True(File.Exists(Path.Combine(dir, oldName)));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
