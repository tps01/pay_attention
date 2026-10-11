namespace PayAttention.Core.Models;

public sealed class TriggerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public MatchMode Match { get; set; } = MatchMode.Contains;
    public CaptureAction Action { get; set; } = CaptureAction.Screenshot;
    /// <summary>Thorough (default) or Fast for briefly visible text.</summary>
    public OcrSpeedMode OcrMode { get; set; } = OcrSpeedMode.Thorough;
    public bool Enabled { get; set; } = false;
}
