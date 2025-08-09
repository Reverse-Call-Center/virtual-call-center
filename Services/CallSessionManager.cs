using System.Collections.Concurrent;
using virtual_call_center.Models;

namespace virtual_call_center.Services;

public class CallSessionManager
{
    private readonly ILogger<CallSessionManager> _logger;
    private readonly ConfigurationManager _configManager;
    private readonly ConcurrentDictionary<string, CallSession> _activeCalls;
    private readonly Timer _cleanupTimer;
    private readonly object _lockObject = new object();

    public CallSessionManager(ILogger<CallSessionManager> logger, ConfigurationManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
        _activeCalls = new ConcurrentDictionary<string, CallSession>();
        
        _cleanupTimer = new Timer(CleanupInactiveCalls, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        
        _logger.LogInformation("CallSessionManager initialized with capacity for {MaxCalls} concurrent calls", 
            _configManager.SipConfig.MaxConcurrentCalls);
    }

    public bool CanAcceptNewCall()
    {
        return _activeCalls.Count < _configManager.SipConfig.MaxConcurrentCalls;
    }

    public string CreateCallSession(string callerNumber)
    {
        if (!CanAcceptNewCall())
        {
            _logger.LogWarning("Cannot accept new call from {CallerNumber} - at capacity ({CurrentCalls}/{MaxCalls})", 
                callerNumber, _activeCalls.Count, _configManager.SipConfig.MaxConcurrentCalls);
            throw new InvalidOperationException("Call center at capacity");
        }

        var callId = Guid.NewGuid().ToString();
        var session = new CallSession
        {
            CallId = callId,
            CallerNumber = callerNumber,
            CurrentAction = _configManager.SipConfig.InitialCallAction,
            StartTime = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };

        _activeCalls.TryAdd(callId, session);
        
        _logger.LogInformation("Created call session {CallId} for {CallerNumber} ({CurrentCalls}/{MaxCalls})", 
            callId, callerNumber, _activeCalls.Count, _configManager.SipConfig.MaxConcurrentCalls);
        
        return callId;
    }

    public CallSession? GetCallSession(string callId)
    {
        _activeCalls.TryGetValue(callId, out var session);
        return session;
    }

    public void UpdateCallActivity(string callId)
    {
        if (_activeCalls.TryGetValue(callId, out var session))
        {
            session.LastActivity = DateTime.UtcNow;
        }
    }

    public bool RemoveCallSession(string callId)
    {
        var removed = _activeCalls.TryRemove(callId, out var session);
        if (removed && session != null)
        {
            var duration = DateTime.UtcNow - session.StartTime;
            _logger.LogInformation("Removed call session {CallId} after {Duration:mm\\:ss} ({CurrentCalls}/{MaxCalls})", 
                callId, duration, _activeCalls.Count, _configManager.SipConfig.MaxConcurrentCalls);
        }
        return removed;
    }

    public int GetActiveCallCount()
    {
        return _activeCalls.Count;
    }

    public List<CallSession> GetActiveCallsInQueue()
    {
        return _activeCalls.Values
            .Where(c => c.State == CallState.InQueue)
            .OrderBy(c => c.QueuedTime)
            .ToList();
    }

    public List<CallSession> GetActiveCallsWithAgent()
    {
        return _activeCalls.Values
            .Where(c => c.State == CallState.ConnectedToAgent && c.AssignedAgentId.HasValue)
            .ToList();
    }

    public void AssignCallToAgent(CallSession session, int agentId)
    {
        session.AssignedAgentId = agentId;
        _logger.LogInformation("Call {CallId} assigned to agent {AgentId}", session.CallId, agentId);
    }

    public void EndCall(string callId)
    {
        if (_activeCalls.TryGetValue(callId, out var session))
        {
            session.State = CallState.Ended;
            RemoveCallSession(callId);
        }
    }

    public List<CallSession> GetAllCalls()
    {
        return _activeCalls.Values.ToList();
    }

    private void CleanupInactiveCalls(object? state)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(_configManager.SipConfig.CallTimeoutSeconds);
            var cutoffTime = DateTime.UtcNow - timeout;
            
            var inactiveCalls = _activeCalls.Values
                .Where(c => c.LastActivity < cutoffTime && c.State != CallState.ConnectedToAgent)
                .ToList();

            foreach (var call in inactiveCalls)
            {
                _logger.LogInformation("Cleaning up inactive call {CallId} (last activity: {LastActivity})", 
                    call.CallId, call.LastActivity);
                
                call.State = CallState.Ended;
                call.UserAgent?.Hangup();
                RemoveCallSession(call.CallId);
            }

            if (inactiveCalls.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} inactive calls", inactiveCalls.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during call cleanup");
        }
    }
}
