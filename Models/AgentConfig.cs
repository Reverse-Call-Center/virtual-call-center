namespace virtual_call_center.Models;

public record AgentConfig
{
    public List<Agent> Agents { get; set; } = new List<Agent>();
    
    public record Agent
    {
        public int Action { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsOnline { get; set; } = false;
        public DateTime LastHeartbeat { get; set; } = DateTime.MinValue;
        public int MaxConcurrentCalls { get; set; } = 1;
        public int CurrentCalls { get; set; } = 0;
        public string? CurrentCallId { get; set; }
    }
}
