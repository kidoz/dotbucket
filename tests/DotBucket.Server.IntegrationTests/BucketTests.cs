// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using AwesomeAssertions;
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
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        // S3 clients always send auth headers; requests with these headers must be authenticated
        request.Headers.TryAddWithoutValidation("x-amz-date", "20260101T000000Z");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", "UNSIGNED-PAYLOAD");

        // Act
        var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken
        );

        // Assert
        // S3 requests without valid SigV4 credentials should be denied
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PutBucket_WithoutAuthHeaders_Returns403()
    {
        // Arrange — no S3 auth headers at all
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/test-bucket-noauth");

        // Act
        var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken
        );

        // Assert — must be rejected, not 200
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetBucket_WithoutAuthHeaders_Returns403()
    {
        // Arrange — no S3 auth headers at all
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync(
            "/test-bucket-noauth",
            TestContext.Current.CancellationToken
        );

        // Assert — must be rejected, not 200
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetRoot_WithoutAuthHeaders_ServesSpA()
    {
        // Arrange — browser request to root (no S3 headers)
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        // Assert — should NOT be 403 (SPA root must remain accessible)
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
