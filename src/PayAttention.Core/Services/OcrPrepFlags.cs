namespace PayAttention.Core.Services;

/// <summary>Which OCR prep passes run for a frame after global + per-app resolution.</summary>
public readonly record struct OcrPrepFlags(
    bool Adaptive,
    bool Neon,
    bool Gray,
    bool Invert,
    bool Terminal)
{
    public static OcrPrepFlags FromGlobals(Models.AppConfig config) =>
        new(
            config.OcrEnableAdaptive,
            config.OcrEnableNeon,
            config.OcrEnableGray,
            config.OcrInvertColors,
            config.OcrEnableTerminal);
}
