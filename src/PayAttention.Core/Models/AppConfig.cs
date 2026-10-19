namespace PayAttention.Core.Models;

public sealed class AppConfig
{
    public int OcrIntervalMs { get; set; } = 500;
    /// <summary>
    /// How closely Fast OCR triggers must match (50–100). 100 requires normalized equality; lower is looser.
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
    public bool OcrInvertColors { get; set; }
    /// <summary>Optional plain luma grayscale OCR pass (no contrast stretch).</summary>
    public bool OcrEnableGray { get; set; }
    public int OcrUpscalePercent { get; set; } = 100;
    /// <summary>Floor for adaptive/neon upscale; terminal/tiles/bands scale from this (100–400).</summary>
    public int OcrBoostUpscalePercent { get; set; } = 200;
    /// <summary>Hard cap on OCR bitmap width after upscale (960–3840).</summary>
    public int OcrAbsoluteMaxWidth { get; set; } = 3840;
    public bool OcrEnableAdaptive { get; set; } = true;
    public bool OcrEnableNeon { get; set; } = true;
    public bool OcrEnableTerminal { get; set; } = true;
    public int OcrTerminalMaxRegions { get; set; } = 4;
    /// <summary>Legacy profile/config field; frozen-frame OCR has no window cap.</summary>
    public int OcrMaxWindows { get; set; } = 12;
    public int OcrQueueCapacity { get; set; } = 4;
    public int OcrCascadeBudgetFastMs { get; set; } = 400;
    public int OcrCascadeBudgetThoroughMs { get; set; } = 8000;
    /// <summary>
    /// When true, cascade time budgets apply even with no backlog.
    /// High (default) keeps budgets only when PendingBehind &gt; 0.
    /// </summary>
    public bool OcrApplyBudgetWhenIdle { get; set; }
    /// <summary>Run capture/OCR workers at BelowNormal priority.</summary>
    public bool UseBackgroundPriority { get; set; }
    /// <summary>Last profile chosen by Optimize for this PC (informational).</summary>
    public PerformanceProfileKind AppliedPerformanceProfile { get; set; } = PerformanceProfileKind.Ultra;
    /// <summary>One-line summary from the last calibration run.</summary>
    public string LastCalibrationSummary { get; set; } = string.Empty;
    public int SequenceIntervalSeconds { get; set; } = 5;
    public int SequenceBeforeSeconds { get; set; } = 30;
    public int SequenceAfterSeconds { get; set; } = 30;
    public string CaptureDirectory { get; set; } = string.Empty;
    public MonitorSelection MonitorSelection { get; set; } = MonitorSelection.Primary;
    public int SpecificMonitorIndex { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMonitoringOnLaunch { get; set; }
    public bool DebugSaveJsonSidecars { get; set; }
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public string PauseHotkey { get; set; } = "Ctrl+Shift+P";
    public int RetentionMaxAgeDays { get; set; } = 14;
    public int RetentionMaxFiles { get; set; } = 200;
    public ApplicationScopeMode ApplicationScope { get; set; } = ApplicationScopeMode.AllMinusBlacklisted;
    public List<TriggerConfig> Triggers { get; set; } = new();
    public List<ApplicationConfig> Applications { get; set; } = new();
}
