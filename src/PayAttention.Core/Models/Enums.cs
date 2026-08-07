namespace PayAttention.Core.Models;

public enum CaptureAction
{
    Screenshot = 0,
    Record = 1
}

public enum MatchMode
{
    Contains = 0,
    Exact = 1,
    Regex = 2
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
