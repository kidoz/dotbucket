// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;

namespace DotBucket.Server.E2ETests;

/// <summary>
/// End-to-end tests against the real server process with production configuration:
/// real Kestrel, real SigV4 signing by the AWS SDK (including aws-chunked streaming
/// uploads with per-chunk signatures), real SQLite-backed storage.
/// </summary>
public class ProductionE2ETests(DotBucketServerFixture server)
    : IClassFixture<DotBucketServerFixture>
{
    private AmazonS3Client CreateClient(string? secretKeyOverride = null) =>
        new(
            DotBucketServerFixture.RootAccessKey,
            secretKeyOverride ?? DotBucketServerFixture.RootSecretKey,
            new AmazonS3Config
            {
                ServiceURL = server.BaseUrl,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
            }
        );

    [Fact]
    public async Task HealthEndpoint_ReportsHealthy()
    {
        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var response = await http.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("Healthy");
    }

    [Fact]
    public async Task UnauthenticatedS3Request_IsDenied()
    {
        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        var response = await http.GetAsync("/some-bucket", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("AccessDenied");
    }

    [Fact]
    public async Task WrongSecretKey_IsRejected()
    {
        using var s3 = CreateClient(secretKeyOverride: "wrong-secret-key");

        var act = async () =>
            await s3.PutBucketAsync(
                new PutBucketRequest { BucketName = $"denied-{Guid.NewGuid():N}" },
                TestContext.Current.CancellationToken
            );

        (await act.Should().ThrowAsync<AmazonS3Exception>())
            .Which.StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminApi_RequiresBearerToken()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

        var withoutToken = await http.GetAsync("/admin/buckets", ct);
        withoutToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            DotBucketServerFixture.AdminToken
        );
        var withToken = await http.GetAsync("/admin/buckets", ct);
        withToken.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ObjectLifecycle_WithChunkedStreamingUpload_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        using var s3 = CreateClient();
        var bucket = $"e2e-chunked-{Guid.NewGuid():N}";
        const string key = "streamed/payload.bin";

        // 1 MiB of random data: the SDK splits this into multiple signed aws-chunked
        // chunks, exercising per-chunk signature validation in the production path.
        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct);
        try
        {
            var put = await s3.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = new MemoryStream(payload),
                    ContentType = "application/octet-stream",
                    UseChunkEncoding = true,
                },
                ct
            );
            put.HttpStatusCode.Should().Be(HttpStatusCode.OK);

            // Content must round-trip byte-for-byte (proves framing was stripped and
            // the decoded payload — not the chunked wire format — was stored).
            using var get = await s3.GetObjectAsync(bucket, key, ct);
            using var received = new MemoryStream();
            await get.ResponseStream.CopyToAsync(received, ct);
            received.ToArray().Should().Equal(payload);

            var head = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucket, Key = key },
                ct
            );
            head.ContentLength.Should().Be(payload.Length);

            var list = await s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket },
                ct
            );
            list.S3Objects.Should().ContainSingle(o => o.Key == key);
        }
        finally
        {
            await s3.DeleteObjectAsync(bucket, key, ct);
            await s3.DeleteBucketAsync(bucket, ct);
        }
    }

    [Fact]
    public async Task MultipartUpload_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        using var s3 = CreateClient();
        var bucket = $"e2e-multipart-{Guid.NewGuid():N}";
        const string key = "multipart/large.bin";

        var part1 = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var part2 = RandomNumberGenerator.GetBytes(64 * 1024);

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct);
        try
        {
            var initiate = await s3.InitiateMultipartUploadAsync(
                new InitiateMultipartUploadRequest { BucketName = bucket, Key = key },
                ct
            );

            var upload1 = await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = initiate.UploadId,
                    PartNumber = 1,
                    InputStream = new MemoryStream(part1),
                },
                ct
            );
            var upload2 = await s3.UploadPartAsync(
                new UploadPartRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = initiate.UploadId,
                    PartNumber = 2,
                    InputStream = new MemoryStream(part2),
                },
                ct
            );

            var complete = await s3.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = key,
                    UploadId = initiate.UploadId,
                    PartETags = [new PartETag(1, upload1.ETag), new PartETag(2, upload2.ETag)],
                },
                ct
            );
            complete.HttpStatusCode.Should().Be(HttpStatusCode.OK);

            using var get = await s3.GetObjectAsync(bucket, key, ct);
            using var received = new MemoryStream();
            await get.ResponseStream.CopyToAsync(received, ct);
            received.ToArray().Should().Equal([.. part1, .. part2]);
        }
        finally
        {
            await s3.DeleteObjectAsync(bucket, key, ct);
            await s3.DeleteBucketAsync(bucket, ct);
        }
    }

    [Fact]
    public async Task PresignedUrl_AllowsUnauthenticatedDownload()
    {
        var ct = TestContext.Current.CancellationToken;
        using var s3 = CreateClient();
        var bucket = $"e2e-presigned-{Guid.NewGuid():N}";
        const string key = "presigned/object.txt";
        const string content = "downloaded via presigned URL";

        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct);
        try
        {
            await s3.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    ContentBody = content,
                },
                ct
            );

            var url = await s3.GetPreSignedURLAsync(
                new GetPreSignedUrlRequest
                {
                    BucketName = bucket,
                    Key = key,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.UtcNow.AddMinutes(5),
                    Protocol = Protocol.HTTP,
                }
            );

            // Plain HttpClient — no SDK, no credentials; the URL itself carries auth.
            using var http = new HttpClient();
            var response = await http.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be(content);
        }
        finally
        {
            await s3.DeleteObjectAsync(bucket, key, ct);
            await s3.DeleteBucketAsync(bucket, ct);
        }
    }
}
