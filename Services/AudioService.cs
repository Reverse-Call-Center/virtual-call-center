using NAudio.Wave;
using virtual_call_center.Models;
using SIPSorcery.Media;
using System.Collections.Concurrent;

namespace virtual_call_center.Services;

public class AudioService
{
    private readonly ILogger<AudioService> _logger;
    private readonly ConfigurationManager _configManager;
    private readonly ConcurrentDictionary<string, WaveFileReader> _audioCache;
    private readonly string _recordingsPath;

    public AudioService(ILogger<AudioService> logger, ConfigurationManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
        _audioCache = new ConcurrentDictionary<string, WaveFileReader>();
        _recordingsPath = "recordings";
        
        if (!Directory.Exists(_recordingsPath))
            Directory.CreateDirectory(_recordingsPath);
    }

    /// <summary>
    /// Plays an audio recording to the specified call session
    /// </summary>
    /// <param name="session">The call session to play audio to</param>
    /// <param name="filename">The recording filename to play</param>
    /// <param name="loop">Whether to loop the recording continuously</param>
    public async Task PlayRecording(CallSession session, string filename, bool loop = false)
    {
        if (session.MediaSession == null)
        {
            _logger.LogWarning("Cannot play recording {Filename} - no media session for call {CallId}", 
                filename, session.CallId);
            return;
        }

        var filePath = Path.Combine(_recordingsPath, filename);
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Recording file not found: {FilePath}", filePath);
            return;
        }

        _logger.LogInformation("Playing recording {Filename} for call {CallId}", filename, session.CallId);

        try
        {
            using var audioFile = new WaveFileReader(filePath);
            var pcmData = new byte[audioFile.Length];
            await audioFile.ReadAsync(pcmData, 0, pcmData.Length);

            var resampledData = ResampleTo8kHz(pcmData, audioFile.WaveFormat);
            
            do
            {
                await SendAudioToCall(session, resampledData);
            } while (loop && session.State != CallState.Ended);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error playing recording {Filename} for call {CallId}", filename, session.CallId);
        }
    }

    /// <summary>
    /// Starts recording audio for the specified call session
    /// </summary>
    public Task StartRecording(CallSession session)
    {
        if (session.RecordingEnabled)
            return Task.CompletedTask;

        session.RecordingEnabled = true;
        session.State = CallState.Recording;
        
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        session.RecordingPath = Path.Combine(_recordingsPath, $"call_{session.CallId}_{timestamp}.wav");
        
        _logger.LogInformation("Started recording for call {CallId} to {RecordingPath}", 
            session.CallId, session.RecordingPath);
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops recording audio for the specified call session
    /// </summary>
    public Task StopRecording(CallSession session)
    {
        if (!session.RecordingEnabled)
            return Task.CompletedTask;

        session.RecordingEnabled = false;
        _logger.LogInformation("Stopped recording for call {CallId}", session.CallId);
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Processes incoming audio data and routes it to recording/agent as needed
    /// </summary>
    public void ProcessIncomingAudio(CallSession session, byte[] audioData)
    {
        if (session.RecordingEnabled && !string.IsNullOrEmpty(session.RecordingPath))
        {
            WriteAudioToFile(session.RecordingPath, audioData);
        }

        if (session.AssignedAgentId.HasValue)
        {
            session.AudioBuffer.Enqueue(audioData);
        }
    }

    public byte[] GetAudioForAgent(CallSession session)
    {
        if (session.AudioBuffer.TryDequeue(out var audioData))
        {
            return audioData;
        }
        return Array.Empty<byte>();
    }

    private byte[] ResampleTo8kHz(byte[] audioData, WaveFormat sourceFormat)
    {
        if (sourceFormat.SampleRate == 8000 && sourceFormat.Channels == 1 && sourceFormat.BitsPerSample == 16)
            return audioData;

        using var sourceStream = new MemoryStream(audioData);
        using var sourceProvider = new RawSourceWaveStream(sourceStream, sourceFormat);
        
        var targetFormat = new WaveFormat(8000, 16, 1);
        using var resampler = new MediaFoundationResampler(sourceProvider, targetFormat);
        
        using var outputStream = new MemoryStream();
        var buffer = new byte[8192];
        int bytesRead;
        
        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            outputStream.Write(buffer, 0, bytesRead);
        }
        
        return outputStream.ToArray();
    }

    private async Task SendAudioToCall(CallSession session, byte[] audioData)
    {
        if (session.MediaSession == null)
            return;

        // Convert PCM to μ-law for SIP transmission
        var muLawData = ConvertToMuLaw(audioData);
        const int frameSize = 160; // 20ms at 8kHz μ-law (160 bytes = 20ms)
        
        for (int offset = 0; offset < muLawData.Length; offset += frameSize)
        {
            var frameLength = Math.Min(frameSize, muLawData.Length - offset);
            var frame = new byte[frameLength];
            Array.Copy(muLawData, offset, frame, 0, frameLength);
            
            // Send μ-law encoded audio frame
            session.MediaSession.SendAudio(20, frame);
            await Task.Delay(20); // 20ms frame timing
        }
    }

    private void WriteAudioToFile(string filePath, byte[] audioData)
    {
        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write);
            fileStream.Write(audioData, 0, audioData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audio to recording file {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Converts PCM audio data to μ-law format for SIP transmission
    /// </summary>
    public byte[] ConvertToMuLaw(byte[] pcmData)
    {
        var muLawData = new byte[pcmData.Length / 2];
        for (int i = 0; i < muLawData.Length; i++)
        {
            var sample = BitConverter.ToInt16(pcmData, i * 2);
            muLawData[i] = MuLawEncoder.LinearToMuLawSample(sample);
        }
        return muLawData;
    }

    /// <summary>
    /// Converts μ-law audio data to PCM format for processing
    /// </summary>
    public byte[] ConvertFromMuLaw(byte[] muLawData)
    {
        var pcmData = new byte[muLawData.Length * 2];
        for (int i = 0; i < muLawData.Length; i++)
        {
            var sample = MuLawDecoder.MuLawToLinearSample(muLawData[i]);
            var bytes = BitConverter.GetBytes(sample);
            pcmData[i * 2] = bytes[0];
            pcmData[i * 2 + 1] = bytes[1];
        }
        return pcmData;
    }
}
