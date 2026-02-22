// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Cluster;

public class HealthMonitorService(
    ClusterState cluster,
    NodeClient nodeClient,
    IOptions<ClusterOptions> options,
    ILogger<HealthMonitorService> logger
) : BackgroundService
{
    private readonly ClusterOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cluster.IsDistributed)
        {
            logger.LogDebug("Cluster mode is disabled, health monitor is idle.");
            return;
        }

        logger.LogInformation(
            "Health monitor started for node {NodeId}, checking every {Interval}ms.",
            cluster.SelfNodeId,
            _options.HealthCheckIntervalMs
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_options.HealthCheckIntervalMs, stoppingToken);

            var peers = cluster.AllNodes.Where(n => !n.IsSelf).ToList();
            var tasks = peers.Select(peer => ProbeNodeAsync(peer, stoppingToken));
            await Task.WhenAll(tasks);
        }
    }

    private async Task ProbeNodeAsync(NodeInfo peer, CancellationToken ct)
    {
        var previousHealth = cluster.GetNodeHealth(peer.NodeId);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.HealthCheckTimeoutMs);

            var result = await nodeClient.HealthCheckAsync(peer.Address, cts.Token);

            if (result.Status == NodeHealthStatus.Healthy)
            {
                if (previousHealth.Status != NodeHealthStatus.Healthy)
                {
                    logger.LogWarning(
                        "Node {NodeId} recovered: {OldStatus} -> Healthy",
                        peer.NodeId,
                        previousHealth.Status
                    );
                }
                cluster.UpdateNodeHealth(
                    peer.NodeId,
                    new NodeHealth(NodeHealthStatus.Healthy, DateTime.UtcNow, 0)
                );
            }
            else
            {
                HandleFailure(peer, previousHealth);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout on health check
            HandleFailure(peer, previousHealth);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Health check failed for node {NodeId}", peer.NodeId);
            HandleFailure(peer, previousHealth);
        }
    }

    private void HandleFailure(NodeInfo peer, NodeHealth previousHealth)
    {
        var failures = previousHealth.ConsecutiveFailures + 1;
        var newStatus = failures >= 3 ? NodeHealthStatus.Dead : NodeHealthStatus.Suspect;

        if (newStatus != previousHealth.Status)
        {
            logger.LogWarning(
                "Node {NodeId} health changed: {OldStatus} -> {NewStatus} (failures: {Failures})",
                peer.NodeId,
                previousHealth.Status,
                newStatus,
                failures
            );
        }

        cluster.UpdateNodeHealth(
            peer.NodeId,
            new NodeHealth(newStatus, previousHealth.LastSeen, failures)
        );
    }
}
