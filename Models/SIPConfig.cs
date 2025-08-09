namespace virtual_call_center.Models;

public record SIPConfig
{
    public int Port { get; set; } = 5080;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int InitialCallAction { get; set; } = 1000;
    public bool EnableRecording { get; set; } = true;
    public string RecordDisclaimerFile { get; set; } = "disclaimer.wav";
    public int MaxConcurrentCalls { get; set; } = 100;
    public int CallTimeoutSeconds { get; set; } = 1800;
    public List<string> BlacklistedNumbers { get; set; } = new List<string>();
    public bool EnableLogging { get; set; } = true;
    public string LogLevel { get; set; } = "Information";
}
