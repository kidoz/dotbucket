// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using AwesomeAssertions;
using DotBucket.Server.Configuration;
using DotBucket.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DotBucket.Server.Tests.Middleware;

public class VirtualHostMiddlewareTests
{
    private static VirtualHostMiddleware Create(S3Options options, RequestDelegate next) =>
        new(next, Options.Create(options), Substitute.For<ILogger<VirtualHostMiddleware>>());

    private static DefaultHttpContext ContextFor(string host, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        ctx.Request.Path = path;
        return ctx;
    }

    private static readonly S3Options Enabled = new()
    {
        VirtualHostedStyle = true,
        Domains = ["s3.example.com"],
    };

    [Fact]
    public async Task RewritesBucketSubdomainObjectRequestToPathStyle()
    {
        var ctx = ContextFor("bucket.s3.example.com", "/key.txt");
        var sut = Create(Enabled, _ => Task.CompletedTask);

        await sut.InvokeAsync(ctx);

        ctx.Request.Path.Value.Should().Be("/bucket/key.txt");
        ctx.Request.Host.Host.Should().Be("bucket.s3.example.com"); // Host never mutated
    }

    [Fact]
    public async Task RewritesBucketSubdomainRootRequestToListObjects()
    {
        var ctx = ContextFor("bucket.s3.example.com", "/");
        var sut = Create(Enabled, _ => Task.CompletedTask);

        await sut.InvokeAsync(ctx);

        ctx.Request.Path.Value.Should().Be("/bucket"); // no trailing slash
    }

    [Fact]
    public async Task LeavesApexDomainUnchanged()
    {
        var ctx = ContextFor("s3.example.com", "/bucket/key");
        var sut = Create(Enabled, _ => Task.CompletedTask);

        await sut.InvokeAsync(ctx);

        ctx.Request.Path.Value.Should().Be("/bucket/key");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("other.domain.com")]
    public async Task LeavesNonMatchingHostsUnchanged(string host)
    {
        var ctx = ContextFor(host, "/bucket/key");
        var sut = Create(Enabled, _ => Task.CompletedTask);

        await sut.InvokeAsync(ctx);

        ctx.Request.Path.Value.Should().Be("/bucket/key");
    }

    [Fact]
    public async Task DoesNotHijackReservedSubdomain()
    {
        var ctx = ContextFor("admin.s3.example.com", "/thing");
        var sut = Create(Enabled, _ => Task.CompletedTask);

        await sut.InvokeAsync(ctx);

        ctx.Request.Path.Value.Should().Be("/thing");
    }

    [Fact]
    public async Task DoesNothingWhenDisabled()
    {
        var ctx = ContextFor("bucket.s3.example.com", "/key.txt");
        var sut = Create(
            new S3Options { VirtualHostedStyle = false, Domains = ["s3.example.com"] },
            _ => Task.CompletedTask
        );

        await sut.InvokeAsync(ctx);

        ctx.Request.Path.Value.Should().Be("/key.txt");
    }
}
