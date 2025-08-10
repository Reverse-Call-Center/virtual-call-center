# gRPC Agent Integration Guide

This document explains how to integrate agents into the Virtual Call Center using gRPC.

## Overview

The Virtual Call Center uses gRPC for real-time communication with external agents. Each agent connects via gRPC and can handle one call at a time. Agents are automatically discovered when they connect and removed when they disconnect.

## Agent Connection Flow

1. **Connect to gRPC Server**: Agents connect to the call center's gRPC server
2. **Register Stream**: Establish a bidirectional stream for real-time communication
3. **Automatic Registration**: Agent is automatically registered when stream is established
4. **Call Assignment**: System assigns calls to available agents
5. **Real-time Audio**: Bidirectional audio streaming during calls
6. **Cleanup**: Agent is automatically removed when connection closes

## gRPC Service Definition

```protobuf
service AgentService {
  rpc ConnectAgent(stream AgentMessage) returns (stream CallCenterMessage);
}

message AgentMessage {
  oneof message_type {
    AgentRegistration registration = 1;
    AudioData audio = 2;
    CallResponse response = 3;
    AgentStatus status = 4;
  }
}

message CallCenterMessage {
  oneof message_type {
    CallAssignment assignment = 1;
    AudioData audio = 2;
    CallEnd end = 3;
    SystemMessage system = 4;
  }
}
```

## Agent Implementation

### 1. Initial Connection

```csharp
var channel = GrpcChannel.ForAddress("http://localhost:5432");
var client = new AgentService.AgentServiceClient(channel);

using var call = client.ConnectAgent();
```

### 2. Agent Registration

Send registration message immediately after connecting:

```csharp
await call.RequestStream.WriteAsync(new AgentMessage {
    Registration = new AgentRegistration {
        AgentId = uniqueAgentId,  // System will assign if not provided
        DisplayName = "Agent Smith",
        Capabilities = { "voice", "chat" }
    }
});
```

### 3. Handle Incoming Messages

```csharp
await foreach (var message in call.ResponseStream.ReadAllAsync())
{
    switch (message.MessageTypeCase)
    {
        case CallCenterMessage.MessageTypeOneofCase.Assignment:
            HandleCallAssignment(message.Assignment);
            break;
        case CallCenterMessage.MessageTypeOneofCase.Audio:
            PlayAudioToAgent(message.Audio);
            break;
        case CallCenterMessage.MessageTypeOneofCase.End:
            HandleCallEnd(message.End);
            break;
    }
}
```

### 4. Send Audio Data

```csharp
await call.RequestStream.WriteAsync(new AgentMessage {
    Audio = new AudioData {
        CallId = currentCallId,
        Data = ByteString.CopyFrom(audioBytes),
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    }
});
```

## Dynamic Agent Management

Agents are managed dynamically:

- **Online Detection**: Agent is online when gRPC connection is active
- **Automatic Registration**: No manual configuration needed
- **Load Balancing**: Calls distributed to available agents
- **Fault Tolerance**: Disconnected agents automatically removed

## Call Flow

1. **Call Assignment**: System sends `CallAssignment` message to available agent
2. **Audio Stream**: Bidirectional audio streaming begins
3. **Call Control**: Agent can accept, reject, hold, or transfer calls
4. **Call End**: System sends `CallEnd` message when call terminates

## Agent States

- **Available**: Ready to receive calls (no active calls)
- **Busy**: Currently handling a call
- **Offline**: Not connected or connection lost

## Error Handling

- **Connection Loss**: Agent automatically marked offline
- **Call Failure**: Call reassigned to another agent if available
- **Audio Issues**: System logs errors and continues

## Security Considerations

- Use TLS for production deployments
- Implement authentication tokens in `AgentRegistration`
- Validate agent permissions before call assignment

## Example Agent Implementation

```csharp
public class CallCenterAgent
{
    private readonly AgentService.AgentServiceClient _client;
    private AsyncDuplexStreamingCall<AgentMessage, CallCenterMessage> _stream;
    
    public async Task ConnectAsync()
    {
        _stream = _client.ConnectAgent();
        
        // Register agent
        await _stream.RequestStream.WriteAsync(new AgentMessage {
            Registration = new AgentRegistration {
                DisplayName = "My Agent"
            }
        });
        
        // Handle incoming messages
        _ = Task.Run(HandleMessages);
    }
    
    private async Task HandleMessages()
    {
        await foreach (var message in _stream.ResponseStream.ReadAllAsync())
        {
            // Process messages...
        }
    }
}
```

## Configuration

No static agent configuration is required. Agents self-register when connecting and are automatically removed when disconnecting.

## Monitoring

- Agent connections logged in real-time
- Call assignments and completions tracked
- Audio quality metrics available
- Connection health monitored

For more details, see the `AgentGrpcService.cs` and `DynamicAgentManager.cs` files in the source code.
