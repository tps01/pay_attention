using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

/// <summary>
/// Merges global Advanced OCR prep toggles with per-application Inherit/Off/On/Auto overrides.
/// </summary>
public static class OcrPrepResolver
{
    public static OcrPrepFlags Resolve(
        AppConfig config,
        string? processName,
        OcrPrepFlags? autoDecision)
    {
        var row = ApplicationScopeEvaluator.FindMatchingRow(config, processName);
        var auto = autoDecision ?? default;
        return new OcrPrepFlags(
            ResolveOne(row?.OcrAdaptive ?? OcrPrepOverride.Inherit, config.OcrEnableAdaptive, auto.Adaptive),
            ResolveOne(row?.OcrNeon ?? OcrPrepOverride.Inherit, config.OcrEnableNeon, auto.Neon),
            ResolveOne(row?.OcrGray ?? OcrPrepOverride.Inherit, config.OcrEnableGray, auto.Gray),
            ResolveOne(row?.OcrInvert ?? OcrPrepOverride.Inherit, config.OcrInvertColors, auto.Invert),
            ResolveOne(row?.OcrTerminal ?? OcrPrepOverride.Inherit, config.OcrEnableTerminal, auto.Terminal));
    }

    public static bool NeedsAutoAnalysis(AppConfig config, string? processName)
    {
        var row = ApplicationScopeEvaluator.FindMatchingRow(config, processName);
        if (row is null || !row.Enabled)
            return false;
        return row.OcrAdaptive == OcrPrepOverride.Auto
               || row.OcrNeon == OcrPrepOverride.Auto
               || row.OcrGray == OcrPrepOverride.Auto
               || row.OcrInvert == OcrPrepOverride.Auto
               || row.OcrTerminal == OcrPrepOverride.Auto;
    }

    /// <summary>Short label for Settings grid (Inherit / Auto / Neon On / Mixed…).</summary>
    public static string FormatSummary(ApplicationConfig app)
    {
        var overrides = new (string Name, OcrPrepOverride Value)[]
        {
            ("Adaptive", app.OcrAdaptive),
            ("Neon", app.OcrNeon),
            ("Gray", app.OcrGray),
            ("Invert", app.OcrInvert),
            ("Terminal", app.OcrTerminal)
        };

        if (overrides.All(o => o.Value == OcrPrepOverride.Inherit))
            return "Inherit";

        var nonInherit = overrides.Where(o => o.Value != OcrPrepOverride.Inherit).ToList();
        if (nonInherit.All(o => o.Value == OcrPrepOverride.Auto))
            return "Auto";

        if (nonInherit.Count == 1)
        {
            var only = nonInherit[0];
            return only.Value switch
            {
                OcrPrepOverride.On => $"{only.Name} On",
                OcrPrepOverride.Off => $"{only.Name} Off",
                OcrPrepOverride.Auto => $"{only.Name} Auto",
                _ => only.Name
            };
        }

        return "Mixed…";
    }

    private static bool ResolveOne(OcrPrepOverride overrideMode, bool global, bool auto) =>
        overrideMode switch
        {
            OcrPrepOverride.Off => false,
            OcrPrepOverride.On => true,
            OcrPrepOverride.Auto => auto,
            _ => global
        };
}
