// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DotBucket.Server.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DotBucket.Server.IntegrationTests;

public class AdminBucketTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AdminBucketTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdminHealth_ReturnsHealthy()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync(
            StorageObjectJsonContext.Default.AdminHealthResponse,
            TestContext.Current.CancellationToken
        );
        result.Should().NotBeNull();
        result!.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task CreateBucket_WithToken_Succeeds()
    {
        // Arrange
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "dev-token"
        );
        var bucketName = $"test-bucket-{Guid.NewGuid():N}";

        // Act
        var response = await client.PostAsJsonAsync(
            "/admin/buckets",
            new CreateBucketRequest(bucketName),
            StorageObjectJsonContext.Default.CreateBucketRequest,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bucket = await response.Content.ReadFromJsonAsync(
            StorageObjectJsonContext.Default.Bucket,
            TestContext.Current.CancellationToken
        );
        bucket.Should().NotBeNull();
        bucket!.Name.Should().Be(bucketName);
    }

    [Fact]
    public async Task CreateBucket_WithoutToken_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();
        var bucketName = $"test-bucket-{Guid.NewGuid():N}";

        // Act
        var response = await client.PostAsJsonAsync(
            "/admin/buckets",
            new CreateBucketRequest(bucketName),
            StorageObjectJsonContext.Default.CreateBucketRequest,
            TestContext.Current.CancellationToken
        );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
