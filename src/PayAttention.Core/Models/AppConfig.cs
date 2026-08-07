namespace PayAttention.Core.Models;

public sealed class AppConfig
{
    public int OcrIntervalMs { get; set; } = 750;
    public int DebounceSeconds { get; set; } = 30;
    public int RecordBufferSeconds { get; set; } = 20;
    public int RecordFps { get; set; } = 5;
    public RecordFormat RecordFormat { get; set; } = RecordFormat.Avi;
    public int RecordJpegQuality { get; set; } = 70;
    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Jpeg;
    public int OcrMaxWidth { get; set; } = 1920;
    public CaptureScope CaptureScope { get; set; } = CaptureScope.Monitor;
    public bool OcrInvertColors { get; set; } = false;
    public int OcrUpscalePercent { get; set; } = 100;
    public int SequenceIntervalSeconds { get; set; } = 3;
    public int SequenceBeforeSeconds { get; set; } = 60;
    public int SequenceAfterSeconds { get; set; } = 60;
    public string CaptureDirectory { get; set; } = string.Empty;
    public MonitorSelection MonitorSelection { get; set; } = MonitorSelection.Primary;
    public int SpecificMonitorIndex { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMonitoringOnLaunch { get; set; } = false;
    public bool DebugSaveJsonSidecars { get; set; } = false;
    public string PauseHotkey { get; set; } = "Ctrl+Shift+P";
    public int RetentionMaxAgeDays { get; set; } = 30;
    public int RetentionMaxFiles { get; set; } = 500;
    public List<TriggerConfig> Triggers { get; set; } = new();
}
