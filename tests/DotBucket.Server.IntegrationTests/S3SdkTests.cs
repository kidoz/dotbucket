// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using Amazon.S3;
using Amazon.S3.Model;
using DotBucket.Server.Auth;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DotBucket.Server.IntegrationTests;

public class S3SdkTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public S3SdkTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private AmazonS3Client CreateS3Client()
    {
        // AWS SDK uses DangerousDisablePathAndQueryCanonicalization which breaks TestHost.
        // We wrap the factory client in a handler that fixes the URI.
        // SigV4 through TestHost has known incompatibilities with SDK v4; we bypass auth
        // for the lifecycle test (auth is tested separately in unit tests).
        var httpClient = _factory
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<ISigV4Authenticator, AllowAllAuthenticator>()
                )
            )
            .CreateClient();
        var uriFixingClient = new HttpClient(new UriFixingHandler(httpClient))
        {
            BaseAddress = httpClient.BaseAddress,
        };

        var config = new AmazonS3Config
        {
            ServiceURL = httpClient.BaseAddress?.ToString() ?? "http://localhost",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            HttpClientFactory = new TestHttpClientFactory(uriFixingClient),
        };

        return new AmazonS3Client("admin", "admin123", config);
    }

    [Fact]
    public async Task FullBucketAndObjectLifecycle_UsingAwsSdk()
    {
        var ct = TestContext.Current.CancellationToken;
        using var s3 = CreateS3Client();
        var bucketName = $"lifecycle-test-{Guid.NewGuid():N}";
        var objectKey = "test-object.txt";
        var objectContent = "Hello, DotBucket lifecycle test!";

        try
        {
            // 1. CreateBucket
            var putBucketResponse = await s3.PutBucketAsync(
                new PutBucketRequest { BucketName = bucketName },
                ct
            );
            putBucketResponse.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.OK);

            // 2. PutObject — disable chunked encoding to avoid aws-chunked content
            // which embeds chunk signatures the server doesn't decode
            var contentBytes = System.Text.Encoding.UTF8.GetBytes(objectContent);
            var putObjectRequest = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = objectKey,
                InputStream = new MemoryStream(contentBytes),
                ContentType = "text/plain",
                UseChunkEncoding = false,
            };
            var putObjectResponse = await s3.PutObjectAsync(putObjectRequest, ct);
            putObjectResponse.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            putObjectResponse.ETag.Should().NotBeNullOrEmpty();

            // 3. GetObject — verify content round-trips
            var getObjectResponse = await s3.GetObjectAsync(bucketName, objectKey, ct);
            getObjectResponse.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            using (var reader = new StreamReader(getObjectResponse.ResponseStream))
            {
                var body = await reader.ReadToEndAsync(ct);
                body.Should().Be(objectContent);
            }

            // 4. HeadObject — verify metadata
            var headResponse = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucketName, Key = objectKey },
                ct
            );
            headResponse.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            headResponse.ContentLength.Should().BeGreaterThan(0);

            // 5. ListObjectsV2 — verify object appears in listing
            var listResponse = await s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucketName },
                ct
            );
            listResponse.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            listResponse.S3Objects.Should().ContainSingle(o => o.Key == objectKey);

            // 6. DeleteObject
            var deleteResponse = await s3.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = bucketName, Key = objectKey },
                ct
            );
            deleteResponse.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);

            // 7. Verify bucket is empty
            var listAfterDelete = await s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucketName },
                ct
            );
            (listAfterDelete.S3Objects ?? []).Should().BeEmpty();

            // 8. DeleteBucket
            var deleteBucketResponse = await s3.DeleteBucketAsync(
                new DeleteBucketRequest { BucketName = bucketName },
                ct
            );
            deleteBucketResponse.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        }
        catch
        {
            // Best-effort cleanup
            try
            {
                await s3.DeleteObjectAsync(
                    new DeleteObjectRequest { BucketName = bucketName, Key = objectKey },
                    ct
                );
            }
            catch { }
            try
            {
                await s3.DeleteBucketAsync(
                    new DeleteBucketRequest { BucketName = bucketName },
                    ct
                );
            }
            catch { }
            throw;
        }
    }

    private class UriFixingHandler(HttpClient innerClient) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            // Clone the request but fix the URI to remove DangerousDisablePathAndQueryCanonicalization
            var fixedUri = new Uri(request.RequestUri!.ToString());
            var fixedRequest = new HttpRequestMessage(request.Method, fixedUri);

            foreach (var header in request.Headers)
                fixedRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (request.Content != null)
            {
                var ms = new MemoryStream();
                await request.Content.CopyToAsync(ms, cancellationToken);
                ms.Position = 0;
                fixedRequest.Content = new StreamContent(ms);
                foreach (var header in request.Content.Headers)
                    fixedRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return await innerClient.SendAsync(fixedRequest, cancellationToken);
        }
    }

    private class TestHttpClientFactory(HttpClient client) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig config)
        {
            return client;
        }
    }

    /// <summary>
    /// Test authenticator that accepts all requests and sets the access key to "admin".
    /// SigV4 signature validation through TestHost has known incompatibilities with AWS SDK v4;
    /// this bypasses auth so the lifecycle test exercises the full S3 API surface.
    /// Auth correctness is covered by dedicated unit tests (SigV4AuthenticatorTests, S3AuthMiddlewareTests).
    /// </summary>
    private class AllowAllAuthenticator : ISigV4Authenticator
    {
        public Task<bool> AuthenticateAsync(
            HttpContext context,
            CancellationToken cancellationToken = default
        )
        {
            context.Items["AccessKey"] = "admin";
            return Task.FromResult(true);
        }
    }
}
