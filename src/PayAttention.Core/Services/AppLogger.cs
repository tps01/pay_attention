namespace PayAttention.Core.Services;

public sealed class AppLogger
{
    private readonly string _logDirectory;
    private readonly object _lock = new();

    public AppLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var detail = ex is null ? message : $"{message}: {ex}";
        Write("ERROR", detail);
    }

    private void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            var path = Path.Combine(_logDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (_lock)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Never throw from logger
        }
    }
}
