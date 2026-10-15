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
        var beta = Trigger("beta");
        var config = ConfigWithTriggers(
            Trigger("alpha"),
            beta,
            Trigger("gamma"));
        var matched = new HashSet<Guid> { beta.Id };

        Assert.True(OcrCascadePolicy.AnyAllowedMatched(matched, config, foregroundProcess: null));
        Assert.False(OcrCascadePolicy.AllEnabledMatched(matched, config));
    }

    [Fact]
    public void AllEnabledMatched_true_when_every_enabled_trigger_hits()
    {
        var a = Trigger("alpha");
        var b = Trigger("beta");
        var config = ConfigWithTriggers(a, b);

        Assert.True(OcrCascadePolicy.AllEnabledMatched(new HashSet<Guid> { a.Id, b.Id }, config));
    }

    [Fact]
    public void AllowedMatched_false_on_total_miss()
    {
        var config = ConfigWithTriggers(
            Trigger("alpha"),
            Trigger("beta"));
        var matched = new HashSet<Guid>();

        Assert.False(OcrCascadePolicy.AnyAllowedMatched(matched, config, foregroundProcess: null));
        Assert.False(OcrCascadePolicy.AllAllowedMatched(matched, config, foregroundProcess: null));
        Assert.False(OcrCascadePolicy.AllEnabledMatched(matched, config));
    }

    [Fact]
    public void AllowedMatched_true_when_no_enabled_triggers()
    {
        var config = ConfigWithTriggers(
            new TriggerConfig { Text = "x", Enabled = false });
        var matched = new HashSet<Guid>();

        Assert.True(OcrCascadePolicy.AnyAllowedMatched(matched, config, foregroundProcess: null));
        Assert.True(OcrCascadePolicy.AllAllowedMatched(matched, config, foregroundProcess: null));
        Assert.True(OcrCascadePolicy.AllEnabledMatched(matched, config));
    }

    [Fact]
    public void AllAllowedMatched_ignores_triggers_not_allowed_for_foreground()
    {
        var alpha = Trigger("alpha");
        var beta = Trigger("beta");
        var config = ConfigWithTriggers(alpha, beta);
        config.ApplicationScope = ApplicationScopeMode.Whitelisted;
        config.Applications =
        [
            new ApplicationConfig
            {
                ProcessName = "notepad",
                Enabled = true,
                ListMode = ApplicationListMode.Whitelist,
                TriggerFilter = ApplicationTriggerFilter.Selected,
                TriggerIds = [alpha.Id]
            }
        ];

        // Only alpha is allowed for notepad; beta matched-or-not must not block "all allowed".
        Assert.True(OcrCascadePolicy.AllAllowedMatched(
            new HashSet<Guid> { alpha.Id }, config, "notepad"));
        Assert.False(OcrCascadePolicy.AllAllowedMatched(
            new HashSet<Guid>(), config, "notepad"));
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
    public void ShouldResample_does_not_spin_when_cut_short_and_the_queue_is_idle()
    {
        // Caller schedules a delayed resample instead of AllowResample forever.
        Assert.False(OcrCascadePolicy.ShouldResample(
            allEnabledMatched: false,
            anyEnabledMatched: false,
            cutShortForBacklog: true,
            pendingBehind: 0));
        Assert.False(OcrCascadePolicy.ShouldResample(
            allEnabledMatched: false,
            anyEnabledMatched: true,
            cutShortForBacklog: true,
            pendingBehind: 0));
    }

    [Fact]
    public void NeedsDeepCascade_false_when_only_fast_triggers_remain()
    {
        var fast = Trigger("alpha", OcrSpeedMode.Fast);
        var thorough = Trigger("beta", OcrSpeedMode.Thorough);
        var config = ConfigWithTriggers(fast, thorough);
        var matched = new HashSet<Guid> { thorough.Id };

        Assert.False(OcrCascadePolicy.NeedsDeepCascade(matched, config));
        Assert.True(OcrCascadePolicy.UnmatchedAreAllFast(matched, config));
    }

    [Fact]
    public void NeedsDeepCascade_ignores_thorough_trigger_outside_foreground_scope()
    {
        var allowedFast = Trigger("alpha", OcrSpeedMode.Fast);
        var blockedThorough = Trigger("beta", OcrSpeedMode.Thorough);
        var config = ConfigWithTriggers(allowedFast, blockedThorough);
        config.ApplicationScope = ApplicationScopeMode.Whitelisted;
        config.Applications =
        [
            new ApplicationConfig
            {
                ProcessName = "notepad",
                Enabled = true,
                ListMode = ApplicationListMode.Whitelist,
                TriggerFilter = ApplicationTriggerFilter.Selected,
                TriggerIds = [allowedFast.Id]
            }
        ];

        Assert.False(OcrCascadePolicy.NeedsDeepCascade(new HashSet<Guid>(), config, "notepad"));
        Assert.False(OcrCascadePolicy.AllAllowedMatched(new HashSet<Guid>(), config, "notepad"));
        Assert.True(OcrCascadePolicy.AllAllowedMatched(
            new HashSet<Guid> { allowedFast.Id }, config, "notepad"));
    }

    [Fact]
    public void NeedsDeepCascade_true_when_a_thorough_trigger_is_still_missing()
    {
        var fast = Trigger("alpha", OcrSpeedMode.Fast);
        var thorough = Trigger("beta", OcrSpeedMode.Thorough);
        var config = ConfigWithTriggers(fast, thorough);
        var matched = new HashSet<Guid> { fast.Id };

        Assert.True(OcrCascadePolicy.NeedsDeepCascade(matched, config));
        Assert.False(OcrCascadePolicy.UnmatchedAreAllFast(matched, config));
    }

    [Fact]
    public void AllEnabledMatched_from_cached_ids()
    {
        var a = Trigger("alpha");
        var b = Trigger("beta");
        var config = ConfigWithTriggers(a, b);

        Assert.False(OcrCascadePolicy.AllEnabledMatched(new HashSet<Guid> { a.Id }, config));
        Assert.True(OcrCascadePolicy.AllEnabledMatched(new HashSet<Guid> { a.Id, b.Id }, config));
    }
}
