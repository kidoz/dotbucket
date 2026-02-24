// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Endpoints.S3;

namespace DotBucket.Server.Middleware;

/// <summary>
/// Endpoint filter that requires S3 authentication on all S3 endpoints.
/// Acts as the final defense layer — even if middleware is bypassed, no S3
/// endpoint will serve content without a valid AccessKey in context.
/// </summary>
public class S3AuthRequiredFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        if (!context.HttpContext.Items.ContainsKey("AccessKey"))
        {
            return Results.Text(
                """<?xml version="1.0" encoding="UTF-8"?><Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>""",
                "application/xml",
                statusCode: 403
            );
        }

        return await next(context);
    }
}
