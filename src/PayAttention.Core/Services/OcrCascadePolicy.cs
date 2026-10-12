using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

/// <summary>
/// OCR cascade stop / budget rules. Stops once any enabled trigger matches so large
/// trigger lists do not keep chasing absent phrases on the same frame.
/// </summary>
public static class OcrCascadePolicy
{
    public static IEnumerable<TriggerConfig> EnabledTriggers(AppConfig config) =>
        config.Triggers.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Text));

    /// <summary>
    /// True when at least one enabled trigger matches, or when there are no enabled triggers
    /// (nothing left to search for).
    /// </summary>
    public static bool AnyEnabledMatched(
        string text,
        AppConfig config,
        int? fastMatchThresholdPercent = null)
    {
        var enabled = EnabledTriggers(config);
        if (!enabled.Any())
            return true;

        var threshold = fastMatchThresholdPercent ?? config.FastMatchThresholdPercent;
        return TriggerMatcher.HasAnyMatch(text, config.Triggers, threshold);
    }

    /// <summary>
    /// Backlog budget: Fast only when every enabled trigger is Fast; otherwise Thorough.
    /// (Called only on total miss — any match already stops the cascade.)
    /// </summary>
    public static TimeSpan CascadeBudgetFor(
        AppConfig config,
        TimeSpan fastBudget,
        TimeSpan thoroughBudget)
    {
        var enabled = EnabledTriggers(config).ToList();
        if (enabled.Count == 0 || enabled.All(t => t.OcrMode == OcrSpeedMode.Fast))
            return fastBudget;
        return thoroughBudget;
    }

    public static bool ShouldStopCascade(
        string text,
        AppConfig config,
        bool applyBudget,
        TimeSpan elapsed,
        TimeSpan fastBudget,
        TimeSpan thoroughBudget,
        int? fastMatchThresholdPercent = null)
    {
        if (AnyEnabledMatched(text, config, fastMatchThresholdPercent))
            return true;
        if (!applyBudget)
            return false;
        return elapsed >= CascadeBudgetFor(config, fastBudget, thoroughBudget);
    }
}
