namespace virtual_call_center.Models;

public record QueueConfig
{
    public List<Queue> Queues { get; set; } = new List<Queue>();
    
    public record Queue
    {
        public int Action { get; set; }
        public string HoldRecording { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 0; // Default to no timeout
        public int TimeoutAction { get; set; } = 0; // Default to hang up
        public int AnnounceTime { get; set; } = 300;
        public string AnnounceRecording { get; set; } = string.Empty;
    }
}