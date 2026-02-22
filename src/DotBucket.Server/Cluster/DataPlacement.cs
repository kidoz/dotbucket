// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Cluster;

public class DataPlacement(ClusterState cluster, IOptions<ClusterOptions> options)
{
    private readonly ClusterOptions _options = options.Value;

    public IReadOnlyList<NodeInfo> GetPreferenceList(string bucketName, string objectKey)
    {
        var compositeKey = $"{bucketName}/{objectKey}";
        var healthyNodes = cluster.GetHealthyNodes();
        return RendezvousHashRing.GetPreferenceList(
            compositeKey,
            healthyNodes,
            _options.ReplicationFactor
        );
    }

    public bool IsPrimaryOwner(string bucketName, string objectKey)
    {
        var preferenceList = GetPreferenceList(bucketName, objectKey);
        return preferenceList.Count > 0 && preferenceList[0].IsSelf;
    }

    public bool IsOwner(string bucketName, string objectKey)
    {
        var preferenceList = GetPreferenceList(bucketName, objectKey);
        return preferenceList.Any(n => n.IsSelf);
    }

    public IReadOnlyList<NodeInfo> GetReplicaTargets(string bucketName, string objectKey)
    {
        var preferenceList = GetPreferenceList(bucketName, objectKey);
        return preferenceList.Where(n => !n.IsSelf).ToList();
    }
}
