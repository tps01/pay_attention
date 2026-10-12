namespace PayAttention.Core.Models;

public sealed class AppConfig
{
    public int OcrIntervalMs { get; set; } = 500;
    /// <summary>
    /// How closely Fast OCR triggers must match (50–100). 100 ≈ today’s rules; lower is looser.
    /// </summary>
    public int FastMatchThresholdPercent { get; set; } = 90;
    public int DebounceSeconds { get; set; } = 30;
    public int RecordBufferSeconds { get; set; } = 15;
    public int RecordFps { get; set; } = 3;
    public RecordFormat RecordFormat { get; set; } = RecordFormat.Avi;
    public int RecordJpegQuality { get; set; } = 60;
    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Jpeg;
    public int OcrMaxWidth { get; set; } = 1920;
    public CaptureScope CaptureScope { get; set; } = CaptureScope.Monitor;
    public bool OcrInvertColors { get; set; } = false;
    public int OcrUpscalePercent { get; set; } = 100;
    public int SequenceIntervalSeconds { get; set; } = 5;
    public int SequenceBeforeSeconds { get; set; } = 30;
    public int SequenceAfterSeconds { get; set; } = 30;
    public string CaptureDirectory { get; set; } = string.Empty;
    public MonitorSelection MonitorSelection { get; set; } = MonitorSelection.Primary;
    public int SpecificMonitorIndex { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMonitoringOnLaunch { get; set; } = false;
    public bool DebugSaveJsonSidecars { get; set; } = false;
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public string PauseHotkey { get; set; } = "Ctrl+Shift+P";
    public int RetentionMaxAgeDays { get; set; } = 14;
    public int RetentionMaxFiles { get; set; } = 200;
    public ApplicationScopeMode ApplicationScope { get; set; } = ApplicationScopeMode.AllMinusBlacklisted;
    public List<TriggerConfig> Triggers { get; set; } = new();
    public List<ApplicationConfig> Applications { get; set; } = new();
}
