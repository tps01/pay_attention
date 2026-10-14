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

    private static IReadOnlyList<PerformanceProfiles.ProfileTrialResult> Ladder(
        (PerformanceProfileKind Kind, double Ms, bool Plain, bool Neon)[] rows) =>
        rows.Select(r => Trial(r.Kind, r.Ms, r.Plain, r.Neon)).ToList();

    [Fact]
    public void DisplayName_VeryLow_is_spaced()
    {
        Assert.Equal("Very Low", PerformanceProfiles.DisplayName(PerformanceProfileKind.VeryLow));
        Assert.Equal("Ultra", PerformanceProfiles.DisplayName(PerformanceProfileKind.Ultra));
    }

    [Fact]
    public void Select_stops_at_knee_when_richer_adds_no_quality()
    {
        // Very Low already reads both; richer rungs add nothing → Very Low.
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 200, true, true),
            (PerformanceProfileKind.Low, 300, true, true),
            (PerformanceProfileKind.Balanced, 400, true, true),
            (PerformanceProfileKind.High, 500, true, true),
            (PerformanceProfileKind.Ultra, 600, true, true)
        ]);

        var chosen = PerformanceProfiles.Select(trials, out var reason);
        Assert.Equal(PerformanceProfileKind.VeryLow, chosen);
        Assert.Contains("Very Low", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_ascends_until_neon_then_stops()
    {
        // Plain-only until High gains neon; Ultra same quality → High.
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 100, true, false),
            (PerformanceProfileKind.Low, 150, true, false),
            (PerformanceProfileKind.Balanced, 200, true, false),
            (PerformanceProfileKind.High, 300, true, true),
            (PerformanceProfileKind.Ultra, 450, true, true)
        ]);

        Assert.Equal(PerformanceProfileKind.High, PerformanceProfiles.Select(trials, out _));
    }

    [Fact]
    public void Select_Ultra_when_only_Ultra_reads_neon()
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
    public void Select_skips_miss_rungs_until_plain_appears()
    {
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 50, false, false),
            (PerformanceProfileKind.Low, 80, false, false),
            (PerformanceProfileKind.Balanced, 120, true, false),
            (PerformanceProfileKind.High, 200, true, true),
            (PerformanceProfileKind.Ultra, 350, true, true)
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
    public void Select_steps_down_when_knee_is_too_slow()
    {
        // High is the quality knee but 3× its 750ms interval → soft step toward Low/Balanced.
        var trials = Ladder(
        [
            (PerformanceProfileKind.VeryLow, 200, true, false),
            (PerformanceProfileKind.Low, 300, true, false),
            (PerformanceProfileKind.Balanced, 400, true, true),
            (PerformanceProfileKind.High, 3000, true, true),
            (PerformanceProfileKind.Ultra, 4000, true, true)
        ]);

        // High improves to neon (q=2) then Ultra plateaus → knee High, but 3000 > 2*750 → step to Balanced (400 <= 2*900).
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
