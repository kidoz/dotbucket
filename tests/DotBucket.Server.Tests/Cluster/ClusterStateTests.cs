// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DotBucket.Server.Tests.Cluster;

public class ClusterStateTests
{
    [Fact]
    public void UpdateNodeHealth_WhenLeaderDies_PromotesHealthyNode()
    {
        var options = Options.Create(
            new ClusterOptions
            {
                Enabled = true,
                NodeId = "node-2",
                AdvertiseAddress = "http://node-2:9000",
                LeaderNodeId = "node-1",
                Nodes =
                [
                    new ClusterOptions.NodeEntry { NodeId = "node-1", Address = "http://node-1:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-2", Address = "http://node-2:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-3", Address = "http://node-3:9000" },
                ],
            }
        );
        var logger = Substitute.For<ILogger<ClusterState>>();
        var state = new ClusterState(options, logger);

        state.LeaderNodeId.Should().Be("node-1");
        state.IsLeader.Should().BeFalse();

        state.UpdateNodeHealth(
            "node-1",
            new NodeHealth(NodeHealthStatus.Dead, DateTime.UtcNow, 3)
        );

        state.LeaderNodeId.Should().Be("node-2");
        state.IsLeader.Should().BeTrue();
    }

    [Fact]
    public void UpdateNodeHealth_MinorityPartition_DoesNotPromote()
    {
        // 5-node cluster: nodes 3,4,5 are dead (minority partition of 2 healthy)
        var options = Options.Create(
            new ClusterOptions
            {
                Enabled = true,
                NodeId = "node-2",
                AdvertiseAddress = "http://node-2:9000",
                LeaderNodeId = "node-1",
                Nodes =
                [
                    new ClusterOptions.NodeEntry { NodeId = "node-1", Address = "http://node-1:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-2", Address = "http://node-2:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-3", Address = "http://node-3:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-4", Address = "http://node-4:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-5", Address = "http://node-5:9000" },
                ],
            }
        );
        var logger = Substitute.For<ILogger<ClusterState>>();
        var state = new ClusterState(options, logger);

        // Mark nodes 3, 4, 5 as Dead
        state.UpdateNodeHealth("node-3", new NodeHealth(NodeHealthStatus.Dead, DateTime.UtcNow, 3));
        state.UpdateNodeHealth("node-4", new NodeHealth(NodeHealthStatus.Dead, DateTime.UtcNow, 3));
        state.UpdateNodeHealth("node-5", new NodeHealth(NodeHealthStatus.Dead, DateTime.UtcNow, 3));

        // Leader dies — only 2 healthy nodes remain (need 3 for quorum of 5)
        state.UpdateNodeHealth("node-1", new NodeHealth(NodeHealthStatus.Dead, DateTime.UtcNow, 3));

        // Promotion should be blocked due to insufficient quorum
        state.LeaderNodeId.Should().Be("node-1");
        state.IsLeader.Should().BeFalse();
    }

    [Fact]
    public void UpdateNodeHealth_MajorityPartition_PromotesAndIncrementsEpoch()
    {
        // 3-node cluster: majority partition (2 healthy)
        var options = Options.Create(
            new ClusterOptions
            {
                Enabled = true,
                NodeId = "node-2",
                AdvertiseAddress = "http://node-2:9000",
                LeaderNodeId = "node-1",
                Nodes =
                [
                    new ClusterOptions.NodeEntry { NodeId = "node-1", Address = "http://node-1:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-2", Address = "http://node-2:9000" },
                    new ClusterOptions.NodeEntry { NodeId = "node-3", Address = "http://node-3:9000" },
                ],
            }
        );
        var logger = Substitute.For<ILogger<ClusterState>>();
        var state = new ClusterState(options, logger);

        state.LeaderEpoch.Should().Be(0);

        // Leader dies with majority still healthy (2/3 = quorum met)
        state.UpdateNodeHealth("node-1", new NodeHealth(NodeHealthStatus.Dead, DateTime.UtcNow, 3));

        state.LeaderNodeId.Should().Be("node-2");
        state.IsLeader.Should().BeTrue();
        state.LeaderEpoch.Should().Be(1);
    }
}
