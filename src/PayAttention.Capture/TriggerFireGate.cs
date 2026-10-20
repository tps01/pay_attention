namespace PayAttention.Capture;

/// <summary>Why <see cref="TriggerFireGate.ShouldFire"/> rejected a fire.</summary>
public enum FireSuppressReason
{
    None = 0,
    Debounce = 1,
    UnchangedScene = 2,
    Pending = 3
}

/// <summary>
/// Decides whether a trigger should fire: time debounce plus "scene unchanged" suppression.
/// </summary>
public sealed class TriggerFireGate
{
    private readonly Dictionary<string, GateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly int _maxFingerprintDistance;

    public TriggerFireGate(int maxFingerprintDistance = 0)
    {
        _maxFingerprintDistance = maxFingerprintDistance;
    }

    /// <summary>
    /// Returns true when the trigger may fire now. Updates stored state on true.
    /// </summary>
    public bool ShouldFire(string key, TimeSpan debounce, ulong fingerprint) =>
        ShouldFire(key, debounce, fingerprint, out _);

    /// <inheritdoc cref="ShouldFire(string, TimeSpan, ulong)"/>
    public bool ShouldFire(
        string key,
        TimeSpan debounce,
        ulong fingerprint,
        out FireSuppressReason reason)
    {
        if (!TryClaim(key, debounce, fingerprint, out var claim, out reason))
            return false;
        Complete(key, claim);
        return true;
    }

    public bool TryClaim(string key, TimeSpan debounce, ulong fingerprint, out Guid claim, out FireSuppressReason reason)
    {
        lock (_lock)
        {
            claim = Guid.Empty;
            var now = DateTimeOffset.UtcNow;
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.Pending)
                {
                    reason = FireSuppressReason.Pending;
                    return false;
                }
                if (now - entry.LastFireUtc < debounce)
                {
                    reason = FireSuppressReason.Debounce;
                    return false;
                }

                if (FrameFingerprint.IsSimilar(entry.Fingerprint, fingerprint, _maxFingerprintDistance))
                {
                    reason = FireSuppressReason.UnchangedScene;
                    return false;
                }
            }

            claim = Guid.NewGuid();
            _entries[key] = new GateEntry(now, fingerprint, claim, true);
            reason = FireSuppressReason.None;
            return true;
        }
    }

    public void Complete(string key, Guid claim)
    {
        lock (_lock)
            if (_entries.TryGetValue(key, out var entry) && entry.Claim == claim)
                _entries[key] = entry with { LastFireUtc = DateTimeOffset.UtcNow, Pending = false };
    }

    public void Abandon(string key, Guid claim)
    {
        lock (_lock)
            if (_entries.TryGetValue(key, out var entry) && entry.Claim == claim && entry.Pending)
                _entries.Remove(key);
    }

    /// <summary>
    /// Time until <paramref name="key"/> can be claimed again, or null when it has never fired.
    /// A zero delay means the debounce window has already elapsed.
    /// </summary>
    public TimeSpan? TimeUntilRetry(string key, TimeSpan debounce)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return null;

            var remain = entry.LastFireUtc + debounce - DateTimeOffset.UtcNow;
            return remain < TimeSpan.Zero ? TimeSpan.Zero : remain;
        }
    }

    public void Reset()
    {
        lock (_lock)
            _entries.Clear();
    }

    private readonly record struct GateEntry(DateTimeOffset LastFireUtc, ulong Fingerprint, Guid Claim, bool Pending);
}
