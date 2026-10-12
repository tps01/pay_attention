using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public sealed class AppLogger
{
    private readonly string _logDirectory;
    private readonly object _lock = new();
    private LogLevel _minLevel = LogLevel.Info;

    public AppLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

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
