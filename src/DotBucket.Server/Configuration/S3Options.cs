// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

/// <summary>
/// Configuration options for S3 request addressing and region handling.
/// </summary>
public class S3Options
{
    public const string SectionName = "S3";

    /// <summary>
    /// When true, requests whose Host header matches "{bucket}.{domain}" (for a
    /// configured domain) are treated as virtual-hosted-style and routed to the
    /// corresponding bucket. When false, only path-style addressing is used.
    /// </summary>
    public bool VirtualHostedStyle { get; set; }

    /// <summary>
    /// Base domains that may carry a bucket subdomain, e.g. "s3.example.com".
    /// A request with Host == "{bucket}.{domain}" is rewritten to path "/{bucket}{path}".
    /// </summary>
    public List<string> Domains { get; set; } = new();

    /// <summary>
    /// The region advertised by the server (e.g. in GetBucketLocation responses and
    /// the x-amz-bucket-region header). Defaults to "us-east-1".
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// When true, SigV4 requests whose credential-scope region does not match
    /// <see cref="Region"/> are rejected. When false (default), any region is accepted.
    /// </summary>
    public bool StrictSigningRegion { get; set; }
}
