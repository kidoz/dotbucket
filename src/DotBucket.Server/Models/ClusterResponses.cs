// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Models;

public class ClusterStatusResponse
{
    public bool Enabled { get; set; }
    public string SelfNodeId { get; set; } = string.Empty;
    public string LeaderNodeId { get; set; } = string.Empty;
    public int ReplicationFactor { get; set; }
    public int WriteQuorum { get; set; }
    public int ReadQuorum { get; set; }
    public List<ClusterNodeStatusResponse> Nodes { get; set; } = new();
}

public class ClusterNodeStatusResponse
{
    public string NodeId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsSelf { get; set; }
    public bool IsLeader { get; set; }
    public string Status { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
}
