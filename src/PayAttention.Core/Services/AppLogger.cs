using System.Globalization;
using System.Text.RegularExpressions;
using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public sealed class AppLogger
{
    /// <summary>App log files older than this many calendar days are deleted.</summary>
    public const int DefaultLogRetentionDays = 5;

    private static readonly Regex LogFileNamePattern = new(
        @"^app-(\d{8})\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly string _logDirectory;
    private readonly object _lock = new();
    private LogLevel _minLevel = LogLevel.Info;

    public AppLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public LogLevel MinLevel
    {
        get
        {
            lock (_lock)
                return _minLevel;
        }
        set
        {
            lock (_lock)
                _minLevel = value;
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, "DEBUG", message);
    public void Info(string message) => Write(LogLevel.Info, "INFO", message);
    public void Warn(string message) => Write(LogLevel.Info, "WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var detail = ex is null ? message : $"{message}: {ex}";
        Write(LogLevel.Info, "ERROR", detail);
    }

    /// <summary>
    /// Deletes <c>app-yyyyMMdd.log</c> files older than <paramref name="maxAgeDays"/>.
    /// Date is taken from the filename when possible.
    /// </summary>
    public int EnforceRetention(int maxAgeDays = DefaultLogRetentionDays)
    {
        if (maxAgeDays <= 0 || !Directory.Exists(_logDirectory))
            return 0;

        var cutoffDate = DateTime.Today.AddDays(-maxAgeDays);
        var deleted = 0;

        lock (_lock)
        {
            foreach (var path in Directory.EnumerateFiles(_logDirectory, "app-*.log"))
            {
                try
                {
                    var name = Path.GetFileName(path);
                    DateTime fileDate;
                    var match = LogFileNamePattern.Match(name);
                    if (match.Success &&
                        DateTime.TryParseExact(
                            match.Groups[1].Value,
                            "yyyyMMdd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsed))
                    {
                        fileDate = parsed.Date;
                    }
                    else
                    {
                        fileDate = File.GetLastWriteTimeUtc(path).Date;
                    }

                    if (fileDate >= cutoffDate)
                        continue;

                    File.Delete(path);
                    deleted++;
                }
                catch
                {
                    // Never throw from logger retention
                }
            }
        }

        if (deleted > 0)
            Info($"Log retention removed {deleted} file(s) older than {maxAgeDays} day(s).");

        return deleted;
    }

    private void Write(LogLevel messageLevel, string levelLabel, string message)
    {
        try
        {
            lock (_lock)
            {
                // Debug messages only when MinLevel is Debug; Info/Warn/Error always write.
                if (messageLevel == LogLevel.Debug && _minLevel != LogLevel.Debug)
                    return;

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{levelLabel}] {message}{Environment.NewLine}";
                var path = Path.Combine(_logDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Never throw from logger
        }
    }
}
