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
}
