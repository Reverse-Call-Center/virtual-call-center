using virtual_call_center.Models;
using virtual_call_center.Services;

namespace virtual_call_center.Services;

public class CallRouter
{
    private readonly ILogger<CallRouter> _logger;
    private readonly ConfigurationManager _configManager;
    private readonly AudioService _audioService;
    private readonly AgentManager _agentManager;
    private readonly DynamicAgentManager _dynamicAgentManager;

    public CallRouter(ILogger<CallRouter> logger, ConfigurationManager configManager, 
        AudioService audioService, AgentManager agentManager, DynamicAgentManager dynamicAgentManager)
    {
        _logger = logger;
        _configManager = configManager;
        _audioService = audioService;
        _agentManager = agentManager;
        _dynamicAgentManager = dynamicAgentManager;
    }

    /// <summary>
    /// Routes a call to the appropriate action based on the current action number and optional DTMF input
    /// </summary>
    /// <param name="session">The call session to route</param>
    /// <param name="dtmfKey">Optional DTMF key pressed by caller</param>
    /// <returns>The next action number to process</returns>
    public async Task<int> RouteCall(CallSession session, int? dtmfKey = null)
    {
        _logger.LogInformation("Routing call {CallId} to action {Action}, DTMF: {DTMF}", 
            session.CallId, session.CurrentAction, dtmfKey);

        var action = session.CurrentAction;
        
        if (action >= 1000 && action <= 1999)
        {
            return await HandleIvrAction(session, dtmfKey);
        }
        else if (action >= 2000 && action <= 2999)
        {
            return await HandleQueueAction(session);
        }
        else if (action >= 5000 && action <= 5999)
        {
            return await HandleAgentAction(session);
        }
        else if (action == 0)
        {
            await EndCall(session, "Hangup action");
            return 0;
        }
        else
        {
            _logger.LogWarning("Unknown action {Action} for call {CallId}", action, session.CallId);
            await EndCall(session, "Unknown action");
            return 0;
        }
    }

