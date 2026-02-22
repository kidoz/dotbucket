// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;

namespace DotBucket.Server.Cluster;

/// <summary>
/// Rendezvous (Highest Random Weight) hashing for consistent data placement.
/// </summary>
public static class RendezvousHashRing
{
    public static IReadOnlyList<NodeInfo> GetPreferenceList(
        string objectKey,
        IReadOnlyList<NodeInfo> nodes,
        int replicationFactor
    )
    {
        if (nodes.Count == 0)
            return [];

        var count = Math.Min(replicationFactor, nodes.Count);

        return nodes
            .Select(node => (Node: node, Weight: ComputeWeight(node.NodeId, objectKey)))
            .OrderByDescending(x => x.Weight)
            .Take(count)
            .Select(x => x.Node)
            .ToList();
    }

    private static ulong ComputeWeight(string nodeId, string objectKey)
    {
        var input = Encoding.UTF8.GetBytes($"{nodeId}:{objectKey}");
        var hash = SHA256.HashData(input);
        return BitConverter.ToUInt64(hash, 0);
    }
}
