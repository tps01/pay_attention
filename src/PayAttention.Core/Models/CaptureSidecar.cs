namespace PayAttention.Core.Models;

public sealed class CaptureSidecar
{
    public DateTimeOffset Timestamp { get; set; }
    public string MatchedText { get; set; } = string.Empty;
    public string TriggerText { get; set; } = string.Empty;
    public MatchMode MatchMode { get; set; }
    public CaptureAction Action { get; set; }
    public string CaptureFile { get; set; } = string.Empty;
    public string? ScreenshotFile { get; set; }
    public int MonitorIndex { get; set; }
    public string MonitorName { get; set; } = string.Empty;
    public string OcrSnippet { get; set; } = string.Empty;
}