    private async Task<int> HandleIvrAction(CallSession session, int? dtmfKey)
    {
        var ivrNode = _configManager.GetIvrNode(session.CurrentAction);
        if (ivrNode == null)
        {
            _logger.LogError("IVR node not found for action {Action}", session.CurrentAction);
            return 0;
        }

        session.State = CallState.InIVR;

        if (dtmfKey.HasValue)
        {
            var option = ivrNode.Options.FirstOrDefault(o => o.Key == dtmfKey.Value);
            if (option != null)
            {
                _logger.LogInformation("Call {CallId} selected option {Key} -> action {NextAction}", 
                    session.CallId, dtmfKey.Value, option.NextAction);
                return option.NextAction;
            }
            else
            {
                _logger.LogInformation("Call {CallId} selected invalid option {Key}", session.CallId, dtmfKey.Value);
                if (!string.IsNullOrEmpty(ivrNode.InvalidRecording))
                {
                    await _audioService.PlayRecording(session, ivrNode.InvalidRecording);
                }
                return session.CurrentAction;
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(ivrNode.PromptRecording))
            {
                await _audioService.PlayRecording(session, ivrNode.PromptRecording);
            }
            
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(ivrNode.TimeoutSeconds));
            var dtmfTask = WaitForDtmf(session);
            
            var completedTask = await Task.WhenAny(timeoutTask, dtmfTask);
            
            if (completedTask == timeoutTask)
            {
                _logger.LogInformation("Call {CallId} timed out in IVR", session.CallId);
                if (!string.IsNullOrEmpty(ivrNode.TimeoutRecording))
                {
                    await _audioService.PlayRecording(session, ivrNode.TimeoutRecording);
                }
                return ivrNode.TimeoutAction;
            }
            else
            {
                var receivedDtmf = await dtmfTask;
                return await HandleIvrAction(session, receivedDtmf);
            }
        }
    }

    private async Task<int> HandleQueueAction(CallSession session)
    {
        var queue = _configManager.GetQueue(session.CurrentAction);
        if (queue == null)
        {
            _logger.LogError("Queue not found for action {Action}", session.CurrentAction);
            return 0;
        }

        session.State = CallState.InQueue;
        session.QueuedTime = DateTime.UtcNow;

        _logger.LogInformation("Call {CallId} entering queue {Action}", session.CallId, session.CurrentAction);

        var availableAgent = _dynamicAgentManager.GetAvailableAgents().FirstOrDefault();
        if (availableAgent != null)
        {
            _logger.LogInformation("Immediately routing call {CallId} to agent {AgentId}", 
                session.CallId, availableAgent.AgentId);
            return availableAgent.AgentId;
        }

        if (!string.IsNullOrEmpty(queue.HoldRecording))
        {
            await _audioService.PlayRecording(session, queue.HoldRecording, loop: true);
        }

        var announceTimer = Task.Delay(TimeSpan.FromSeconds(queue.AnnounceTime));
        var agentAvailableTask = WaitForAgentAvailable(session);

        while (session.State == CallState.InQueue)
        {
            var completedTask = await Task.WhenAny(announceTimer, agentAvailableTask);
            
            if (completedTask == announceTimer && queue.AnnounceTime > 0)
            {
                if (!string.IsNullOrEmpty(queue.AnnounceRecording))
                {
                    await _audioService.PlayRecording(session, queue.AnnounceRecording);
                }
                announceTimer = Task.Delay(TimeSpan.FromSeconds(queue.AnnounceTime));
            }
            else if (completedTask == agentAvailableTask)
            {
                var agent = await agentAvailableTask;
                if (agent != null)
                {
                    _logger.LogInformation("Agent {AgentId} became available for call {CallId}", 
                        agent.AgentId, session.CallId);
                    return agent.AgentId;
                }
            }

            if (queue.TimeoutSeconds > 0)
            {
                var queueTime = DateTime.UtcNow - session.QueuedTime.Value;
                if (queueTime.TotalSeconds >= queue.TimeoutSeconds)
                {
                    _logger.LogInformation("Call {CallId} timed out in queue after {Seconds} seconds", 
                        session.CallId, queueTime.TotalSeconds);
                    return queue.TimeoutAction;
                }
            }

            await Task.Delay(1000);
        }

        return 0;
    }

    private async Task<int> HandleAgentAction(CallSession session)
    {
        var agent = _dynamicAgentManager.GetAgent(session.CurrentAction);
        if (agent == null || !agent.IsOnline)
        {
            _logger.LogWarning("Agent {AgentId} not available for call {CallId}", 
                session.CurrentAction, session.CallId);
            return 0;
        }

        session.State = CallState.ConnectedToAgent;
        session.AssignedAgentId = agent.AgentId;
        
        _logger.LogInformation("Connecting call {CallId} to agent {AgentId}", 
            session.CallId, agent.AgentId);

        var success = await _agentManager.AssignCallToAgent(session, agent.AgentId);
        if (!success)
        {
            _logger.LogWarning("Failed to assign call {CallId} to agent {AgentId}", 
                session.CallId, agent.AgentId);
            return 0;
        }

        _dynamicAgentManager.UpdateAgentCallCount(agent.AgentId, agent.CurrentCalls + 1, session.CallId);
        
        await WaitForCallEnd(session);
        
        _dynamicAgentManager.UpdateAgentCallCount(agent.AgentId, Math.Max(0, agent.CurrentCalls - 1), null);
        
        return 0;
    }

    private async Task<int> WaitForDtmf(CallSession session)
    {
        // Create a TaskCompletionSource to wait for DTMF input
        var tcs = new TaskCompletionSource<int>();
        session.DtmfTaskCompletionSource = tcs;
        
        try
        {
            return await tcs.Task;
        }
        finally
        {
            session.DtmfTaskCompletionSource = null;
        }
    }

    private async Task<DynamicAgent?> WaitForAgentAvailable(CallSession session)
    {
        while (session.State == CallState.InQueue)
        {
            var agent = _dynamicAgentManager.GetAvailableAgents().FirstOrDefault();
            if (agent != null)
                return agent;
                
            await Task.Delay(1000);
        }
        return null;
    }

    private async Task EndCall(CallSession session, string reason)
    {
        _logger.LogInformation("Ending call {CallId}: {Reason}", session.CallId, reason);
        session.State = CallState.Ended;
        
        if (session.AssignedAgentId.HasValue)
        {
            await _agentManager.EndAgentCall(session.CallId, session.AssignedAgentId.Value, reason);
        }
        
        session.UserAgent?.Hangup();
    }

    private async Task WaitForCallEnd(CallSession session)
    {
        while (session.State == CallState.ConnectedToAgent)
        {
            await Task.Delay(1000);
        }
    }
}
