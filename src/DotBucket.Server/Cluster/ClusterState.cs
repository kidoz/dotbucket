// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using DotBucket.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Cluster;

public enum NodeHealthStatus
{
    Healthy,
    Suspect,
    Dead,
}

public record NodeInfo(string NodeId, string Address, bool IsSelf, bool IsLeader);

public record NodeHealth(NodeHealthStatus Status, DateTime LastSeen, int ConsecutiveFailures);

public class ClusterState
{
    private readonly ClusterOptions _options;
    private readonly ConcurrentDictionary<string, NodeHealth> _healthMap = new();

    public ClusterState(IOptions<ClusterOptions> options)
    {
        _options = options.Value;

        if (!_options.Enabled)
            return;

        // Validate: self node must be in the node list
        var selfEntry = _options.Nodes.FirstOrDefault(n => n.NodeId == _options.NodeId);
        if (selfEntry == null)
            throw new InvalidOperationException(
                $"Cluster node '{_options.NodeId}' not found in Nodes list."
            );

        // Validate: leader must be in the node list
        if (!_options.Nodes.Any(n => n.NodeId == _options.LeaderNodeId))
            throw new InvalidOperationException(
                $"Leader node '{_options.LeaderNodeId}' not found in Nodes list."
            );

        // Validate: unique node IDs
        var duplicates = _options
            .Nodes.GroupBy(n => n.NodeId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException(
                $"Duplicate node IDs in cluster config: {string.Join(", ", duplicates)}"
            );

        AllNodes = _options
            .Nodes.Select(n => new NodeInfo(
                n.NodeId,
                n.Address,
                n.NodeId == _options.NodeId,
                n.NodeId == _options.LeaderNodeId
            ))
            .ToList();

        // Initialize all nodes as Healthy
        foreach (var node in AllNodes)
        {
            _healthMap[node.NodeId] = new NodeHealth(NodeHealthStatus.Healthy, DateTime.UtcNow, 0);
        }
    }

    public bool IsDistributed => _options.Enabled;
    public string SelfNodeId => _options.NodeId;
    public string SelfAddress => _options.AdvertiseAddress;
    public bool IsLeader => _options.NodeId == _options.LeaderNodeId;
    public IReadOnlyList<NodeInfo> AllNodes { get; } = [];

    public NodeHealth GetNodeHealth(string nodeId)
    {
        return _healthMap.GetValueOrDefault(
            nodeId,
            new NodeHealth(NodeHealthStatus.Dead, DateTime.MinValue, 0)
        );
    }

    public void UpdateNodeHealth(string nodeId, NodeHealth health)
    {
        _healthMap[nodeId] = health;
    }

    public IReadOnlyList<NodeInfo> GetHealthyNodes()
    {
        return AllNodes
            .Where(n => GetNodeHealth(n.NodeId).Status != NodeHealthStatus.Dead)
            .ToList();
    }
}
