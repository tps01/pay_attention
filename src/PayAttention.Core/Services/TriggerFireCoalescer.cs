using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

/// <summary>
/// Selects which matches may fire for one OCR result.
/// Every distinct phrase fires. The same phrase with a different action (Screenshot + Record)
/// still fires once per action. A repeated phrase with the same action does not double-fire.
/// </summary>
public static class TriggerFireCoalescer
{
    /// <param name="allowedMatchesInOrder">Matches already filtered by app scope, in trigger list order.</param>
    /// <param name="tryClaimFire">Returns true when this match may fire; may update debounce state.</param>
    public static IReadOnlyList<TriggerMatchResult> SelectFires(
        IReadOnlyList<TriggerMatchResult> allowedMatchesInOrder,
        Func<TriggerMatchResult, bool> tryClaimFire)
    {
        if (allowedMatchesInOrder.Count == 0)
            return [];

        var selected = new List<TriggerMatchResult>();
        var seen = new HashSet<(string Text, CaptureAction Action)>();

        foreach (var match in allowedMatchesInOrder)
        {
            if (!seen.Add((match.Trigger.Text, match.Trigger.Action)))
                continue;
            if (!tryClaimFire(match))
                continue;

            selected.Add(match);
        }

        return selected;
    }
}
