namespace PayAttention.Core.Models;

public sealed class ApplicationConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string ProcessName { get; set; } = string.Empty;
    public ApplicationListMode ListMode { get; set; } = ApplicationListMode.Blacklist;
    public int DebounceSeconds { get; set; } = 30;
    public ApplicationTriggerFilter TriggerFilter { get; set; } = ApplicationTriggerFilter.All;
    public List<Guid> TriggerIds { get; set; } = new();

    /// <summary>Override global adaptive-contrast OCR pass for this process.</summary>
    public OcrPrepOverride OcrAdaptive { get; set; } = OcrPrepOverride.Inherit;

    /// <summary>Override global neon high-contrast OCR pass for this process.</summary>
    public OcrPrepOverride OcrNeon { get; set; } = OcrPrepOverride.Inherit;

    /// <summary>Override global plain grayscale OCR pass for this process.</summary>
    public OcrPrepOverride OcrGray { get; set; } = OcrPrepOverride.Inherit;

    /// <summary>Override global extra invert OCR pass for this process.</summary>
    public OcrPrepOverride OcrInvert { get; set; } = OcrPrepOverride.Inherit;

    /// <summary>Override global terminal ROI OCR passes for this process.</summary>
    public OcrPrepOverride OcrTerminal { get; set; } = OcrPrepOverride.Inherit;
}
