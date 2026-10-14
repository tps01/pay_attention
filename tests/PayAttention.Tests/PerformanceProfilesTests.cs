using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public class PerformanceProfilesTests
{
    private static PerformanceProfiles.ProfileTrialResult Trial(
        PerformanceProfileKind kind,
        double medianMs,
        bool plain,
        bool neon) =>
        new(kind, medianMs, plain, neon);

    private static List<PerformanceProfiles.ProfileTrialResult> Ladder(
        (PerformanceProfileKind Kind, double Ms, bool Plain, bool Neon)[] rows) =>
        rows.Select(r => Trial(r.Kind, r.Ms, r.Plain, r.Neon)).ToList();

    [Fact]
    public void DisplayName_VeryLow_is_spaced()
    {
        Assert.Equal("Very Low", PerformanceProfiles.DisplayName(PerformanceProfileKind.VeryLow));
        Assert.Equal("Ultra", PerformanceProfiles.DisplayName(PerformanceProfileKind.Ultra));
    }

    [Fact]
    public void Select_uses_headroom_on_fast_hardware_with_neon()
    {
        // Neon verified on every rung; all finish under budget → richest (Ultra).
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 200, true, true),
            (PerformanceProfileKind.Low, 300, true, true),
            (PerformanceProfileKind.Balanced, 400, true, true),
            (PerformanceProfileKind.High, 500, true, true),
            (PerformanceProfileKind.Ultra, 400, true, true)
        ]);

        var chosen = PerformanceProfiles.Select(trials, out var reason);
        Assert.Equal(PerformanceProfileKind.Ultra, chosen);
        Assert.Contains("Ultra", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_caps_at_Balanced_when_neon_never_reads()
    {
        // Plain-only on a fast PC: climb with headroom but do not enable High/Ultra neon blindly.
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 50, true, false),
            (PerformanceProfileKind.Low, 80, true, false),
            (PerformanceProfileKind.Balanced, 120, true, false),
            (PerformanceProfileKind.High, 150, true, false),
            (PerformanceProfileKind.Ultra, 200, true, false)
        ]);

        Assert.Equal(PerformanceProfileKind.Balanced, PerformanceProfiles.Select(trials, out _));
    }

    [Fact]
    public void Select_stops_at_richest_fitting_neon_rung()
    {
        // Neon only from High up; Ultra is too slow for its 500ms interval → High.
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 100, true, false),
            (PerformanceProfileKind.Low, 150, true, false),
            (PerformanceProfileKind.Balanced, 200, true, false),
            (PerformanceProfileKind.High, 300, true, true),
            (PerformanceProfileKind.Ultra, 900, true, true)
        ]);

        Assert.Equal(PerformanceProfileKind.High, PerformanceProfiles.Select(trials, out _));
    }

    [Fact]
    public void Select_Ultra_when_only_Ultra_reads_neon_and_fits()
    {
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 100, true, false),
            (PerformanceProfileKind.Low, 150, true, false),
            (PerformanceProfileKind.Balanced, 200, true, false),
            (PerformanceProfileKind.High, 300, true, false),
            (PerformanceProfileKind.Ultra, 400, true, true)
        ]);

        Assert.Equal(PerformanceProfileKind.Ultra, PerformanceProfiles.Select(trials, out _));
    }

    [Fact]
    public void Select_falls_back_to_plain_tier_when_neon_rung_is_too_slow()
    {
        // Only Ultra reads neon but 1200ms > 1×500 → fall back to richest plain that fits (High).
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 50, false, false),
            (PerformanceProfileKind.Low, 80, false, false),
            (PerformanceProfileKind.Balanced, 120, true, false),
            (PerformanceProfileKind.High, 200, true, false),
            (PerformanceProfileKind.Ultra, 1200, true, true)
        ]);

        Assert.Equal(PerformanceProfileKind.High, PerformanceProfiles.Select(trials, out _));
    }

    [Fact]
    public void Select_VeryLow_when_nothing_reads_plain()
    {
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 100, false, false),
            (PerformanceProfileKind.Low, 200, false, false),
            (PerformanceProfileKind.Balanced, 300, false, true),
            (PerformanceProfileKind.High, 400, false, true),
            (PerformanceProfileKind.Ultra, 500, false, true)
        ]);

        var chosen = PerformanceProfiles.Select(trials, out var reason);
        Assert.Equal(PerformanceProfileKind.VeryLow, chosen);
        Assert.Contains("plain token not read", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_steps_down_when_chosen_exceeds_ceiling()
    {
        // High is the only neon fit under budget, but 1600ms > 2×750 ceiling → step to Balanced.
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 200, true, false),
            (PerformanceProfileKind.Low, 300, true, false),
            (PerformanceProfileKind.Balanced, 400, true, true),
            (PerformanceProfileKind.High, 1600, true, true),
            (PerformanceProfileKind.Ultra, 4000, true, true)
        ]);

        // High neon is over budget (1600 > 750) so q=2 pick is Balanced (400 <= 900).
        Assert.Equal(PerformanceProfileKind.Balanced, PerformanceProfiles.Select(trials, out _));
    }

    [Fact]
    public void Select_Balanced_when_richer_rungs_over_budget()
    {
        // Fast enough for Balanced, but High/Ultra exceed 1× their intervals.
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 200, true, true),
            (PerformanceProfileKind.Low, 400, true, true),
            (PerformanceProfileKind.Balanced, 700, true, true),
            (PerformanceProfileKind.High, 900, true, true),
            (PerformanceProfileKind.Ultra, 800, true, true)
        ]);

        Assert.Equal(PerformanceProfileKind.Balanced, PerformanceProfiles.Select(trials, out _));
    }

    [Fact]
    public void Apply_Ultra_sets_product_defaults()
    {
        var config = new AppConfig
        {
            OcrIntervalMs = 9999,
            Triggers = [new TriggerConfig { Text = "keep" }]
        };
        PerformanceProfiles.Apply(config, PerformanceProfileKind.Ultra);

        Assert.Equal(500, config.OcrIntervalMs);
        Assert.Equal(1920, config.OcrMaxWidth);
        Assert.Equal(200, config.OcrBoostUpscalePercent);
        Assert.Equal(3840, config.OcrAbsoluteMaxWidth);
        Assert.True(config.OcrEnableAdaptive);
        Assert.True(config.OcrEnableNeon);
        Assert.True(config.OcrEnableTerminal);
        Assert.Equal(4, config.OcrTerminalMaxRegions);
        Assert.Equal(12, config.OcrMaxWindows);
        Assert.Equal(4, config.OcrQueueCapacity);
        Assert.False(config.OcrApplyBudgetWhenIdle);
        Assert.False(config.UseBackgroundPriority);
        Assert.Equal(PerformanceProfileKind.Ultra, config.AppliedPerformanceProfile);
        Assert.Single(config.Triggers);
        Assert.Equal("keep", config.Triggers[0].Text);
    }

    [Fact]
    public void Apply_VeryLow_is_baseline_only()
    {
        var config = new AppConfig();
        PerformanceProfiles.Apply(config, PerformanceProfileKind.VeryLow);

        Assert.Equal(1500, config.OcrIntervalMs);
        Assert.Equal(960, config.OcrMaxWidth);
        Assert.False(config.OcrEnableAdaptive);
        Assert.False(config.OcrEnableNeon);
        Assert.False(config.OcrEnableTerminal);
        Assert.Equal(0, config.OcrTerminalMaxRegions);
        Assert.True(config.UseBackgroundPriority);
    }

    [Fact]
    public void Apply_High_enables_neon()
    {
        var config = new AppConfig();
        PerformanceProfiles.Apply(config, PerformanceProfileKind.High);
        Assert.True(config.OcrEnableNeon);
    }

    [Fact]
    public void All_is_lean_to_rich()
    {
        Assert.Equal(
            new[]
            {
                PerformanceProfileKind.VeryLow,
                PerformanceProfileKind.Low,
                PerformanceProfileKind.Balanced,
                PerformanceProfileKind.High,
                PerformanceProfileKind.Ultra
            },
            PerformanceProfiles.All.Select(d => d.Kind).ToArray());
    }

    [Fact]
    public void MigrateLegacy_maps_old_High_fingerprint_to_Ultra()
    {
        var config = new AppConfig
        {
            AppliedPerformanceProfile = PerformanceProfileKind.High,
            OcrBoostUpscalePercent = 200,
            OcrEnableNeon = true,
            OcrApplyBudgetWhenIdle = false,
            OcrMaxWindows = 12
        };
        PerformanceProfiles.MigrateLegacyAppliedProfile(config);
        Assert.Equal(PerformanceProfileKind.Ultra, config.AppliedPerformanceProfile);
    }

    [Fact]
    public void ScaledUpscale_maps_Ultra_floors()
    {
        Assert.Equal(200, PerformanceProfiles.ScaledUpscale(200, 200));
        Assert.Equal(300, PerformanceProfiles.ScaledUpscale(200, 300));
        Assert.Equal(100, PerformanceProfiles.ScaledUpscale(100, 200));
    }
}
