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
    public void AllEnabledMatched_false_when_only_one_of_many_hits()
    {
        var config = ConfigWithTriggers(
            Trigger("alpha"),
            Trigger("beta"),
            Trigger("gamma"));

        Assert.True(OcrCascadePolicy.AnyEnabledMatched("noise beta noise", config));
        Assert.False(OcrCascadePolicy.AllEnabledMatched("noise beta noise", config));
    }

    [Fact]
    public void AllEnabledMatched_true_when_every_enabled_trigger_hits()
    {
        var config = ConfigWithTriggers(
            Trigger("alpha"),
            Trigger("beta"));

        Assert.True(OcrCascadePolicy.AllEnabledMatched("alpha and beta", config));
    }

    [Fact]
    public void AnyEnabledMatched_false_on_total_miss()
    {
        var config = ConfigWithTriggers(
            Trigger("alpha"),
            Trigger("beta"));

        Assert.False(OcrCascadePolicy.AnyEnabledMatched("nothing here", config));
        Assert.False(OcrCascadePolicy.AllEnabledMatched("nothing here", config));
    }

    [Fact]
    public void AnyEnabledMatched_true_when_no_enabled_triggers()
    {
        var config = ConfigWithTriggers(
            new TriggerConfig { Text = "x", Enabled = false });

        Assert.True(OcrCascadePolicy.AnyEnabledMatched("anything", config));
        Assert.True(OcrCascadePolicy.AllEnabledMatched("anything", config));
    }

    [Fact]
    public void ShouldResample_stays_quiet_after_a_finished_partial_hit()
    {
        Assert.False(OcrCascadePolicy.ShouldResample(
            allEnabledMatched: false,
            anyEnabledMatched: true,
            cutShortForBacklog: false,
            pendingBehind: 0));
    }

    [Fact]
    public void ShouldResample_retries_a_finished_total_miss()
    {
        Assert.True(OcrCascadePolicy.ShouldResample(
            allEnabledMatched: false,
            anyEnabledMatched: false,
            cutShortForBacklog: false,
            pendingBehind: 0));
    }

    [Fact]
    public void ShouldResample_false_when_every_trigger_matched()
    {
        Assert.False(OcrCascadePolicy.ShouldResample(
            allEnabledMatched: true,
            anyEnabledMatched: true,
            cutShortForBacklog: false,
            pendingBehind: 0));
    }

    [Fact]
    public void ShouldResample_drains_backlog_before_retrying_a_cut_short_search()
    {
        Assert.False(OcrCascadePolicy.ShouldResample(
            allEnabledMatched: false,
            anyEnabledMatched: true,
            cutShortForBacklog: true,
            pendingBehind: 2));
    }

    [Fact]
    public void ShouldResample_retries_when_cut_short_and_the_queue_is_idle()
    {
        Assert.True(OcrCascadePolicy.ShouldResample(
            allEnabledMatched: false,
            anyEnabledMatched: false,
            cutShortForBacklog: true,
            pendingBehind: 0));
    }
}
