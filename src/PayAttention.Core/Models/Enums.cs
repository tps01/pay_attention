namespace PayAttention.Core.Models;

public enum CaptureAction
{
    Screenshot = 0,
    Record = 1,
    Sequence = 2
}

public enum MatchMode
{
    Contains = 0,
    Exact = 1,
    Regex = 2
}

/// <summary>
/// Per-trigger OCR thoroughness. Fast (default) budgets fallbacks so briefly visible
/// text can be sampled more often. Thorough runs the full multi-pass cascade when idle
/// and digs longer under backlog for hard static text.
/// </summary>
public enum OcrSpeedMode
{
    Thorough = 0,
    Fast = 1
}

public enum MonitorSelection
{
    Primary = 0,
    All = 1,
    Specific = 2
}

public enum CaptureScope
{
    Monitor = 0,
    ForegroundWindow = 1
}

public enum RecordFormat
{
    Avi = 0,
    Gif = 1
}

public enum ScreenshotFormat
{
    Jpeg = 0,
    Png = 1
}

public enum ApplicationListMode
{
    Whitelist = 0,
    Blacklist = 1
}

public enum ApplicationScopeMode
{
    All = 0,
    Whitelisted = 1,
    AllMinusBlacklisted = 2
}

/// <summary>Which triggers an application row applies to.</summary>
public enum ApplicationTriggerFilter
{
    All = 0,
    Selected = 1,
    None = 2
}

/// <summary>
/// Per-application OCR prep pass override. Inherit uses the global Advanced toggle;
/// Auto samples the app’s colors to pick On/Off.
/// </summary>
public enum OcrPrepOverride
{
    Inherit = 0,
    Off = 1,
    On = 2,
    Auto = 3
}

/// <summary>Minimum severity written to app log files.</summary>
public enum LogLevel
{
    Info = 0,
    Debug = 1,
    Verbose = 2
}

/// <summary>
/// Preset from Optimize for this PC. Values are copied into individual AppConfig fields;
/// the enum is informational (which profile last applied). UI shows VeryLow as "Very Low".
/// </summary>
public enum PerformanceProfileKind
{
    VeryLow = 0,
    Low = 1,
    Balanced = 2,
    High = 3,
    Ultra = 4
}
