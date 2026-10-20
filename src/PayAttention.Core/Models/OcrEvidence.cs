using System.Text.Json;

namespace PayAttention.Core.Models;

/// <summary>Capture-time geometry and OCR settings, without trigger text or desktop content.</summary>
public sealed class OcrEvidence
{
    public int Version { get; set; } = 1;
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public List<OcrRegion> Windows { get; set; } = [];
    public List<OcrRegion> Monitors { get; set; } = [];
    public Dictionary<string, JsonElement> Settings { get; set; } = [];
    public OcrPrepOverride Adaptive { get; set; }
    public OcrPrepOverride Neon { get; set; }
    public OcrPrepOverride Gray { get; set; }
    public OcrPrepOverride Invert { get; set; }
    public OcrPrepOverride Terminal { get; set; }

    public void SaveSettings(AppConfig config, ApplicationConfig? row)
    {
        Settings = typeof(AppConfig).GetProperties()
            .Where(p => p.Name.StartsWith("Ocr", StringComparison.Ordinal) || p.Name == nameof(AppConfig.FastMatchThresholdPercent))
            .ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.GetValue(config), p.PropertyType));
        Adaptive = row?.OcrAdaptive ?? OcrPrepOverride.Inherit;
        Neon = row?.OcrNeon ?? OcrPrepOverride.Inherit;
        Gray = row?.OcrGray ?? OcrPrepOverride.Inherit;
        Invert = row?.OcrInvert ?? OcrPrepOverride.Inherit;
        Terminal = row?.OcrTerminal ?? OcrPrepOverride.Inherit;
    }

    public AppConfig ReplayConfig(TriggerConfig trigger)
    {
        if (Version != 1) throw new InvalidOperationException($"Unsupported OCR evidence version {Version}");
        var config = new AppConfig { Triggers = [trigger], ApplicationScope = ApplicationScopeMode.All };
        foreach (var property in typeof(AppConfig).GetProperties().Where(p =>
                     p.Name.StartsWith("Ocr", StringComparison.Ordinal) || p.Name == nameof(AppConfig.FastMatchThresholdPercent)))
            if (Settings.TryGetValue(property.Name, out var value))
                property.SetValue(config, value.Deserialize(property.PropertyType));
        config.Applications = [new ApplicationConfig
        {
            ProcessName = "evidence-replay", Enabled = true, ListMode = ApplicationListMode.Whitelist,
            OcrAdaptive = Adaptive, OcrNeon = Neon, OcrGray = Gray,
            OcrInvert = Invert, OcrTerminal = Terminal
        }];
        // Replay has no producer/backlog; search the frozen image to completion.
        config.OcrApplyBudgetWhenIdle = false;
        return config;
    }
}

/// <summary>Rectangle in saved-image coordinates, independent of desktop origin.</summary>
public sealed record OcrRegion(int X, int Y, int Width, int Height);
