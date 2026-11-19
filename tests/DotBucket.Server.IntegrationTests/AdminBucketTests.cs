// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.IntegrationTests;

public class AdminBucketTests : IClassFixture<IsolatedServerFactory>
{
    private readonly IsolatedServerFactory _factory;

    public AdminBucketTests(IsolatedServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DownloadReturnsTheSelectedHistoricalVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var services = _factory.Services;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            services.GetRequiredService<IOptions<AuthOptions>>().Value.AdminToken
        );
        var storage = services.GetRequiredService<IStorageEngine>();
        var bucket = "history-" + Guid.NewGuid().ToString("N");
        await storage.CreateBucketAsync(bucket, true, ct);
        const string key = "reports/annual report";
        var first = await storage.PutObjectAsync(
            bucket,
            key,
            new MemoryStream([1]),
            "application/pdf",
            cancellationToken: ct
        );
        await storage.PutObjectAsync(
            bucket,
            key,
            new MemoryStream([2]),
            "application/pdf",
            cancellationToken: ct
        );
        var path = $"/admin/buckets/{bucket}/download/reports/annual%20report";
        Assert.Equal(
            new byte[] { 1 },
            await client.GetByteArrayAsync($"{path}?versionId={first.VersionId}", ct)
        );
        Assert.Equal(new byte[] { 2 }, await client.GetByteArrayAsync(path, ct));
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
