namespace PayAttention.Core.Models;

public sealed class ApplicationConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string ProcessName { get; set; } = string.Empty;
    public ApplicationListMode ListMode { get; set; } = ApplicationListMode.Blacklist;
    public int DebounceSeconds { get; set; } = 30;
    public ApplicationTriggerFilter TriggerFilter { get; set; } = ApplicationTriggerFilter.All;
    public List<Guid> TriggerIds { get; set; } = new();
}
