namespace PayAttention.Core.Models;

public sealed class TriggerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public MatchMode Match { get; set; } = MatchMode.Contains;
    public CaptureAction Action { get; set; } = CaptureAction.Screenshot;
    /// <summary>Fast (default) for briefly visible text, or Thorough for hard static text.</summary>
    public OcrSpeedMode OcrMode { get; set; } = OcrSpeedMode.Fast;
    public bool Enabled { get; set; } = false;
}
