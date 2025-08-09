# gRPC Agent Integration Guide

This document explains how to integrate Discord bot agents (or any client) with the Virtual Call Center using gRPC.

## Overview

Agents connect to the call center via gRPC to handle incoming calls. Each agent can handle one call at a time and communicates using PCM audio frames at 20ms intervals.

## Agent ID Range

- **Agent IDs**: 5000-5999
- Each agent must use a unique ID within this range
- The system will automatically route calls to available agents

## gRPC Service Definition

The agent service is defined in `Protos/agent.proto` and provides the following methods:

### 1. RegisterAgent
Register your agent with the call center.

```protobuf
rpc RegisterAgent(AgentRegistration) returns (AgentResponse);
```

**Request:**
```protobuf
message AgentRegistration {
  int32 agent_id = 1;           // Unique ID (5000-5999)
  string agent_name = 2;        // Display name for your agent
  int32 max_concurrent_calls = 3; // Always set to 1
}
```

### 2. SendHeartbeat
Maintain connection and receive pending call assignments.

```protobuf
rpc SendHeartbeat(HeartbeatRequest) returns (HeartbeatResponse);
```

**Request:**
```protobuf
message HeartbeatRequest {
  int32 agent_id = 1;      // Your agent ID
  bool is_available = 2;   // Whether you can accept calls
}
```

**Response:**
```protobuf
message HeartbeatResponse {
  bool success = 1;
  repeated CallAssignment pending_calls = 2; // Calls waiting for you
}
```

### 3. Audio Streaming (Bidirectional)

#### Receive Audio from Calls
```protobuf
rpc ReceiveAudio(ReceiveAudioRequest) returns (stream AudioFrame);
```

#### Send Audio to Calls
```protobuf
rpc SendAudio(AudioFrame) returns (AudioResponse);
```

**Audio Frame Format:**
```protobuf
message AudioFrame {
  string call_id = 1;        // Call identifier
  int32 agent_id = 2;        // Your agent ID
  bytes pcm_data = 3;        // PCM audio data (320 bytes = 20ms)
  int64 timestamp = 4;       // Unix timestamp in milliseconds
  int32 sequence_number = 5; // Frame sequence number
}
```

### 4. Call Management

#### Accept a Call
```protobuf
rpc AcceptCall(AcceptCallRequest) returns (AcceptCallResponse);
```

#### End a Call
```protobuf
rpc EndCall(EndCallRequest) returns (EndCallResponse);
```

## Integration Steps

### 1. Connect to gRPC Server

Connect to the call center gRPC server (default: `localhost:5000`):

```csharp
// C# Example
var channel = GrpcChannel.ForAddress("https://localhost:5001");
var client = new AgentService.AgentServiceClient(channel);
```

### 2. Register Your Agent

```csharp
var registration = new AgentRegistration
{
    AgentId = 5001,  // Choose unique ID between 5000-5999
    AgentName = "Discord Bot Agent",
    MaxConcurrentCalls = 1  // Always 1
};

var response = await client.RegisterAgentAsync(registration);
```

### 3. Start Heartbeat Loop

Send heartbeats every 5-10 seconds to maintain connection:

```csharp
while (isRunning)
{
    var heartbeat = new HeartbeatRequest
    {
        AgentId = 5001,
        IsAvailable = true  // Set false when busy
    };
    
    var response = await client.SendHeartbeatAsync(heartbeat);
    
    // Check for pending calls
    foreach (var call in response.PendingCalls)
    {
        await HandleIncomingCall(call);
    }
    
    await Task.Delay(5000); // 5 second interval
}
```

### 4. Handle Incoming Calls

When you receive a call assignment:

```csharp
private async Task HandleIncomingCall(CallAssignment call)
{
    // Accept the call
    var acceptRequest = new AcceptCallRequest
    {
        AgentId = 5001,
        CallId = call.CallId
    };
    
    await client.AcceptCallAsync(acceptRequest);
    
    // Start audio streaming
    await StartAudioStreaming(call.CallId);
}
```

### 5. Audio Streaming

#### Receiving Audio from Caller

```csharp
var audioRequest = new ReceiveAudioRequest
{
    AgentId = 5001,
    CallId = callId
};

using var audioStream = client.ReceiveAudio(audioRequest);

await foreach (var frame in audioStream.ResponseStream.ReadAllAsync())
{
    // Process incoming PCM audio (320 bytes = 20ms at 8kHz, 16-bit mono)
    ProcessIncomingAudio(frame.PcmData.ToByteArray());
}
```

#### Sending Audio to Caller

```csharp
private async Task SendAudioFrame(string callId, byte[] pcmData, int sequenceNumber)
{
    var audioFrame = new AudioFrame
    {
        CallId = callId,
        AgentId = 5001,
        PcmData = Google.Protobuf.ByteString.CopyFrom(pcmData),
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SequenceNumber = sequenceNumber
    };
    
    await client.SendAudioAsync(audioFrame);
}
```

### 6. End Call

When the call is finished:

```csharp
var endRequest = new EndCallRequest
{
    AgentId = 5001,
    CallId = callId,
    Reason = "Call completed"
};

await client.EndCallAsync(endRequest);
```

## Audio Format Requirements

- **Sample Rate**: 8000 Hz
- **Bit Depth**: 16-bit
- **Channels**: Mono (1 channel)
- **Frame Size**: 320 bytes (20ms of audio)
- **Format**: PCM (signed 16-bit little-endian)

## Discord Bot Integration

For Discord bots, you'll need to:

1. Convert Discord's Opus audio to PCM
2. Resample to 8kHz if needed
3. Convert stereo to mono if needed
4. Send 20ms frames (320 bytes each)

## Error Handling

- **Connection Loss**: Implement reconnection logic with exponential backoff
- **Call Timeout**: End calls if no audio received for 30+ seconds
- **Audio Buffer**: Implement buffering for smooth audio playback
- **Heartbeat Failure**: Reconnect if heartbeats fail

## Example Implementation

See the `examples/` directory for complete Discord bot integration examples in various languages.

## Troubleshooting

### Common Issues

1. **Agent Not Receiving Calls**
   - Ensure heartbeats are being sent regularly
   - Check that `is_available = true` in heartbeat
   - Verify agent ID is in range 5000-5999

2. **Audio Quality Issues**
   - Verify PCM format (8kHz, 16-bit, mono)
   - Check frame timing (exactly 20ms = 320 bytes)
   - Implement proper buffering

3. **Connection Problems**
   - Check gRPC server address and port
   - Verify SSL/TLS configuration
   - Implement proper error handling and reconnection

### Logs

Enable detailed logging in your agent to debug issues:
- Connection events
- Audio frame statistics
- Call state changes
- Error conditions
