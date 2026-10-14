using PayAttention.Core.Models;

namespace PayAttention.Core.Services;

/// <summary>
/// Named performance presets for Optimize for this PC, plus P1dB-style selection from trial results.
/// Ladder (lean → rich): VeryLow → Low → Balanced → High → Ultra.
/// </summary>
public static class PerformanceProfiles
{
    public const string PlainToken = "PAYATTN PLAIN OK";
    public const string NeonToken = "PAYATTN NEON OK";

    public sealed record ProfileDefinition(
        PerformanceProfileKind Kind,
        int OcrIntervalMs,
        int OcrMaxWidth,
        int OcrUpscalePercent,
        int OcrBoostUpscalePercent,
        int OcrAbsoluteMaxWidth,
        bool OcrEnableAdaptive,
        bool OcrEnableNeon,
        bool OcrEnableTerminal,
        int OcrTerminalMaxRegions,
        int OcrMaxWindows,
        int OcrQueueCapacity,
        int OcrCascadeBudgetFastMs,
        int OcrCascadeBudgetThoroughMs,
        bool OcrApplyBudgetWhenIdle,
        bool UseBackgroundPriority);

    /// <param name="MedianMs">
    /// Median over trials of max(plainCascadeMs, neonCascadeMs) — one frame's cascade cost.
    /// </param>
    public sealed record ProfileTrialResult(
        PerformanceProfileKind Kind,
        double MedianMs,
        bool PlainRead,
        bool NeonRead);

    /// <summary>Baseline only — leanest rung.</summary>
    public static ProfileDefinition VeryLow { get; } = new(
        Kind: PerformanceProfileKind.VeryLow,
        OcrIntervalMs: 1500,
        OcrMaxWidth: 960,
        OcrUpscalePercent: 100,
        OcrBoostUpscalePercent: 100,
        OcrAbsoluteMaxWidth: 960,
        OcrEnableAdaptive: false,
        OcrEnableNeon: false,
        OcrEnableTerminal: false,
        OcrTerminalMaxRegions: 0,
        OcrMaxWindows: 1,
        OcrQueueCapacity: 1,
        OcrCascadeBudgetFastMs: 1500,
        OcrCascadeBudgetThoroughMs: 8000,
        OcrApplyBudgetWhenIdle: true,
        UseBackgroundPriority: true);

    public static ProfileDefinition Low { get; } = new(
        Kind: PerformanceProfileKind.Low,
        OcrIntervalMs: 1000,
        OcrMaxWidth: 1280,
        OcrUpscalePercent: 100,
        OcrBoostUpscalePercent: 100,
        OcrAbsoluteMaxWidth: 1600,
        OcrEnableAdaptive: false,
        OcrEnableNeon: false,
        OcrEnableTerminal: true,
        OcrTerminalMaxRegions: 1,
        OcrMaxWindows: 1,
        OcrQueueCapacity: 1,
        OcrCascadeBudgetFastMs: 1000,
        OcrCascadeBudgetThoroughMs: 8000,
        OcrApplyBudgetWhenIdle: true,
        UseBackgroundPriority: true);

    public static ProfileDefinition Balanced { get; } = new(
        Kind: PerformanceProfileKind.Balanced,
        OcrIntervalMs: 900,
        OcrMaxWidth: 1440,
        OcrUpscalePercent: 100,
        OcrBoostUpscalePercent: 125,
        OcrAbsoluteMaxWidth: 1600,
        OcrEnableAdaptive: true,
        OcrEnableNeon: false,
        OcrEnableTerminal: true,
        OcrTerminalMaxRegions: 2,
        OcrMaxWindows: 2,
        OcrQueueCapacity: 2,
        OcrCascadeBudgetFastMs: 600,
        OcrCascadeBudgetThoroughMs: 8000,
        OcrApplyBudgetWhenIdle: true,
        UseBackgroundPriority: true);

    public static ProfileDefinition High { get; } = new(
        Kind: PerformanceProfileKind.High,
        OcrIntervalMs: 750,
        OcrMaxWidth: 1600,
        OcrUpscalePercent: 100,
        OcrBoostUpscalePercent: 150,
        OcrAbsoluteMaxWidth: 1920,
        OcrEnableAdaptive: true,
        OcrEnableNeon: true,
        OcrEnableTerminal: true,
        OcrTerminalMaxRegions: 2,
        OcrMaxWindows: 4,
        OcrQueueCapacity: 2,
        OcrCascadeBudgetFastMs: 400,
        OcrCascadeBudgetThoroughMs: 8000,
        OcrApplyBudgetWhenIdle: true,
        UseBackgroundPriority: true);

