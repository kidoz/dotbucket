// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DotBucket.Server.IntegrationTests;

public class BucketTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BucketTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken
        );
        content.Should().Contain("Healthy");
    }

    [Fact]
    public async Task GetBuckets_RequiresAuthentication()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // Assert
        // Standard S3 requests go through S3AuthMiddleware
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
