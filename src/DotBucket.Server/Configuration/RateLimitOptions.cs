// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

/// <summary>
/// Configuration for the built-in ASP.NET Core rate limiter. Two surfaces are
/// throttled with independent token buckets:
/// <list type="bullet">
/// <item>The S3 API, partitioned per authenticated access key (falls back to
/// client IP for unauthenticated requests).</item>
/// <item>The admin/IAM API, partitioned per client IP.</item>
/// </list>
///
/// The S3 surface is partitioned on the access key so limits apply uniformly
/// regardless of whether the deployment is behind a reverse proxy. The admin
/// surface uses the direct connection IP; behind a proxy, deploy
/// <c>ForwardedHeaders</c> middleware separately (follow-up: it requires a
/// trust-boundary decision and is intentionally out of scope for this knob).
/// </summary>
public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>
    /// Master switch. When false, no rate limiter is added to the pipeline and
    /// every request flows through unthrottled. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Token replenishment rate, in tokens per second, applied per S3 access key.
    /// Default 100. Generous enough for typical SDK burst patterns.
    /// </summary>
    public int S3PermitPerSecond { get; set; } = 100;

    /// <summary>
    /// Token bucket capacity per S3 access key. Sets the burst ceiling: a client
    /// can briefly exceed <see cref="S3PermitPerSecond"/> up to this many queued
    /// tokens. Default 200.
    /// </summary>
    public int S3Burst { get; set; } = 200;

    /// <summary>
    /// Token replenishment rate, in tokens per second, applied per admin-API
    /// client IP. Default 20 — admin traffic is interactive, not bulk.
    /// </summary>
    public int AdminPermitPerSecond { get; set; } = 20;

    /// <summary>
    /// Token bucket capacity per admin-API client IP. Default 40.
    /// </summary>
    public int AdminBurst { get; set; } = 40;

    /// <summary>
    /// Upper bound on the number of tokens a queue may hold across all
    /// partitions. Default 1000 — prevents unbounded per-partition queue growth
    /// under a flood from many distinct keys/IPs.
    /// </summary>
    public int GlobalQueueLimit { get; set; } = 1000;
}
