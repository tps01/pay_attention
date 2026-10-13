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
        int? fastMatchThresholdPercent = null)
    {
        var enabled = EnabledTriggers(config).ToList();
        if (enabled.Count == 0)
            return true;

        var threshold = fastMatchThresholdPercent ?? config.FastMatchThresholdPercent;
        var matchedIds = TriggerMatcher.FindMatches(text, config.Triggers, threshold)
            .Select(m => m.Trigger.Id)
            .ToHashSet();
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
