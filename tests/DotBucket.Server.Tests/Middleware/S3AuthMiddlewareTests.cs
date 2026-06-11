// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using AwesomeAssertions;
using DotBucket.Server.Auth;
using DotBucket.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DotBucket.Server.Tests.Middleware;

public class S3AuthMiddlewareTests
{
    private readonly ILogger<S3AuthMiddleware> _logger = Substitute.For<
        ILogger<S3AuthMiddleware>
    >();

    [Fact]
    public async Task InvokeAsync_WithPresignedQuery_InvokesAuthenticator()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/bucket/object";
        context.Request.QueryString = new QueryString(
            "?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Date=20260224T000000Z"
        );

        var authenticator = Substitute.For<ISigV4Authenticator>();
        authenticator
            .AuthenticateAsync(context, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var middleware = new S3AuthMiddleware(_ => Task.CompletedTask, _logger);

        await middleware.InvokeAsync(context, authenticator);

        await authenticator.Received(1).AuthenticateAsync(context, Arg.Any<CancellationToken>());
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }
}
