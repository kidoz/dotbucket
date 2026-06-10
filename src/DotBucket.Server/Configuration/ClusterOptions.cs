// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

public class ClusterOptions
{
    public const string SectionName = "Cluster";

    public bool Enabled { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string AdvertiseAddress { get; set; } = string.Empty;
    public int ReplicationFactor { get; set; } = 3;
    public int WriteQuorum { get; set; } = 2;
    public int ReadQuorum { get; set; } = 1;
    public string LeaderNodeId { get; set; } = string.Empty;
    public string ClusterToken { get; set; } = string.Empty;
    public int HealthCheckIntervalMs { get; set; } = 5000;
    public int HealthCheckTimeoutMs { get; set; } = 2000;
    public List<NodeEntry> Nodes { get; set; } = new();

    /// <summary>
    /// Optional path to a PEM bundle of CA certificate(s) to trust for inter-node
    /// HTTPS calls. When empty, the OS/default trust store is used.
    /// </summary>
    public string TrustedCaBundlePath { get; set; } = string.Empty;

    public class NodeEntry
    {
        public string NodeId { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
    }
}
