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
/// Per-trigger OCR thoroughness. Thorough (default) runs the full multi-pass cascade.
/// Fast budgets fallbacks so briefly visible text can be sampled more often.
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

/// <summary>Minimum severity written to app log files.</summary>
public enum LogLevel
{
    Info = 0,
    Debug = 1
}
