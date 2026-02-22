// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using Amazon.S3;
using Microsoft.AspNetCore.Mvc.Testing;

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
        var httpClient = _factory.CreateClient();
        var uriFixingClient = new HttpClient(new UriFixingHandler(httpClient))
        {
            BaseAddress = httpClient.BaseAddress,
        };

        var config = new AmazonS3Config
        {
            ServiceURL = httpClient.BaseAddress?.ToString() ?? "http://localhost",
            ForcePathStyle = true,
            HttpClientFactory = new TestHttpClientFactory(uriFixingClient),
        };

        return new AmazonS3Client("admin", "admin123", config);
    }

    [Fact]
    public async Task FullBucketAndObjectLifecycle_UsingAwsSdk()
    {
        // ... same test code ...
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
}
