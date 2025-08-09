namespace virtual_call_center.Models;

public record IvrConfig
{
    public List<IvrNode> Nodes { get; set; } = new List<IvrNode>();
    
    public record IvrNode
    {
        public int Action { get; set; }
        public string PromptRecording { get; set; } = string.Empty;
        public int TimeoutSeconds { get; set; } = 30;
        public string TimeoutRecording { get; set; } = string.Empty;
        public int TimeoutAction { get; set; } = 0; // Default to hang up
        public string InvalidRecording { get; set; } = string.Empty;
        public List<IvrOption> Options { get; set; } = new List<IvrOption>();
    }

    public record IvrOption
    {
        public int Key { get; set; }
        public int NextAction { get; set; }
    }
}