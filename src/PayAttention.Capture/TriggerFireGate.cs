namespace PayAttention.Capture;

/// <summary>
/// Decides whether a trigger should fire: time debounce plus "scene unchanged" suppression.
/// </summary>
public sealed class TriggerFireGate
{
    private readonly Dictionary<string, GateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly int _maxFingerprintDistance;

    public TriggerFireGate(int maxFingerprintDistance = FrameFingerprint.DefaultMaxDistance)
    {
        _maxFingerprintDistance = maxFingerprintDistance;
    }

    /// <summary>
    /// Returns true when the trigger may fire now. Updates stored state on true.
    /// </summary>
    public bool ShouldFire(string key, TimeSpan debounce, ulong fingerprint)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_entries.TryGetValue(key, out var entry))
            {
                if (now - entry.LastFireUtc < debounce)
                    return false;

                if (FrameFingerprint.IsSimilar(entry.Fingerprint, fingerprint, _maxFingerprintDistance))
                    return false;
            }

            _entries[key] = new GateEntry(now, fingerprint);
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
            _entries.Clear();
    }

    private readonly record struct GateEntry(DateTimeOffset LastFireUtc, ulong Fingerprint);
}
