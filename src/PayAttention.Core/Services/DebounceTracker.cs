namespace PayAttention.Core.Services;

public sealed class DebounceTracker
{
    private readonly Dictionary<string, DateTimeOffset> _lastFired = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool ShouldFire(string key, TimeSpan debounce)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastFired.TryGetValue(key, out var last) && now - last < debounce)
                return false;

            _lastFired[key] = now;
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _lastFired.Clear();
        }
    }
}
