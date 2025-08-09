using System.Collections.Concurrent;
using virtual_call_center.Models;

namespace virtual_call_center.Services;

/// <summary>
/// Manages dynamic agent registration and status based on active gRPC connections
/// </summary>
public class DynamicAgentManager : IDisposable
{
    private readonly ILogger<DynamicAgentManager> _logger;
    private readonly ConcurrentDictionary<int, DynamicAgent> _connectedAgents;
    private readonly Timer _heartbeatChecker;
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(30);
    private bool _disposed = false;

    public DynamicAgentManager(ILogger<DynamicAgentManager> logger)
    {
        _logger = logger;
        _connectedAgents = new ConcurrentDictionary<int, DynamicAgent>();
        
        _heartbeatChecker = new Timer(CheckHeartbeats, null, 
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Registers a new agent or updates existing agent information
    /// </summary>
    public bool RegisterAgent(int agentId, string name, int maxConcurrentCalls = 1)
    {
        if (agentId < 5000 || agentId > 5999)
        {
            _logger.LogWarning("Agent ID {AgentId} is outside valid range (5000-5999)", agentId);
            return false;
        }

        var agent = new DynamicAgent
        {
            AgentId = agentId,
            Name = name,
            IsOnline = true,
            LastHeartbeat = DateTime.UtcNow,
            MaxConcurrentCalls = maxConcurrentCalls,
            ConnectedAt = DateTime.UtcNow
        };

        _connectedAgents.AddOrUpdate(agentId, agent, (key, existing) =>
        {
            existing.Name = name;
            existing.IsOnline = true;
            existing.LastHeartbeat = DateTime.UtcNow;
            existing.MaxConcurrentCalls = maxConcurrentCalls;
            return existing;
        });

        _logger.LogInformation("Agent {AgentId} ({Name}) registered successfully", agentId, name);
        return true;
    }

    /// <summary>
    /// Updates agent heartbeat and availability status
    /// </summary>
    public void UpdateHeartbeat(int agentId, bool isAvailable)
    {
        if (_connectedAgents.TryGetValue(agentId, out var agent))
        {
            agent.LastHeartbeat = DateTime.UtcNow;
            agent.IsOnline = isAvailable;
        }
    }

    /// <summary>
    /// Removes an agent (called when gRPC connection closes)
    /// </summary>
    public void UnregisterAgent(int agentId)
    {
        if (_connectedAgents.TryRemove(agentId, out var agent))
        {
            _logger.LogInformation("Agent {AgentId} ({Name}) disconnected after {Duration}",
                agentId, agent.Name, DateTime.UtcNow - agent.ConnectedAt);
        }
    }

    /// <summary>
    /// Gets all available agents that can accept calls
    /// </summary>
    public List<DynamicAgent> GetAvailableAgents()
    {
        return _connectedAgents.Values
            .Where(a => a.IsOnline && a.CurrentCalls < a.MaxConcurrentCalls)
            .OrderBy(a => a.CurrentCalls)
            .ThenBy(a => a.ConnectedAt)
            .ToList();
    }

    /// <summary>
    /// Gets a specific agent by ID
    /// </summary>
    public DynamicAgent? GetAgent(int agentId)
    {
        _connectedAgents.TryGetValue(agentId, out var agent);
        return agent;
    }

    /// <summary>
    /// Updates agent call count
    /// </summary>
    public void UpdateAgentCallCount(int agentId, int currentCalls, string? currentCallId = null)
    {
        if (_connectedAgents.TryGetValue(agentId, out var agent))
        {
            agent.CurrentCalls = currentCalls;
            agent.CurrentCallId = currentCallId;
        }
    }

    /// <summary>
    /// Gets all connected agents (for monitoring/debugging)
    /// </summary>
    public List<DynamicAgent> GetAllAgents()
    {
        return _connectedAgents.Values.ToList();
    }

    /// <summary>
    /// Checks for agents that haven't sent heartbeats and marks them offline
    /// </summary>
    private void CheckHeartbeats(object? state)
    {
        if (_disposed) return;
        
        try
        {
            var cutoffTime = DateTime.UtcNow - _heartbeatTimeout;
            var staleAgents = _connectedAgents.Values
                .Where(a => a.LastHeartbeat < cutoffTime && a.IsOnline)
                .ToList();

            foreach (var agent in staleAgents)
            {
                agent.IsOnline = false;
                _logger.LogWarning("Agent {AgentId} ({Name}) marked offline due to missed heartbeat",
                    agent.AgentId, agent.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during heartbeat check");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _heartbeatChecker?.Dispose();
            _disposed = true;
        }
    }
}
