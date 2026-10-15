using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

public sealed class TriggerMatchResult
{
    public required TriggerConfig Trigger { get; init; }
    public required string MatchedPortion { get; init; }
}

public static class TriggerMatcher
{
    public const int DefaultFastMatchThresholdPercent = 90;

    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    private static readonly Regex WhitespaceCollapse = new(
        @"[\s_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SpaceCollapse = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NonAlnum = new(
        @"[^0-9a-z]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasAnyMatch(
        string ocrText,
        IEnumerable<TriggerConfig> triggers,
        int fastMatchThresholdPercent = DefaultFastMatchThresholdPercent) =>
        MatchCore(ocrText, triggers, fastMatchThresholdPercent, stopAfterFirst: true).Count > 0;

    public static IReadOnlyList<TriggerMatchResult> FindMatches(
        string ocrText,
        IEnumerable<TriggerConfig> triggers,
        int fastMatchThresholdPercent = DefaultFastMatchThresholdPercent) =>
        MatchCore(ocrText, triggers, fastMatchThresholdPercent, stopAfterFirst: false);

    private static List<TriggerMatchResult> MatchCore(
        string ocrText,
        IEnumerable<TriggerConfig> triggers,
        int fastMatchThresholdPercent,
        bool stopAfterFirst)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
            return [];

        var threshold = Math.Clamp(fastMatchThresholdPercent, 50, 100);
        var normalizedHaystack = Normalize(ocrText);
        var foldedHaystack = FoldConfusables(normalizedHaystack);
        var compactHaystack = CompactAlnum(foldedHaystack);
        var results = new List<TriggerMatchResult>();

