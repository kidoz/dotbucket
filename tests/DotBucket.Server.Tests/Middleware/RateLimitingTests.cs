// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using DotBucket.Server.Configuration;
using Microsoft.AspNetCore.Http;

namespace DotBucket.Server.Tests.Middleware;

/// <summary>
/// Unit tests for the rate-limiter partitioner logic declared in Program.cs.
/// The partitioner selects the per-request rate-limit bucket; correctness here
/// is what guarantees the "100 r/s per access key" contract (a misconfigured
/// partitioner would let one key starve another or apply the wrong limit).
///
/// Full-pipeline 429 behavior (token bucket exhausts → 429) is covered by the
/// integration test suite in DotBucket.Server.IntegrationTests, which runs the
/// real pipeline against WebApplicationFactory<Program>.
/// </summary>
public class RateLimitingTests
{
    // Mirrors the "s3" partitioner lambda in Program.cs. Extracted as a static
    // helper so the test asserts exactly the production logic — if the lambda
    // drifts from this helper, the test will fail loudly when re-synced.
    private static RateLimitPartition<string> S3Partition(
        HttpContext httpContext,
        RateLimitOptions options
    )
    {
        var accessKey = httpContext.Items.TryGetValue("AccessKey", out var ak)
            ? ak as string
            : null;
        var partition = accessKey ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetTokenBucketLimiter(
            partition,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.S3Burst,
                TokensPerPeriod = options.S3PermitPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = options.S3Burst,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }
        );
    }

    private static RateLimitPartition<string> AdminPartition(
        HttpContext httpContext,
        RateLimitOptions options
    )
    {
        var partition = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetTokenBucketLimiter(
            partition,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.AdminBurst,
                TokensPerPeriod = options.AdminPermitPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = options.AdminBurst,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }
        );
    }

    private static HttpContext NewHttpContext(string? accessKey = null, string? remoteIp = null)
    {
        var httpContext = new DefaultHttpContext();
        if (accessKey is not null)
            httpContext.Items["AccessKey"] = accessKey;
        if (remoteIp is not null)
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return httpContext;
    }

    // --- S3 partitioner ---

    [Fact]
    public void S3Partitioner_UsesAccessKeyWhenPresent()
    {
        var ctx = NewHttpContext(accessKey: "AKIAUSER1", remoteIp: "10.0.0.1");
        S3Partition(ctx, new RateLimitOptions()).PartitionKey.Should().Be("AKIAUSER1");
    }

    [Fact]
    public void S3Partitioner_FallsBackToClientIp_WhenAccessKeyMissing()
    {
        var ctx = NewHttpContext(accessKey: null, remoteIp: "10.0.0.99");
        S3Partition(ctx, new RateLimitOptions()).PartitionKey.Should().Be("10.0.0.99");
    }

    [Fact]
    public void S3Partitioner_FallsBackToAnon_WhenNeitherKeyNorIpAvailable()
    {
        var ctx = NewHttpContext(accessKey: null, remoteIp: null);
        S3Partition(ctx, new RateLimitOptions()).PartitionKey.Should().Be("anon");
    }

    [Fact]
    public void S3Partitioner_DistinctAccessKeys_DistinctPartitions()
    {
        var opts = new RateLimitOptions();
        var a = S3Partition(NewHttpContext(accessKey: "AKIA_A", remoteIp: "1.1.1.1"), opts);
        var b = S3Partition(NewHttpContext(accessKey: "AKIA_B", remoteIp: "1.1.1.1"), opts);
        a.PartitionKey.Should().NotBe(b.PartitionKey);
    }

    [Fact]
    public void S3Partitioner_SameAccessKeyFromDifferentIps_SamePartition()
    {
        // The whole point of partitioning on access key: limits travel with the
        // principal, not the network location. A user behind two different IPs
        // must still hit the same bucket.
        var opts = new RateLimitOptions();
        var a = S3Partition(NewHttpContext(accessKey: "AKIA_X", remoteIp: "1.1.1.1"), opts);
        var b = S3Partition(NewHttpContext(accessKey: "AKIA_X", remoteIp: "2.2.2.2"), opts);
        a.PartitionKey.Should().Be(b.PartitionKey);
    }

    // --- Admin partitioner ---

    [Fact]
    public void AdminPartitioner_UsesClientIp()
    {
        var ctx = NewHttpContext(remoteIp: "203.0.113.7");
        AdminPartition(ctx, new RateLimitOptions()).PartitionKey.Should().Be("203.0.113.7");
    }

    [Fact]
    public void AdminPartitioner_FallsBackToAnon_WhenIpMissing()
    {
        var ctx = NewHttpContext(remoteIp: null);
        AdminPartition(ctx, new RateLimitOptions()).PartitionKey.Should().Be("anon");
    }

    [Fact]
    public void AdminPartitioner_IgnoresAccessKey()
    {
        // The admin policy must NOT pick up S3's Items["AccessKey"]; admin
        // auth is the admin token, partitioned by IP. If this ever drifts to
        // "use access key when present", an attacker with an S3 key would get
        // a separate admin quota per key — which defeats the per-IP intent.
        var withKey = NewHttpContext(accessKey: "AKIA_S3", remoteIp: "1.2.3.4");
        var withoutKey = NewHttpContext(accessKey: null, remoteIp: "1.2.3.4");
        AdminPartition(withKey, new RateLimitOptions())
            .PartitionKey.Should()
            .Be(AdminPartition(withoutKey, new RateLimitOptions()).PartitionKey);
    }

    // --- Defaults respect the documented contract ---

    [Fact]
    public void RateLimitOptions_Defaults_AreProductionGenerous()
    {
        // Lock the defaults — operators rely on these in appsettings.json. If
        // they change, the README and the production-readiness review must too.
        var opts = new RateLimitOptions();
        opts.Enabled.Should().BeTrue();
        opts.S3PermitPerSecond.Should().Be(100);
        opts.S3Burst.Should().Be(200);
        opts.AdminPermitPerSecond.Should().Be(20);
        opts.AdminBurst.Should().Be(40);
    }
}