    /// <summary>Full product cascade — uncalibrated default.</summary>
    public static ProfileDefinition Ultra { get; } = new(
        Kind: PerformanceProfileKind.Ultra,
        OcrIntervalMs: 500,
        OcrMaxWidth: 1920,
        OcrUpscalePercent: 100,
        OcrBoostUpscalePercent: 200,
        OcrAbsoluteMaxWidth: 3840,
        OcrEnableAdaptive: true,
        OcrEnableNeon: true,
        OcrEnableTerminal: true,
        OcrTerminalMaxRegions: 4,
        OcrMaxWindows: 12,
        OcrQueueCapacity: 4,
        OcrCascadeBudgetFastMs: 400,
        OcrCascadeBudgetThoroughMs: 8000,
        OcrApplyBudgetWhenIdle: false,
        UseBackgroundPriority: false);

    /// <summary>Lean → rich order for benchmarking and P1dB ascent.</summary>
    public static IReadOnlyList<ProfileDefinition> All { get; } =
        [VeryLow, Low, Balanced, High, Ultra];

    public static ProfileDefinition Get(PerformanceProfileKind kind) => kind switch
    {
        PerformanceProfileKind.VeryLow => VeryLow,
        PerformanceProfileKind.Low => Low,
        PerformanceProfileKind.Balanced => Balanced,
        PerformanceProfileKind.High => High,
        _ => Ultra
    };

    public static string DisplayName(PerformanceProfileKind kind) =>
        kind == PerformanceProfileKind.VeryLow ? "Very Low" : kind.ToString();

    /// <summary>
    /// Copies profile knobs onto <paramref name="config"/>. Leaves triggers, debounce, monitors, etc.
    /// </summary>
    public static void Apply(AppConfig config, ProfileDefinition profile)
    {
        config.OcrIntervalMs = profile.OcrIntervalMs;
        config.OcrMaxWidth = profile.OcrMaxWidth;
        config.OcrUpscalePercent = profile.OcrUpscalePercent;
        config.OcrBoostUpscalePercent = profile.OcrBoostUpscalePercent;
        config.OcrAbsoluteMaxWidth = profile.OcrAbsoluteMaxWidth;
        config.OcrEnableAdaptive = profile.OcrEnableAdaptive;
        config.OcrEnableNeon = profile.OcrEnableNeon;
        config.OcrEnableTerminal = profile.OcrEnableTerminal;
        config.OcrTerminalMaxRegions = profile.OcrTerminalMaxRegions;
        config.OcrMaxWindows = profile.OcrMaxWindows;
        config.OcrQueueCapacity = profile.OcrQueueCapacity;
        config.OcrCascadeBudgetFastMs = profile.OcrCascadeBudgetFastMs;
        config.OcrCascadeBudgetThoroughMs = profile.OcrCascadeBudgetThoroughMs;
        config.OcrApplyBudgetWhenIdle = profile.OcrApplyBudgetWhenIdle;
        config.UseBackgroundPriority = profile.UseBackgroundPriority;
        config.AppliedPerformanceProfile = profile.Kind;
    }

    public static void Apply(AppConfig config, PerformanceProfileKind kind) =>
        Apply(config, Get(kind));

    /// <summary>
    /// Upscale floor for a pass, scaled from <paramref name="boost"/> relative to Ultra's 200% base.
    /// </summary>
    public static int ScaledUpscale(int boost, int floorRelativeTo200) =>
        Math.Clamp((int)Math.Round(boost * (floorRelativeTo200 / 200.0)), 100, 400);

    public static int QualityScore(ProfileTrialResult trial)
    {
        if (!trial.PlainRead)
            return 0;
        return trial.NeonRead ? 2 : 1;
    }

    /// <summary>
    /// Cascade may use up to this fraction of the profile's OCR interval and still "fit".
    /// Rungs that finish within budget are preferred over leaner ones on fast hardware.
    /// </summary>
    public const double InteractiveLoadBudget = 1.0;

    /// <summary>
    /// Hard ceiling: never keep a rung whose median cascade exceeds this multiple of its interval.
    /// </summary>
    public const double InteractiveLoadCeiling = 2.0;

