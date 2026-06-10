// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using DotBucket.Server.Configuration;
using DotBucket.Server.Endpoints.S3;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Middleware;

/// <summary>
/// Rewrites virtual-hosted-style requests ("{bucket}.{domain}/key") into the
/// equivalent path-style form ("/{bucket}/key") so the existing path-style
/// endpoints handle them.
///
/// IMPORTANT: this middleware MUST run AFTER <see cref="S3AuthMiddleware"/> so the
/// SigV4 signature is verified against the original Host header and path. Only the
/// request Path is rewritten here; the Host header is never mutated.
/// </summary>
public class VirtualHostMiddleware(
    RequestDelegate next,
    IOptions<S3Options> options,
    ILogger<VirtualHostMiddleware> logger
)
{
    private readonly S3Options _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.VirtualHostedStyle || _options.Domains.Count == 0)
        {
            await next(context);
            return;
        }

        var host = context.Request.Host.Host; // port already stripped by HostString.Host

        // IP literals are never virtual-hosted.
        if (!string.IsNullOrEmpty(host) && !IPAddress.TryParse(host, out _))
        {
            foreach (var domain in _options.Domains)
            {
                if (string.IsNullOrWhiteSpace(domain))
                    continue;

                // Apex domain => path-style, no rewrite.
                if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
                    break;

                var suffix = "." + domain;
                if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var label = host[..^suffix.Length];
                var dot = label.IndexOf('.');
                var bucket = dot >= 0 ? label[..dot] : label; // leading label only

                // Skip empty or reserved names (e.g. "admin.s3.example.com").
                if (string.IsNullOrEmpty(bucket) || S3Endpoints.IsReservedBucketNamePublic(bucket))
                    break;

                var original = context.Request.Path.Value;
                context.Request.Path =
                    string.IsNullOrEmpty(original) || original == "/"
                        ? new PathString("/" + bucket)
                        : new PathString("/" + bucket + original);
                context.Items["VirtualHostBucket"] = bucket;
                logger.LogDebug(
                    "Virtual-hosted request for host {Host} rewritten to path {Path}",
                    host,
                    context.Request.Path
                );
                break;
            }
        }

        await next(context);
    }
}
