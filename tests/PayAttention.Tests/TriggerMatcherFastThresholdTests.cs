using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public class TriggerMatcherFastThresholdTests
{
    // Compact needle length 10; OCR near-miss needs edit distance 3 (beyond FuzzyContains max of 2).
    private const string TriggerText = "abcdefghij";
    private const string NearMissOcr = "prefix xxxdefghij suffix";

    [Fact]
    public void Fast_high_threshold_rejects_garbled_near_miss()
    {
        var triggers = new[]
        {
            new TriggerConfig
            {
                Text = TriggerText,
                Match = MatchMode.Contains,
                OcrMode = OcrSpeedMode.Fast,
                Enabled = true
            }
        };

        var matches = TriggerMatcher.FindMatches(NearMissOcr, triggers, fastMatchThresholdPercent: 90);
        Assert.Empty(matches);
    }

    [Fact]
    public void Fast_low_threshold_accepts_same_near_miss()
    {
        var triggers = new[]
        {
            new TriggerConfig
            {
                Text = TriggerText,
                Match = MatchMode.Contains,
                OcrMode = OcrSpeedMode.Fast,
                Enabled = true
            }
        };

        var matches = TriggerMatcher.FindMatches(NearMissOcr, triggers, fastMatchThresholdPercent: 70);
        Assert.Single(matches);
        Assert.Equal(TriggerText, matches[0].MatchedPortion);
    }

    [Fact]
    public void Thorough_ignores_low_threshold()
    {
        var triggers = new[]
        {
            new TriggerConfig
            {
                Text = TriggerText,
                Match = MatchMode.Contains,
                OcrMode = OcrSpeedMode.Thorough,
                Enabled = true
            }
        };

        var matches = TriggerMatcher.FindMatches(NearMissOcr, triggers, fastMatchThresholdPercent: 50);
        Assert.Empty(matches);
    }

    [Fact]
    public void BestCompactSimilarityPercent_reports_expected_band()
    {
        var needle = TriggerMatcher.CompactAlnum(TriggerMatcher.FoldConfusables(TriggerText));
        var hay = TriggerMatcher.CompactAlnum(TriggerMatcher.FoldConfusables(NearMissOcr));
        var score = TriggerMatcher.BestCompactSimilarityPercent(hay, needle);
        Assert.InRange(score, 65, 75);
    }

    [Fact]
    public void HasAnyMatch_true_without_scanning_remaining_triggers()
    {
        var triggers = new[]
        {
            new TriggerConfig { Text = "first", Match = MatchMode.Contains, Enabled = true },
            new TriggerConfig { Text = "second", Match = MatchMode.Contains, Enabled = true }
        };

        Assert.True(TriggerMatcher.HasAnyMatch("xx first yy", triggers));
        Assert.False(TriggerMatcher.HasAnyMatch("nothing", triggers));
    }

    [Fact]
    public void FindMatches_returns_first_list_order_hit()
    {
        var triggers = new[]
        {
            new TriggerConfig { Text = "beta", Match = MatchMode.Contains, Enabled = true },
            new TriggerConfig { Text = "alpha", Match = MatchMode.Contains, Enabled = true }
        };

        var matches = TriggerMatcher.FindMatches("alpha and beta", triggers);
        Assert.Equal(2, matches.Count);
        Assert.Equal("beta", matches[0].Trigger.Text);
    }

    [Fact]
    public void FindMatches_regex_invalid_pattern_is_skipped()
    {
        var triggers = new[]
        {
            new TriggerConfig { Text = "[", Match = MatchMode.Regex, Enabled = true },
            new TriggerConfig { Text = "ok", Match = MatchMode.Contains, Enabled = true }
        };

        var matches = TriggerMatcher.FindMatches("ok token", triggers);
        Assert.Single(matches);
        Assert.Equal("ok", matches[0].Trigger.Text);
    }

    [Fact]
    public void Contains_underscore_only_needle_does_not_match_everything()
    {
        var triggers = new[]
        {
            new TriggerConfig { Text = "___", Match = MatchMode.Contains, Enabled = true }
        };

        Assert.Empty(TriggerMatcher.FindMatches("hello world", triggers));
    }

    [Fact]
    public void FoldConfusables_treats_b_and_g_the_same_in_either_case()
    {
        Assert.Equal(
            TriggerMatcher.FoldConfusables("BigData"),
            TriggerMatcher.FoldConfusables("bigdata"));

        var triggers = new[]
        {
            new TriggerConfig { Text = "BigData", Match = MatchMode.Contains, Enabled = true }
        };
        var matches = TriggerMatcher.FindMatches("see big data here", triggers);
        Assert.Single(matches);
    }

    [Fact]
    public void Regex_catastrophic_pattern_does_not_hang()
    {
        var triggers = new[]
        {
            new TriggerConfig { Text = "(a+)+$", Match = MatchMode.Regex, Enabled = true }
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var matches = TriggerMatcher.FindMatches(new string('a', 28) + "b", triggers);
        Assert.Empty(matches);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }
}
