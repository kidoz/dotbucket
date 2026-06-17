// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Xml.Linq;
using DotBucket.Server.Endpoints.S3;
using DotBucket.Server.Iam;

namespace DotBucket.Server.Middleware;

/// <summary>
/// Middleware that enforces IAM policy-based authorization on S3 requests.
/// Runs after S3AuthMiddleware (which sets context.Items["AccessKey"]).
/// </summary>
public class S3AuthorizationMiddleware(
    RequestDelegate next,
    ILogger<S3AuthorizationMiddleware> logger
)
{
    private const string ArnPrefix = "arn:aws:s3:::";

    public async Task InvokeAsync(HttpContext context, PolicyEngine policyEngine)
    {
        // If no access key was set by auth middleware, deny unless this is a
        // SPA pass-through (S3AuthMiddleware set S3AuthSkipped for the root path).
        if (
            !context.Items.TryGetValue("AccessKey", out var accessKeyObj)
            || accessKeyObj is not string accessKey
        )
        {
            if (context.Items.ContainsKey("S3AuthSkipped"))
            {
                await next(context);
                return;
            }

            logger.LogWarning(
                "Unauthenticated request to {Path} reached authorization middleware",
                context.Request.Path
            );
            await S3ErrorResponses.AccessDeniedAsync(context);
            return;
        }

        // Resolve S3 action + resource
        var resolved = S3ActionResolver.Resolve(context);
        if (resolved == null)
        {
            // Fail closed: an authenticated request whose action cannot be mapped to an
            // IAM action must NOT be allowed through unauthorized. Denying here ensures
            // that any future/unmapped endpoint is not silently exposed without IAM
            // enforcement. (Unauthenticated SPA pass-through is handled above via
            // S3AuthSkipped before this point.)
            logger.LogWarning(
                "Unmapped S3 action for {Method} {Path} by {AccessKey} — denying (fail closed)",
                context.Request.Method,
                context.Request.Path,
                accessKey
            );
            await S3ErrorResponses.AccessDeniedAsync(context);
            return;
        }

        var (action, resource) = resolved.Value;

        // Batch delete (POST /{bucket}?delete) authorizes each object in the request body
        // individually against s3:DeleteObject, instead of a single bucket-level check.
        if (
            context.Request.Method == "POST"
            && context.Request.Query.ContainsKey("delete")
            && action == "s3:DeleteObject"
        )
        {
            await AuthorizeBatchDeleteAsync(context, policyEngine, accessKey, resource);
            return;
        }

        // Evaluate primary action
        var authContext = new S3AuthorizationContext
        {
            Action = action,
            Resource = resource,
            AccessKey = accessKey,
        };

        var result = await policyEngine.EvaluateAsync(authContext, context.RequestAborted);
        if (result != AuthorizationResult.Allow)
        {
            logger.LogWarning(
                "Access denied for {AccessKey} on {Action} {Resource}",
                accessKey,
                action,
                resource
            );
            await S3ErrorResponses.AccessDeniedAsync(context);
            return;
        }

        // CopyObject dual-auth: if this is a PutObject with x-amz-copy-source, also check GetObject on source
        if (action == "s3:PutObject")
        {
            var sourceArn = S3ActionResolver.ResolveCopySource(context);
            if (sourceArn != null)
            {
                var sourceContext = new S3AuthorizationContext
                {
                    Action = "s3:GetObject",
                    Resource = sourceArn,
                    AccessKey = accessKey,
                };

                var sourceResult = await policyEngine.EvaluateAsync(
                    sourceContext,
                    context.RequestAborted
                );
                if (sourceResult != AuthorizationResult.Allow)
                {
                    logger.LogWarning(
                        "Access denied for {AccessKey} on copy source {Resource}",
                        accessKey,
                        sourceArn
                    );
                    await S3ErrorResponses.AccessDeniedAsync(context);
                    return;
                }
            }
        }

        await next(context);
    }

    /// <summary>
    /// Authorizes a multi-object delete (POST /{bucket}?delete) by evaluating
    /// s3:DeleteObject against each object ARN parsed from the request body. The whole
    /// request is denied if the caller lacks permission on ANY requested key. The body is
    /// buffered and rewound so the endpoint can re-read it.
    /// </summary>
    private async Task AuthorizeBatchDeleteAsync(
        HttpContext context,
        PolicyEngine policyEngine,
        string accessKey,
        string bucketResource
    )
    {
        var bucket = bucketResource.StartsWith(ArnPrefix, StringComparison.Ordinal)
            ? bucketResource[ArnPrefix.Length..]
            : bucketResource;

        // Buffer the body so the downstream endpoint can read it again.
        context.Request.EnableBuffering();
        List<string> keys;
        try
        {
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var xml = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;

            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            keys =
                doc.Root?.Elements(ns + "Object")
                    .Select(o => o.Element(ns + "Key")?.Value ?? string.Empty)
                    .Where(k => !string.IsNullOrEmpty(k))
                    .ToList()
                ?? new List<string>();
        }
        catch
        {
            // Malformed/empty body: there is nothing authorizable to delete. Rewind and let
            // the endpoint return its own MalformedXML/400 response.
            if (context.Request.Body.CanSeek)
                context.Request.Body.Position = 0;
            await next(context);
            return;
        }

        foreach (var key in keys)
        {
            var authContext = new S3AuthorizationContext
            {
                Action = "s3:DeleteObject",
                Resource = $"{ArnPrefix}{bucket}/{key}",
                AccessKey = accessKey,
            };
            var result = await policyEngine.EvaluateAsync(authContext, context.RequestAborted);
            if (result != AuthorizationResult.Allow)
            {
                logger.LogWarning(
                    "Access denied for {AccessKey} on batch delete of {Bucket}/{Key}",
                    accessKey,
                    bucket,
                    key
                );
                await S3ErrorResponses.AccessDeniedAsync(context);
                return;
            }
        }

        await next(context);
    }
}