        foreach (var trigger in triggers.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Text)))
        {
            switch (trigger.Match)
            {
                case MatchMode.Exact:
                {
                    var needle = Normalize(trigger.Text);
                    if (needle.Length == 0)
                        break;
                    var foldedNeedle = FoldConfusables(needle);
                    if (string.Equals(normalizedHaystack, needle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(foldedHaystack, foldedNeedle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(compactHaystack, CompactAlnum(foldedNeedle), StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new TriggerMatchResult { Trigger = trigger, MatchedPortion = trigger.Text });
                        if (stopAfterFirst)
                            return results;
                    }
                    break;
                }
                case MatchMode.Regex:
                {
                    var regex = GetCachedRegex(trigger.Text);
                    if (regex is null)
                        break;
                    Match match;
                    try
                    {
                        match = regex.Match(ocrText);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        break;
                    }

                    if (match.Success)
                    {
                        results.Add(new TriggerMatchResult { Trigger = trigger, MatchedPortion = match.Value });
                        if (stopAfterFirst)
                            return results;
                    }
                    break;
                }
                default:
                {
                    var needle = Normalize(trigger.Text);
                    if (needle.Length == 0)
                        break;
                    var foldedNeedle = FoldConfusables(needle);
                    var compactNeedle = CompactAlnum(foldedNeedle);
                    var hit =
                        normalizedHaystack.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        foldedHaystack.Contains(foldedNeedle, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(compactNeedle) &&
                         compactHaystack.Contains(compactNeedle, StringComparison.OrdinalIgnoreCase)) ||
                        FuzzyContains(compactHaystack, compactNeedle);

                    // Fast similarity is only useful when it allows more edits than FuzzyContains.
                    // At typical thresholds (e.g. 85%+), FuzzyContains already covers that band, so
                    // skip the expensive sliding-window rescan on long OCR haystacks.
                    if (!hit &&
                        trigger.OcrMode == OcrSpeedMode.Fast &&
                        NeedsBroaderFastSimilarity(compactNeedle, threshold) &&
                        MeetsFastSimilarityThreshold(compactHaystack, compactNeedle, threshold))
                    {
                        hit = true;
                    }

                    if (hit)
                    {
                        results.Add(new TriggerMatchResult { Trigger = trigger, MatchedPortion = trigger.Text });
                        if (stopAfterFirst)
                            return results;
                    }
                    break;
                }
            }
        }

        return results;
    }

    private static Regex? GetCachedRegex(string pattern) =>
        RegexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(
                    p,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    RegexMatchTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });

    /// <summary>
    /// Collapse whitespace/underscores and strip zero-width junk OCR sometimes inserts.
    /// </summary>
    public static string Normalize(string text)
    {
        var cleaned = text
            .Replace("\u200b", "")
            .Replace("\ufeff", "")
            .Replace("\u00a0", " ");
        return WhitespaceCollapse.Replace(cleaned, " ").Trim();
    }

    /// <summary>
    /// Map characters OCR commonly confuses. Punctuation is treated as a separator
    /// (dropped) so intentional symbols in triggers still match when OCR omits them.
    /// </summary>
    public static string FoldConfusables(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '0' or 'O' or 'o' or 'Ο' or 'ο' => '0',
                '1' or 'l' or 'I' or 'i' or '|' or '¡' => '1',
                '2' or 'Z' or 'z' => '2',
                '3' or '9' => '9', // 3/9 often confused at tiny sizes
                '5' or 'S' or 's' => '5',
                '8' or 'B' or 'b' => '8',
                '6' or 'G' or 'g' => '6',
                'A' or 'a' or 'Α' => 'a',
                'E' or 'e' or '€' => 'e',
                'C' or 'c' => 'c',
                'D' or 'd' => 'd',
                'F' or 'f' => 'f',
                'H' or 'h' => 'h',
                'K' or 'k' => 'k',
                'M' or 'm' => 'm',
                'N' or 'n' => 'n',
                'P' or 'p' => 'p',
                'R' or 'r' => 'r',
                'T' or 't' => 't',
                'U' or 'u' or 'V' or 'v' => 'u',
                'W' or 'w' => 'w',
                'X' or 'x' or '×' => 'x',
                'Y' or 'y' => 'y',
                'Q' or 'q' => 'q',
                _ when char.IsLetterOrDigit(ch) => char.ToLowerInvariant(ch),
                // Punctuation / symbols → separator (OCR often drops or mangling these)
                _ => ' '
            });
        }

        return SpaceCollapse.Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>Letters/digits only after confusable folding — punctuation-agnostic compare.</summary>
    public static string CompactAlnum(string foldedText) =>
        NonAlnum.Replace(foldedText, "");

    /// <summary>
    /// Fast-only: best sliding-window similarity vs threshold (percent). Needle length ≥ 6.
    /// At 100 this requires a perfect compact match (already covered by contains).
    /// </summary>
    public static bool MeetsFastSimilarityThreshold(string compactHaystack, string compactNeedle, int thresholdPercent)
    {
        if (string.IsNullOrEmpty(compactNeedle) || compactNeedle.Length < 6)
            return false;
        if (string.IsNullOrEmpty(compactHaystack))
            return false;

        var scorePercent = BestCompactSimilarityPercent(compactHaystack, compactNeedle);
        return scorePercent >= thresholdPercent;
    }

    /// <summary>
    /// True when the Fast threshold permits a larger edit distance than <see cref="FuzzyContains"/>.
    /// </summary>
    internal static bool NeedsBroaderFastSimilarity(string compactNeedle, int thresholdPercent)
    {
        if (string.IsNullOrEmpty(compactNeedle) || compactNeedle.Length < 6)
            return false;

        var threshold = Math.Clamp(thresholdPercent, 50, 100);
        // Edits allowed by threshold: score = 100*(1-dist/len) >= threshold
        // => dist <= len * (1 - threshold/100)
        var maxDistForThreshold = (int)Math.Floor(compactNeedle.Length * (1.0 - threshold / 100.0));
        var fuzzyMax = compactNeedle.Length >= 8 ? 2 : 1;
        return maxDistForThreshold > fuzzyMax;
    }

    /// <summary>
    /// Best window score: 100 * (1 - Levenshtein / max(window, needle length)).
    /// </summary>
    public static int BestCompactSimilarityPercent(string compactHaystack, string compactNeedle)
    {
        if (string.IsNullOrEmpty(compactNeedle) || string.IsNullOrEmpty(compactHaystack))
            return 0;

        if (compactHaystack.Contains(compactNeedle, StringComparison.OrdinalIgnoreCase))
            return 100;

        // Search windows near needle length; allow more slack than FuzzyContains when threshold is low.
        var maxDistance = Math.Max(1, (int)Math.Ceiling(compactNeedle.Length * 0.5));
        return BestWindowScorePercent(compactHaystack, compactNeedle, maxDistance);
    }

    /// <summary>
    /// Allow a small edit distance for short needles (OCR drop/insert of a few chars).
    /// </summary>
    private static bool FuzzyContains(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || needle.Length < 6)
            return false;

        if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;

        var maxDistance = needle.Length >= 8 ? 2 : 1;
        if (haystack.Length < needle.Length - maxDistance)
            return false;

        var minWindow = Math.Max(1, needle.Length - maxDistance);
        var maxWindow = needle.Length + maxDistance;
        var needleFirst = char.ToLowerInvariant(needle[0]);
        var needleLast = char.ToLowerInvariant(needle[^1]);

        for (var win = minWindow; win <= maxWindow; win++)
        {
            if (haystack.Length < win)
                break;

            for (var i = 0; i <= haystack.Length - win; i++)
            {
                var hFirst = char.ToLowerInvariant(haystack[i]);
                var hLast = char.ToLowerInvariant(haystack[i + win - 1]);
                var endPenalty = (hFirst == needleFirst ? 0 : 1) + (hLast == needleLast ? 0 : 1);
                if (endPenalty > maxDistance)
                    continue;

                if (LevenshteinBounded(haystack.AsSpan(i, win), needle.AsSpan(), maxDistance) <= maxDistance)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sliding-window similarity with banded Levenshtein. Skips hopeless windows via
    /// first/last char checks so many-trigger + long OCR haystacks stay interactive.
    /// </summary>
    private static int BestWindowScorePercent(string haystack, string needle, int maxDistance)
    {
        var minWindow = Math.Max(1, needle.Length - maxDistance);
        var maxWindow = needle.Length + maxDistance;
        var best = 0;
        var needleFirst = char.ToLowerInvariant(needle[0]);
        var needleLast = char.ToLowerInvariant(needle[^1]);

        for (var win = minWindow; win <= maxWindow; win++)
        {
            if (haystack.Length < win)
                break;

            var denom = Math.Max(needle.Length, win);
            // Any distance above this cannot beat the current best (or cannot meet a
            // useful score). Keep a floor so we still discover the first hit.
            var usefulDistCap = maxDistance;
            if (best > 0)
            {
                var distForBest = (int)Math.Floor(denom * (1.0 - best / 100.0));
                usefulDistCap = Math.Min(usefulDistCap, Math.Max(0, distForBest - 1));
            }

            for (var i = 0; i <= haystack.Length - win; i++)
            {
                // Cheap reject: ends must be within edit budget of the needle ends.
                var hFirst = char.ToLowerInvariant(haystack[i]);
                var hLast = char.ToLowerInvariant(haystack[i + win - 1]);
                var endPenalty = (hFirst == needleFirst ? 0 : 1) + (hLast == needleLast ? 0 : 1);
                if (endPenalty > usefulDistCap)
                    continue;

                var dist = LevenshteinBounded(haystack.AsSpan(i, win), needle.AsSpan(), usefulDistCap);
                if (dist > usefulDistCap)
                    continue;

                var score = (int)Math.Round(100.0 * (1.0 - (double)dist / denom));
                if (score > best)
                {
                    best = score;
                    if (best >= 100)
                        return 100;
                    usefulDistCap = Math.Min(
                        maxDistance,
                        Math.Max(0, (int)Math.Floor(denom * (1.0 - best / 100.0)) - 1));
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Banded Levenshtein; returns <paramref name="maxDistance"/> + 1 when the edit
    /// distance cannot be ≤ maxDistance (Ukkonen-style early abort).
    /// </summary>
    private static int LevenshteinBounded(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int maxDistance)
    {
        var n = a.Length;
        var m = b.Length;
        if (Math.Abs(n - m) > maxDistance)
            return maxDistance + 1;
        if (maxDistance < 0)
            return 0;

        // Two-row DP; compare case-insensitively (haystack/needle are usually folded already).
        Span<int> prev = stackalloc int[m + 1];
        Span<int> curr = stackalloc int[m + 1];
        for (var j = 0; j <= m; j++)
            prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            var ai = char.ToLowerInvariant(a[i - 1]);
            var jStart = Math.Max(1, i - maxDistance);
            var jEnd = Math.Min(m, i + maxDistance);

            for (var j = 1; j < jStart; j++)
                curr[j] = maxDistance + 1;

            for (var j = jStart; j <= jEnd; j++)
            {
                var cost = ai == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                var del = prev[j] + 1;
                var ins = curr[j - 1] + 1;
                var sub = prev[j - 1] + cost;
                var val = Math.Min(del, Math.Min(ins, sub));
                curr[j] = val;
                if (val < rowMin)
                    rowMin = val;
            }

            for (var j = jEnd + 1; j <= m; j++)
                curr[j] = maxDistance + 1;

            if (rowMin > maxDistance)
                return maxDistance + 1;

            var tmp = prev;
            prev = curr;
            curr = tmp;
        }

        return prev[m];
    }
}