    /// <summary>
    /// Hardware-aware pick: prefer the richest rung that still hits the best quality score
    /// (0 miss / 1 plain / 2 plain+neon) while MedianMs ≤ <see cref="InteractiveLoadBudget"/>×
    /// interval. If nothing fits at that quality, try the next-lower quality tier. Finally
    /// soft step-down while MedianMs &gt; <see cref="InteractiveLoadCeiling"/>× interval.
    /// </summary>
    public static PerformanceProfileKind Select(
        IReadOnlyList<ProfileTrialResult> trials,
        out string reason)
    {
        ProfileTrialResult? Find(PerformanceProfileKind kind) =>
            trials.FirstOrDefault(t => t.Kind == kind);

        var maxQuality = 0;
        foreach (var def in All)
        {
            var trial = Find(def.Kind);
            if (trial is null)
                continue;
            maxQuality = Math.Max(maxQuality, QualityScore(trial));
        }

        if (maxQuality < 1)
        {
            reason = $"{DisplayName(PerformanceProfileKind.VeryLow)} (plain token not read)";
            return PerformanceProfileKind.VeryLow;
        }

        PerformanceProfileKind? chosen = null;
        ProfileTrialResult? chosenTrial = null;

        // Without a verified neon read, do not climb into High/Ultra just because the PC is fast —
        // those rungs buy neon/terminal work the calibration never confirmed. Cap at Balanced.
        var richCapIndex = maxQuality >= 2
            ? All.Count - 1
            : IndexOf(PerformanceProfileKind.Balanced);

        // Highest quality first; within a tier, richest rung (up to the cap) that still fits.
        for (var q = maxQuality; q >= 1 && chosen is null; q--)
        {
            for (var i = richCapIndex; i >= 0; i--)
            {
                var def = All[i];
                var trial = Find(def.Kind);
                if (trial is null || QualityScore(trial) < q)
                    continue;
                if (trial.MedianMs > InteractiveLoadBudget * def.OcrIntervalMs)
                    continue;

                chosen = def.Kind;
                chosenTrial = trial;
                break;
            }
        }

        // Nothing fit the budget — take the leanest rung that still has max quality.
        if (chosen is null)
        {
            foreach (var def in All)
            {
                var trial = Find(def.Kind);
                if (trial is null || QualityScore(trial) < maxQuality)
                    continue;
                chosen = def.Kind;
                chosenTrial = trial;
                break;
            }
        }

        chosen ??= PerformanceProfileKind.VeryLow;

        // Soft interactivity ceiling: step down while cascade is more than 2× the interval.
        while (chosen != PerformanceProfileKind.VeryLow && chosenTrial is not null)
        {
            var def = Get(chosen.Value);
            if (chosenTrial.MedianMs <= InteractiveLoadCeiling * def.OcrIntervalMs)
                break;

            var idx = IndexOf(chosen.Value);
            if (idx <= 0)
                break;

            chosen = All[idx - 1].Kind;
            chosenTrial = Find(chosen.Value);
            if (chosenTrial is null)
                break;
        }

        var picked = chosen.Value;
        var name = DisplayName(picked);
        var ms = chosenTrial?.MedianMs ?? 0;
        var qFinal = chosenTrial is null ? maxQuality : QualityScore(chosenTrial);
        var neon = chosenTrial?.NeonRead == true ? "neon read" : "neon missed";
        var load = Get(picked).OcrIntervalMs <= 0
            ? 0
            : ms / Get(picked).OcrIntervalMs;
        reason = $"{name}: quality {qFinal}, {ms:0}ms ({neon}, {load:0.00}× interval)";
        return picked;
    }

    private static int IndexOf(PerformanceProfileKind kind)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i].Kind == kind)
                return i;
        }

        return -1;
    }

    public static string FormatSummary(
        PerformanceProfileKind chosen,
        IReadOnlyList<ProfileTrialResult> trials)
    {
        static string Part(ProfileTrialResult? t)
        {
            if (t is null)
                return "?";
            var plain = t.PlainRead ? "plain" : "no-plain";
            var neon = t.NeonRead ? "neon" : "no-neon";
            return $"{t.MedianMs:0}ms ({plain}/{neon})";
        }

        var parts = All.Select(d =>
            $"{DisplayName(d.Kind)} {Part(trials.FirstOrDefault(t => t.Kind == d.Kind))}");
        return $"{DisplayName(chosen)} — {string.Join("; ", parts)}";
    }

    /// <summary>
    /// Map legacy 3-rung labels (High/Balanced/Low) onto the five-rung ladder using knob fingerprints.
    /// </summary>
    public static void MigrateLegacyAppliedProfile(AppConfig config)
    {
        if (!Enum.IsDefined(config.AppliedPerformanceProfile))
        {
            config.AppliedPerformanceProfile = PerformanceProfileKind.Ultra;
            return;
        }

        // Old "high" (full cascade) deserialized as High after renumber — detect Ultra fingerprint.
        if (config.AppliedPerformanceProfile == PerformanceProfileKind.High &&
            config.OcrBoostUpscalePercent >= 200 &&
            config.OcrEnableNeon &&
            !config.OcrApplyBudgetWhenIdle &&
            config.OcrMaxWindows >= 12)
        {
            config.AppliedPerformanceProfile = PerformanceProfileKind.Ultra;
            return;
        }

        // Old "balanced" (750ms / 4 windows) may deserialize as Balanced — move to High.
        if (config.AppliedPerformanceProfile == PerformanceProfileKind.Balanced &&
            config.OcrIntervalMs == 750 &&
            config.OcrMaxWindows == 4 &&
            config.OcrBoostUpscalePercent == 150)
        {
            config.AppliedPerformanceProfile = PerformanceProfileKind.High;
        }
    }
}
