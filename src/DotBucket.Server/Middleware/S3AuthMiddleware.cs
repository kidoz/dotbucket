// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Middleware;

using DotBucket.Server.Auth;
using DotBucket.Server.Endpoints.S3;

/// <summary>
/// Middleware to intercept incoming S3 API requests and validate them using AWS Signature Version 4.
/// Requests without S3 auth headers are rejected (except the root path for SPA serving).
/// </summary>
public class S3AuthMiddleware(RequestDelegate next, ILogger<S3AuthMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ISigV4Authenticator authenticator)
    {
        var hasS3Auth =
            context.Request.Headers.ContainsKey("Authorization")
            || context.Request.Headers.ContainsKey("x-amz-date")
            // Presigned URLs carry auth material in query params.
            || context.Request.Query.ContainsKey("X-Amz-Algorithm");

        if (!hasS3Auth)
        {
            // No S3 auth headers — not an S3 request.
            // Allow root path through for SPA fallback (index.html).
            // All other paths are rejected to prevent unauthenticated S3 access.
            if (context.Request.Path.Value is "/" or "")
            {
                context.Items["S3AuthSkipped"] = true;
                await next(context);
                return;
            }

            logger.LogWarning(
                "Request to {Path} has no S3 auth headers — rejecting.",
                context.Request.Path
            );
            await S3ErrorResponses.AccessDeniedAsync(context);
            return;
        }

        // Validate SigV4 authentication
        var routedPath = context.Request.Path;
        bool isAuthenticated;
        try
        {
            if (context.Items[typeof(VirtualHostMiddleware)] is PathString signedPath)
                context.Request.Path = signedPath;
            isAuthenticated = await authenticator.AuthenticateAsync(
                context,
                context.RequestAborted
            );
        }
        finally
        {
            context.Request.Path = routedPath;
        }
        if (!isAuthenticated)
        {
            logger.LogWarning("Request to {Path} failed S3 authentication.", context.Request.Path);
            await S3ErrorResponses.AccessDeniedAsync(context);
            return;
        }

        await next(context);
    }
}
