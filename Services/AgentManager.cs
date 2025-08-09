using Grpc.Core;
using virtual_call_center.Models;
using virtual_call_center.Protos;
using System.Collections.Concurrent;

namespace virtual_call_center.Services;

public class AgentManager
{
    private readonly ILogger<AgentManager> _logger;
    private readonly ConfigurationManager _configManager;
    private readonly AudioService _audioService;
    private readonly CallSessionManager _sessionManager;
    private readonly ConcurrentDictionary<int, IServerStreamWriter<AudioFrame>> _agentStreams;

    public AgentManager(ILogger<AgentManager> logger, ConfigurationManager configManager, 
        AudioService audioService, CallSessionManager sessionManager)
    {
        _logger = logger;
        _configManager = configManager;
        _audioService = audioService;
        _sessionManager = sessionManager;
        _agentStreams = new ConcurrentDictionary<int, IServerStreamWriter<AudioFrame>>();
    }

    /// <summary>
    /// Assigns a call to an available agent
    /// </summary>
    public Task<bool> AssignCallToAgent(CallSession session, int agentId)
    {
        if (!_agentStreams.ContainsKey(agentId))
        {
            _logger.LogWarning("Agent {AgentId} not connected for call assignment", agentId);
            return Task.FromResult(false);
        }

        _sessionManager.AssignCallToAgent(session, agentId);
        session.AssignedAgentId = agentId;

        _logger.LogInformation("Assigned call {CallId} to agent {AgentId}", session.CallId, agentId);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Ends a call assignment with an agent
    /// </summary>
    public Task EndAgentCall(string callId, int agentId, string reason)
    {
        _sessionManager.EndCall(callId);
        _logger.LogInformation("Ended call {CallId} with agent {AgentId}: {Reason}", callId, agentId, reason);
        return Task.CompletedTask;
    }

    public void RegisterAgentStream(int agentId, IServerStreamWriter<AudioFrame> stream)
    {
        _agentStreams.TryAdd(agentId, stream);
        _logger.LogInformation("Registered audio stream for agent {AgentId}", agentId);
    }

    public void UnregisterAgentStream(int agentId)
    {
        _agentStreams.TryRemove(agentId, out _);
        _logger.LogInformation("Unregistered audio stream for agent {AgentId}", agentId);
    }

    /// <summary>
    /// Sends audio data to the specified agent via gRPC stream
    /// </summary>
    public async Task SendAudioToAgent(string callId, int agentId, byte[] pcmData, long timestamp, int sequenceNumber)
    {
        if (!_agentStreams.TryGetValue(agentId, out var stream))
        {
            _logger.LogWarning("No stream found for agent {AgentId}", agentId);
            return;
        }

        try
        {
            var audioFrame = new AudioFrame
            {
                CallId = callId,
                AgentId = agentId,
                PcmData = Google.Protobuf.ByteString.CopyFrom(pcmData),
                Timestamp = timestamp,
                SequenceNumber = sequenceNumber
            };

            await stream.WriteAsync(audioFrame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send audio to agent {AgentId}", agentId);
        }
    }

    /// <summary>
    /// Processes audio received from an agent and forwards to the call
    /// </summary>
    public Task ProcessAgentAudio(AudioFrame audioFrame)
    {
        var session = _sessionManager.GetCallSession(audioFrame.CallId);
        if (session == null)
        {
            _logger.LogWarning("No active call found for audio frame: {CallId}", audioFrame.CallId);
            return Task.CompletedTask;
        }

        if (session.MediaSession == null)
        {
            _logger.LogWarning("No media session for call {CallId}", audioFrame.CallId);
            return Task.CompletedTask;
        }

        try
        {
            var pcmData = audioFrame.PcmData.ToByteArray();
            var muLawData = _audioService.ConvertToMuLaw(pcmData);
            session.MediaSession.SendAudio(20, muLawData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process agent audio for call {CallId}", audioFrame.CallId);
        }
        
        return Task.CompletedTask;
    }

    public CallSession? GetCallSession(string callId)
    {
        return _sessionManager.GetCallSession(callId);
    }

    public List<CallAssignment> GetPendingCallsForAgent(int agentId)
    {
        return _sessionManager.GetAllCalls()
            .Where(c => c.State == CallState.InQueue && !c.AssignedAgentId.HasValue)
            .Take(5)
            .Select(c => new CallAssignment
            {
                CallId = c.CallId,
                CallerNumber = c.CallerNumber,
                QueueTime = c.QueuedTime?.Ticks ?? 0
            })
            .ToList();
    }
}
