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
    public static TriggerMatchResult? FindMatch(string ocrText, IEnumerable<TriggerConfig> triggers) =>
        FindMatches(ocrText, triggers).FirstOrDefault();

    public static IReadOnlyList<TriggerMatchResult> FindMatches(string ocrText, IEnumerable<TriggerConfig> triggers)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
            return [];

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
                    var foldedNeedle = FoldConfusables(needle);
                    if (string.Equals(normalizedHaystack, needle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(foldedHaystack, foldedNeedle, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(compactHaystack, CompactAlnum(foldedNeedle), StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new TriggerMatchResult { Trigger = trigger, MatchedPortion = trigger.Text });
                    }
                    break;
                }
                case MatchMode.Regex:
                {
                    try
                    {
                        var match = Regex.Match(ocrText, trigger.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (match.Success)
                            results.Add(new TriggerMatchResult { Trigger = trigger, MatchedPortion = match.Value });
                    }
                    catch (ArgumentException)
                    {
                        // Invalid pattern — skip
                    }
                    break;
                }
                default:
                {
                    var needle = Normalize(trigger.Text);
                    var foldedNeedle = FoldConfusables(needle);
                    var compactNeedle = CompactAlnum(foldedNeedle);
                    if (normalizedHaystack.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                        foldedHaystack.Contains(foldedNeedle, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(compactNeedle) &&
                         compactHaystack.Contains(compactNeedle, StringComparison.OrdinalIgnoreCase)) ||
                        FuzzyContains(compactHaystack, compactNeedle))
                    {
                        results.Add(new TriggerMatchResult { Trigger = trigger, MatchedPortion = trigger.Text });
                    }
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Collapse whitespace/underscores and strip zero-width junk OCR sometimes inserts.
    /// </summary>
    public static string Normalize(string text)
    {
        var cleaned = text
            .Replace("\u200b", "")
            .Replace("\ufeff", "")
            .Replace("\u00a0", " ");
        return Regex.Replace(cleaned, @"[\s_]+", " ").Trim();
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
                '3' or '9' or 'g' => '9', // 3/9 often confused at tiny sizes
                '5' or 'S' or 's' => '5',
                '8' or 'B' => '8',
                '6' or 'G' => '6',
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
                'b' => 'b',
                _ when char.IsLetterOrDigit(ch) => char.ToLowerInvariant(ch),
                // Punctuation / symbols → separator (OCR often drops or mangling these)
                _ => ' '
            });
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Letters/digits only after confusable folding — punctuation-agnostic compare.</summary>
    public static string CompactAlnum(string foldedText) =>
        Regex.Replace(foldedText, @"[^0-9a-z]+", "", RegexOptions.IgnoreCase);

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
        for (var win = minWindow; win <= maxWindow; win++)
        {
            if (haystack.Length < win)
                break;

            for (var i = 0; i <= haystack.Length - win; i++)
            {
                var window = haystack.Substring(i, win);
                if (Levenshtein(window, needle) <= maxDistance)
                    return true;
            }
        }

        return false;
    }

    private static int Levenshtein(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}
