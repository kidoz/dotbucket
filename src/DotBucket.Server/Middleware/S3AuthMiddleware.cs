// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Middleware;

using DotBucket.Server.Auth;
using DotBucket.Server.Endpoints.S3;

/// <summary>
/// Middleware to intercept incoming S3 API requests and validate them using AWS Signature Version 4.
/// </summary>
public class S3AuthMiddleware(RequestDelegate next, ILogger<S3AuthMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ISigV4Authenticator authenticator)
    {
        // Apply SigV4 authentication
        var isAuthenticated = await authenticator.AuthenticateAsync(
            context,
            context.RequestAborted
        );
        if (!isAuthenticated)
        {
            logger.LogWarning("Request to {Path} failed S3 authentication.", context.Request.Path);
            await S3ErrorResponses.AccessDeniedAsync(context);
            return;
        }

        await next(context);
    }
}
