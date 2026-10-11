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
}
