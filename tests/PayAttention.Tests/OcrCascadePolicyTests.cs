using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public class OcrCascadePolicyTests
{
    private static AppConfig ConfigWithTriggers(params TriggerConfig[] triggers) =>
        new()
        {
            Triggers = triggers.ToList(),
            FastMatchThresholdPercent = 90
        };

    private static TriggerConfig Trigger(string text, OcrSpeedMode mode = OcrSpeedMode.Thorough) =>
        new()
        {
            Text = text,
            Match = MatchMode.Contains,
            OcrMode = mode,
            Enabled = true
        };

    [Fact]
    public void AnyEnabledMatched_true_when_one_of_many_hits()
    {
        var config = ConfigWithTriggers(
            Trigger("alpha"),
            Trigger("beta"),
            Trigger("gamma"));

        Assert.True(OcrCascadePolicy.AnyEnabledMatched("noise beta noise", config));
    }

    [Fact]
    public void AnyEnabledMatched_false_on_total_miss()
    {
        var config = ConfigWithTriggers(
            Trigger("alpha"),
            Trigger("beta"));

        Assert.False(OcrCascadePolicy.AnyEnabledMatched("nothing here", config));
    }

    [Fact]
    public void AnyEnabledMatched_true_when_no_enabled_triggers()
    {
        var config = ConfigWithTriggers(
            new TriggerConfig { Text = "x", Enabled = false });

        Assert.True(OcrCascadePolicy.AnyEnabledMatched("anything", config));
    }

    [Fact]
    public void ShouldStopCascade_stops_on_first_match_even_without_budget()
    {
        var config = ConfigWithTriggers(
            Trigger("hit-me"),
            Trigger("never-seen"));

        Assert.True(OcrCascadePolicy.ShouldStopCascade(
            "prefix hit-me suffix",
            config,
            applyBudget: false,
            elapsed: TimeSpan.Zero,
            fastBudget: TimeSpan.FromMilliseconds(400),
            thoroughBudget: TimeSpan.FromMilliseconds(8000)));
    }

    [Fact]
    public void ShouldStopCascade_miss_without_budget_keeps_going()
    {
        var config = ConfigWithTriggers(Trigger("absent"));

        Assert.False(OcrCascadePolicy.ShouldStopCascade(
            "other text",
            config,
            applyBudget: false,
            elapsed: TimeSpan.FromSeconds(60),
            fastBudget: TimeSpan.FromMilliseconds(400),
            thoroughBudget: TimeSpan.FromMilliseconds(8000)));
    }

    [Fact]
    public void CascadeBudgetFor_fast_when_all_enabled_are_fast()
    {
        var config = ConfigWithTriggers(
            Trigger("a", OcrSpeedMode.Fast),
            Trigger("b", OcrSpeedMode.Fast));
        var fast = TimeSpan.FromMilliseconds(400);
        var thorough = TimeSpan.FromMilliseconds(8000);

        Assert.Equal(fast, OcrCascadePolicy.CascadeBudgetFor(config, fast, thorough));
    }

    [Fact]
    public void CascadeBudgetFor_thorough_when_any_enabled_is_thorough()
    {
        var config = ConfigWithTriggers(
            Trigger("a", OcrSpeedMode.Fast),
            Trigger("b", OcrSpeedMode.Thorough));
        var fast = TimeSpan.FromMilliseconds(400);
        var thorough = TimeSpan.FromMilliseconds(8000);

        Assert.Equal(thorough, OcrCascadePolicy.CascadeBudgetFor(config, fast, thorough));
    }
}
