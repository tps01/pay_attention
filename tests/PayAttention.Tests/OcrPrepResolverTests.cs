using PayAttention.Core.Models;
using PayAttention.Core.Services;
using Xunit;

namespace PayAttention.Tests;

public class OcrPrepResolverTests
{
    private static AppConfig Globals(
        bool adaptive = false,
        bool neon = false,
        bool gray = false,
        bool invert = false,
        bool terminal = false,
        params ApplicationConfig[] apps) =>
        new()
        {
            OcrEnableAdaptive = adaptive,
            OcrEnableNeon = neon,
            OcrEnableGray = gray,
            OcrInvertColors = invert,
            OcrEnableTerminal = terminal,
            Applications = apps.ToList()
        };

    private static ApplicationConfig App(
        string process,
        OcrPrepOverride adaptive = OcrPrepOverride.Inherit,
        OcrPrepOverride neon = OcrPrepOverride.Inherit,
        OcrPrepOverride gray = OcrPrepOverride.Inherit,
        OcrPrepOverride invert = OcrPrepOverride.Inherit,
        OcrPrepOverride terminal = OcrPrepOverride.Inherit) =>
        new()
        {
            Enabled = true,
            ProcessName = process,
            ListMode = ApplicationListMode.Whitelist,
            OcrAdaptive = adaptive,
            OcrNeon = neon,
            OcrGray = gray,
            OcrInvert = invert,
            OcrTerminal = terminal
        };

    [Fact]
    public void Resolve_missing_row_uses_globals()
    {
        var config = Globals(adaptive: true, neon: true);
        var flags = OcrPrepResolver.Resolve(config, "chrome", autoDecision: null);
        Assert.True(flags.Adaptive);
        Assert.True(flags.Neon);
        Assert.False(flags.Gray);
    }

    [Fact]
    public void Resolve_On_overrides_global_off()
    {
        var config = Globals(neon: false, apps: App("wt", neon: OcrPrepOverride.On));
        var flags = OcrPrepResolver.Resolve(config, "wt", autoDecision: null);
        Assert.True(flags.Neon);
        Assert.False(flags.Adaptive);
    }

    [Fact]
    public void Resolve_Off_overrides_global_on()
    {
        var config = Globals(neon: true, apps: App("notepad", neon: OcrPrepOverride.Off));
        var flags = OcrPrepResolver.Resolve(config, "notepad", autoDecision: null);
        Assert.False(flags.Neon);
    }

    [Fact]
    public void Resolve_Auto_uses_analyzer_decision()
    {
        var config = Globals(
            neon: false,
            apps: App("term", neon: OcrPrepOverride.Auto, terminal: OcrPrepOverride.Auto));
        var auto = new OcrPrepFlags(false, Neon: true, false, false, Terminal: true);
        var flags = OcrPrepResolver.Resolve(config, "term", auto);
        Assert.True(flags.Neon);
        Assert.True(flags.Terminal);
        Assert.False(flags.Adaptive);
    }

    [Fact]
    public void NeedsAutoAnalysis_true_when_any_pass_is_Auto()
    {
        var config = Globals(apps: App("x", gray: OcrPrepOverride.Auto));
        Assert.True(OcrPrepResolver.NeedsAutoAnalysis(config, "x"));
        Assert.False(OcrPrepResolver.NeedsAutoAnalysis(config, "other"));
    }

    [Fact]
    public void FormatSummary_inherit_auto_mixed()
    {
        Assert.Equal("Inherit", OcrPrepResolver.FormatSummary(App("a")));
        Assert.Equal("Auto", OcrPrepResolver.FormatSummary(App(
            "a",
            adaptive: OcrPrepOverride.Auto,
            neon: OcrPrepOverride.Auto,
            gray: OcrPrepOverride.Auto,
            invert: OcrPrepOverride.Auto,
            terminal: OcrPrepOverride.Auto)));
        Assert.Equal("Neon On", OcrPrepResolver.FormatSummary(App("a", neon: OcrPrepOverride.On)));
        Assert.Equal("Mixed…", OcrPrepResolver.FormatSummary(App(
            "a", neon: OcrPrepOverride.On, adaptive: OcrPrepOverride.Off)));
    }
}
