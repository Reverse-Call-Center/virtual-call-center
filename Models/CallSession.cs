using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using System.Collections.Concurrent;

namespace virtual_call_center.Models;

public class CallSession
{
    public string CallId { get; set; } = Guid.NewGuid().ToString();
    public string CallerNumber { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public CallState State { get; set; } = CallState.Incoming;
    public int CurrentAction { get; set; }
    public SIPUserAgent? UserAgent { get; set; }
    public VoIPMediaSession? MediaSession { get; set; }
    public int? AssignedAgentId { get; set; }
    public DateTime? QueuedTime { get; set; }
    public List<int> VisitedActions { get; set; } = new List<int>();
    public Dictionary<string, object> SessionData { get; set; } = new Dictionary<string, object>();
    public bool RecordingEnabled { get; set; } = false;
    public string? RecordingPath { get; set; }
    public ConcurrentQueue<byte[]> AudioBuffer { get; set; } = new ConcurrentQueue<byte[]>();
    public bool IsOnHold { get; set; } = false;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

public enum CallState
{
    Incoming,
    Connected,
    InIVR,
    InQueue,
    ConnectedToAgent,
    OnHold,
    Recording,
    Ended,
    Failed
}
