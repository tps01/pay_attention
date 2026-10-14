using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

/// <summary>
/// When to keep searching a frame, and when a static scene should be sampled again.
/// A frame is searched until every enabled trigger has matched (or the passes/budget run out).
/// Stopping at the first hit drops other phrases that need a later OCR pass.
/// </summary>
public static class OcrCascadePolicy
{
    public static IEnumerable<TriggerConfig> EnabledTriggers(AppConfig config) =>
        config.Triggers.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Text));

    public static bool AllEnabledMatched(
        string text,
        AppConfig config,
        int? fastMatchThresholdPercent = null) =>
        AllEnabledMatched(CollectMatchedIds(text, config, fastMatchThresholdPercent), config);

    public static bool AllEnabledMatched(IReadOnlySet<Guid> matchedIds, AppConfig config)
    {
        var enabled = EnabledTriggers(config).ToList();
        if (enabled.Count == 0)
            return true;
        return enabled.All(t => matchedIds.Contains(t.Id));
    }

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
    /// IDs of enabled triggers that currently match <paramref name="text"/>.
    /// </summary>
    public static HashSet<Guid> CollectMatchedIds(
        string text,
        AppConfig config,
        int? fastMatchThresholdPercent = null)
    {
        var threshold = fastMatchThresholdPercent ?? config.FastMatchThresholdPercent;
        return TriggerMatcher.FindMatches(text, config.Triggers, threshold)
            .Select(m => m.Trigger.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Deep OCR (tiles/bands) is for Thorough misses. When every still-unmatched trigger
    /// is Fast (or nothing is left), first-tier (+ optional terminal) is enough.
    /// </summary>
    public static bool NeedsDeepCascade(IReadOnlySet<Guid> matchedIds, AppConfig config)
    {
        var unmatched = EnabledTriggers(config).Where(t => !matchedIds.Contains(t.Id)).ToList();
        if (unmatched.Count == 0)
            return false;
        return unmatched.Any(t => t.OcrMode == OcrSpeedMode.Thorough);
    }

    /// <summary>
    /// Backlog budget: Fast when every unmatched trigger is Fast (or none remain).
    /// </summary>
    public static bool UnmatchedAreAllFast(IReadOnlySet<Guid> matchedIds, AppConfig config)
    {
        var unmatched = EnabledTriggers(config).Where(t => !matchedIds.Contains(t.Id)).ToList();
        return unmatched.Count == 0 || unmatched.All(t => t.OcrMode == OcrSpeedMode.Fast);
    }

    /// <summary>
    /// Retry a static scene only when this search did not already answer it.
    /// A finished search that matched something stays quiet, even if other triggers
    /// are absent — otherwise one hit among dozens re-OCRs forever.
    /// A total miss, or a search cut short with nothing newer waiting, is tried again.
    /// </summary>
    public static bool ShouldResample(
        bool allEnabledMatched,
        bool anyEnabledMatched,
        bool cutShortForBacklog,
        int pendingBehind)
    {
        if (allEnabledMatched)
            return false;
        if (cutShortForBacklog && pendingBehind > 0)
            return false;
        if (!cutShortForBacklog && anyEnabledMatched)
            return false;
        return true;
    }
}
