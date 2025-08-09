using Grpc.Core;
using virtual_call_center.Protos;
using virtual_call_center.Services;

namespace virtual_call_center.Services;

public class AgentService : Protos.AgentService.AgentServiceBase
{
    private readonly ILogger<AgentService> _logger;
    private readonly ConfigurationManager _configManager;
    private readonly AgentManager _agentManager;
    private readonly AudioService _audioService;
    private readonly CallSessionManager _sessionManager;
    private readonly DynamicAgentManager _dynamicAgentManager;

    public AgentService(ILogger<AgentService> logger, ConfigurationManager configManager, 
        AgentManager agentManager, AudioService audioService, CallSessionManager sessionManager, DynamicAgentManager dynamicAgentManager)
    {
        _logger = logger;
        _configManager = configManager;
        _agentManager = agentManager;
        _audioService = audioService;
        _sessionManager = sessionManager;
        _dynamicAgentManager = dynamicAgentManager;
    }

    /// <summary>
    /// Updates agent registration and heartbeat status
    /// </summary>
    public override Task<AgentResponse> RegisterAgent(AgentRegistration request, ServerCallContext context)
    {
        _logger.LogInformation("Agent {AgentId} ({AgentName}) registering with max {MaxCalls} concurrent calls", 
            request.AgentId, request.AgentName, request.MaxConcurrentCalls);

        var success = _dynamicAgentManager.RegisterAgent(request.AgentId, request.AgentName, request.MaxConcurrentCalls);

        return Task.FromResult(new AgentResponse
        {
            Success = success,
            Message = success ? "Agent registered successfully" : "Failed to register agent"
        });
    }

    public override Task<HeartbeatResponse> SendHeartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        _dynamicAgentManager.UpdateHeartbeat(request.AgentId, request.IsAvailable);

        var pendingCalls = _agentManager.GetPendingCallsForAgent(request.AgentId);

        return Task.FromResult(new HeartbeatResponse
        {
            Success = true,
            PendingCalls = { pendingCalls }
        });
    }

    public override async Task<AudioResponse> SendAudio(AudioFrame request, ServerCallContext context)
    {
        await _agentManager.ProcessAgentAudio(request);

        return new AudioResponse
        {
            Success = true
        };
    }

    public override async Task ReceiveAudio(ReceiveAudioRequest request, IServerStreamWriter<AudioFrame> responseStream, ServerCallContext context)
    {
        _logger.LogInformation("Agent {AgentId} started audio stream for call {CallId}", 
            request.AgentId, request.CallId);

        _agentManager.RegisterAgentStream(request.AgentId, responseStream);

        try
        {
            var session = _sessionManager.GetCallSession(request.CallId);
            if (session == null)
            {
                _logger.LogWarning("No call session found for call {CallId}", request.CallId);
                return;
            }

            int sequenceNumber = 0;
            while (!context.CancellationToken.IsCancellationRequested && 
                   session.State == Models.CallState.ConnectedToAgent)
            {
                var audioData = _audioService.GetAudioForAgent(session);
                
                if (audioData.Length > 0)
                {
                    var audioFrame = new AudioFrame
                    {
                        CallId = request.CallId,
                        AgentId = request.AgentId,
                        PcmData = Google.Protobuf.ByteString.CopyFrom(audioData),
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        SequenceNumber = sequenceNumber++
                    };

                    await responseStream.WriteAsync(audioFrame);
                }

                await Task.Delay(20, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent {AgentId} audio stream cancelled for call {CallId}", 
                request.AgentId, request.CallId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in agent {AgentId} audio stream for call {CallId}", 
                request.AgentId, request.CallId);
        }
        finally
        {
            _agentManager.UnregisterAgentStream(request.AgentId);
        }
    }

    public override Task<AcceptCallResponse> AcceptCall(AcceptCallRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Agent {AgentId} accepting call {CallId}", request.AgentId, request.CallId);

        var session = _sessionManager.GetCallSession(request.CallId);
        if (session == null)
        {
            return Task.FromResult(new AcceptCallResponse
            {
                Success = false,
                Message = "Call not found"
            });
        }

        session.AssignedAgentId = request.AgentId;
        session.State = Models.CallState.ConnectedToAgent;

        return Task.FromResult(new AcceptCallResponse
        {
            Success = true,
            Message = "Call accepted"
        });
    }

    public override async Task<EndCallResponse> EndCall(EndCallRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Agent {AgentId} ending call {CallId}: {Reason}", 
            request.AgentId, request.CallId, request.Reason);

        await _agentManager.EndAgentCall(request.CallId, request.AgentId, request.Reason);

        return new EndCallResponse
        {
            Success = true
        };
    }
}
