namespace virtual_call_center.Models;

public class DynamicAgent
{
    public int AgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsOnline { get; set; } = false;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public int MaxConcurrentCalls { get; set; } = 1;
    public int CurrentCalls { get; set; } = 0;
    public string? CurrentCallId { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
}
