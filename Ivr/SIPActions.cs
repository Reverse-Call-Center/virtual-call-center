using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Media;
using SIPSorcery.Net;
using virtual_call_center.Models;
using virtual_call_center.Services;
using SIPSorceryMedia.Abstractions;
using ConfigManager = virtual_call_center.Services.ConfigurationManager;

namespace virtual_call_center.Ivr;

public class SIPActions
{
    private readonly ILogger<SIPActions> _logger;
    private readonly ConfigManager _configManager;
    private readonly CallRouter _callRouter;
    private readonly AudioService _audioService;
    private readonly AgentManager _agentManager;
    private readonly CallSessionManager _sessionManager;
    private readonly DTMFDetector _dtmfDetector;

    public SIPActions(ILogger<SIPActions> logger, ConfigManager configManager, 
        CallRouter callRouter, AudioService audioService, AgentManager agentManager,
        CallSessionManager sessionManager, DTMFDetector dtmfDetector)
    {
        _logger = logger;
        _configManager = configManager;
        _callRouter = callRouter;
        _audioService = audioService;
        _agentManager = agentManager;
        _sessionManager = sessionManager;
        _dtmfDetector = dtmfDetector;
        
        _logger.LogInformation("SIPActions initialized at {Timestamp}", DateTime.UtcNow);
    }
    
    /// <summary>
    /// Handles incoming SIP calls, sets up media session and starts call processing
    /// </summary>
    public async Task OnIncomingCall(SIPUserAgent userAgent, SIPRequest sipRequest)
    {
        var callerNumber = sipRequest.Header.From.FromURI.User;
        
        _logger.LogInformation("Incoming call from {CallerNumber}", callerNumber);

        if (IsCallerBlacklisted(callerNumber))
        {
            _logger.LogWarning("Blocking blacklisted caller {CallerNumber}", callerNumber);
            userAgent.Cancel();
            return;
        }

        if (!_sessionManager.CanAcceptNewCall())
        {
            _logger.LogWarning("Rejecting call from {CallerNumber} - at capacity", callerNumber);
            userAgent.Cancel();
            return;
        }

        var callId = _sessionManager.CreateCallSession(callerNumber);
        var session = _sessionManager.GetCallSession(callId);
        
        if (session == null)
        {
            _logger.LogError("Failed to create call session for {CallerNumber}", callerNumber);
            userAgent.Cancel();
            return;
        }

        session.UserAgent = userAgent;

        try
        {
            var userAgentServer = userAgent.AcceptCall(sipRequest);
            if (userAgentServer == null)
            {
                _logger.LogError("Failed to accept call for {CallerNumber}", callerNumber);
                _sessionManager.RemoveCallSession(callId);
                return;
            }
            
            var mediaSession = CreateMediaSession(session);
            if (mediaSession == null)
            {
                _logger.LogError("Failed to create media session for {CallerNumber}", callerNumber);
                userAgent.Cancel();
                _sessionManager.RemoveCallSession(callId);
                return;
            }
            
            await userAgent.Answer(userAgentServer, mediaSession);
            session.MediaSession = mediaSession;
            session.State = CallState.Connected;

            _logger.LogInformation("Call {CallId} answered successfully", callId);

            if (_configManager.SipConfig.EnableRecording)
            {
                await PlayDisclaimerAndStartRecording(session);
            }

            await ProcessCall(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling incoming call {CallId}", callId);
            _sessionManager.RemoveCallSession(callId);
            userAgent?.Hangup();
        }
    }

    /// <summary>
    /// Creates and configures the media session for the call
    /// </summary>
    private VoIPMediaSession? CreateMediaSession(CallSession session)
    {
        try
        {
            var mediaEndPoints = new MediaEndPoints();
            var mediaSession = new VoIPMediaSession(mediaEndPoints);
            
            if (mediaSession.AudioLocalTrack != null)
            {
                mediaSession.AudioLocalTrack.Capabilities.Clear();
                
                mediaSession.AudioLocalTrack.Capabilities.Add(
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU));
                
                mediaSession.AudioLocalTrack.Capabilities.Add(
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMA));
                
                mediaSession.AudioLocalTrack.Capabilities.Add(
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.G722));
                
                mediaSession.AudioLocalTrack.Capabilities.Add(
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.G729));
            }

            if (mediaSession.AudioExtrasSource != null)
            {
                mediaSession.AudioExtrasSource.OnAudioSourceEncodedSample += (uint durationRtpUnits, byte[] sample) => 
                    OnAudioReceived(session, sample);
            }
            
            return mediaSession;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating media session for call {CallId}", session.CallId);
            return null;
        }
    }

    private async Task PlayDisclaimerAndStartRecording(CallSession session)
    {
        if (!string.IsNullOrEmpty(_configManager.SipConfig.RecordDisclaimerFile))
        {
            await _audioService.PlayRecording(session, _configManager.SipConfig.RecordDisclaimerFile);
        }
        
        await _audioService.StartRecording(session);
    }

    private async Task ProcessCall(CallSession session)
    {
        while (session.State != CallState.Ended)
        {
            try
            {
                var nextAction = await _callRouter.RouteCall(session);
                
                if (nextAction == 0)
                {
                    break;
                }
                
                session.VisitedActions.Add(session.CurrentAction);
                session.CurrentAction = nextAction;
                session.LastActivity = DateTime.UtcNow;
                
                _logger.LogInformation("Call {CallId} routed to action {Action}", session.CallId, nextAction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing call {CallId}", session.CallId);
                break;
            }
        }

        await EndCall(session);
    }

    private void OnAudioReceived(CallSession session, byte[] audioSample)
    {
        try
        {
            var pcmData = _audioService.ConvertFromMuLaw(audioSample);
            _audioService.ProcessIncomingAudio(session, pcmData);

            if (session.AssignedAgentId.HasValue)
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _ = _agentManager.SendAudioToAgent(session.CallId, session.AssignedAgentId.Value, 
                    pcmData, timestamp, 0);
            }

            if (session.State == CallState.InIVR)
            {
                var dtmf = DetectDTMF(audioSample);
                if (dtmf.HasValue)
                {
                    _logger.LogInformation("DTMF {Key} detected for call {CallId}", dtmf.Value, session.CallId);
                    
                    if (session.DtmfTaskCompletionSource != null && !session.DtmfTaskCompletionSource.Task.IsCompleted)
                    {
                        session.DtmfTaskCompletionSource.SetResult(dtmf.Value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio for call {CallId}", session.CallId);
        }
    }

    private int? DetectDTMF(byte[] audioSample)
    {
        return _dtmfDetector.DetectDTMF(audioSample);
    }

    /// <summary>
    /// Ends the call and cleans up resources
    /// </summary>
    private async Task EndCall(CallSession session)
    {
        _logger.LogInformation("Ending call {CallId}", session.CallId);
        
        if (session.RecordingEnabled)
        {
            await _audioService.StopRecording(session);
        }

        if (session.AssignedAgentId.HasValue)
        {
            await _agentManager.EndAgentCall(session.CallId, session.AssignedAgentId.Value, "Call ended");
        }

        session.UserAgent?.Hangup();
        _sessionManager.RemoveCallSession(session.CallId);
    }
    
    private static bool IsCallerBlacklisted(string user)
    {
        return false;
    }
}