// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using DotBucket.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Auth;

public class AdminTokenEndpointFilter(IOptions<AuthOptions> options) : IEndpointFilter
{
    private readonly AuthOptions _authOptions = options.Value;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        if (string.IsNullOrEmpty(_authOptions.AdminToken))
        {
            return Results.StatusCode(503);
        }

        var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();
        if (
            string.IsNullOrEmpty(authHeader)
            || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        )
        {
            return Results.Unauthorized();
        }

        var providedToken = authHeader["Bearer ".Length..];

        var expectedBytes = Encoding.UTF8.GetBytes(_authOptions.AdminToken);
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);

        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
