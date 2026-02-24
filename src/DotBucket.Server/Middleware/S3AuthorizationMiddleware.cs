// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

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
            // Cannot determine action — allow through (will be handled by endpoint routing)
            await next(context);
            return;
        }

        var (action, resource) = resolved.Value;

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
}
