using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

/// <summary>
/// Picks at most one capture event per OCR frame: the first list-order match that may fire,
/// plus same-phrase siblings with distinct actions (e.g. Screenshot + Record).
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

        TriggerMatchResult? winner = null;
        foreach (var match in allowedMatchesInOrder)
        {
            if (!tryClaimFire(match))
                continue;
            winner = match;
            break;
        }

        if (winner is null)
            return [];

        var selected = new List<TriggerMatchResult> { winner };
        var actions = new HashSet<CaptureAction> { winner.Trigger.Action };

        foreach (var match in allowedMatchesInOrder)
        {
            if (match.Trigger.Id == winner.Trigger.Id)
                continue;
            if (!string.Equals(match.Trigger.Text, winner.Trigger.Text, StringComparison.Ordinal))
                continue;
            if (actions.Contains(match.Trigger.Action))
                continue;
            if (!tryClaimFire(match))
                continue;

            selected.Add(match);
            actions.Add(match.Trigger.Action);
        }

        return selected;
    }
}
