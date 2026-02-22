// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Text.Json;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Cluster;

public class NodeClient(IHttpClientFactory httpClientFactory, IOptions<ClusterOptions> options)
{
    private readonly ClusterOptions _options = options.Value;

    public async Task<IEnumerable<StorageObject>> ListObjectsAsync(
        string nodeAddress,
        string bucketName,
        string? prefix,
        bool versions,
        CancellationToken ct
    )
    {
        using var client = CreateClient();
        var url =
            $"{nodeAddress.TrimEnd('/')}/_internal/buckets/{Uri.EscapeDataString(bucketName)}/objects?versions={versions}";
        if (!string.IsNullOrEmpty(prefix))
            url += $"&prefix={Uri.EscapeDataString(prefix)}";

        var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return Enumerable.Empty<StorageObject>();

        var result = await response.Content.ReadFromJsonAsync(
            StorageObjectJsonContext.Default.IEnumerableStorageObject,
            ct
        );
        return result ?? Enumerable.Empty<StorageObject>();
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient("ClusterNode");
        client.DefaultRequestHeaders.Add("X-DotBucket-Cluster-Token", _options.ClusterToken);
        return client;
    }

    public async Task<StorageObject?> PutObjectAsync(
        string nodeAddress,
        string bucket,
        string key,
        Stream content,
        string contentType,
        Dictionary<string, string>? metadata,
        CancellationToken ct
    )
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{nodeAddress.TrimEnd('/')}/_internal/objects/{Uri.EscapeDataString(bucket)}/{key}"
        );

        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            contentType
        );
        request.Content = streamContent;

        if (metadata != null)
        {
            foreach (var (k, v) in metadata)
            {
                request.Headers.TryAddWithoutValidation($"X-DotBucket-Meta-{k}", v);
            }
        }

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            StorageObjectJsonContext.Default.StorageObject,
            ct
        );
    }

    public async Task<(StorageObject Metadata, Stream Content)?> GetObjectAsync(
        string nodeAddress,
        string bucket,
        string key,
        string? versionId,
        CancellationToken ct
    )
    {
        using var client = CreateClient();
        var url =
            $"{nodeAddress.TrimEnd('/')}/_internal/objects/{Uri.EscapeDataString(bucket)}/{key}";
        if (versionId != null)
            url += $"?versionId={Uri.EscapeDataString(versionId)}";

        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        // Parse metadata from response headers
        var metadataJson = response.Headers.TryGetValues(
            "X-DotBucket-Object-Metadata",
            out var metaValues
        )
            ? metaValues.First()
            : "{}";
        var storageObj = JsonSerializer.Deserialize(
            metadataJson,
            StorageObjectJsonContext.Default.StorageObject
        );
        if (storageObj == null)
            return null;

        var stream = await response.Content.ReadAsStreamAsync(ct);
        return (storageObj, stream);
    }

    public async Task<StorageObject?> HeadObjectAsync(
        string nodeAddress,
        string bucket,
        string key,
        string? versionId,
        CancellationToken ct
    )
    {
        using var client = CreateClient();
        var url =
            $"{nodeAddress.TrimEnd('/')}/_internal/objects/{Uri.EscapeDataString(bucket)}/{key}";
        if (versionId != null)
            url += $"?versionId={Uri.EscapeDataString(versionId)}";

        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var metadataJson = response.Headers.TryGetValues(
            "X-DotBucket-Object-Metadata",
            out var metaValues
        )
            ? metaValues.First()
            : null;
        if (metadataJson == null)
            return null;

        return JsonSerializer.Deserialize(
            metadataJson,
            StorageObjectJsonContext.Default.StorageObject
        );
    }

    public async Task<bool> DeleteObjectAsync(
        string nodeAddress,
        string bucket,
        string key,
        string? versionId,
        CancellationToken ct
    )
    {
        using var client = CreateClient();
        var url =
            $"{nodeAddress.TrimEnd('/')}/_internal/objects/{Uri.EscapeDataString(bucket)}/{key}";
        if (versionId != null)
            url += $"?versionId={Uri.EscapeDataString(versionId)}";

        var response = await client.DeleteAsync(url, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<Bucket?> CreateBucketAsync(
        string nodeAddress,
        string bucketName,
        CancellationToken ct
    )
    {
        using var client = CreateClient();
        var body = new InternalCreateBucketRequest(bucketName);
        var content = JsonContent.Create(
            body,
            StorageObjectJsonContext.Default.InternalCreateBucketRequest
        );
        var response = await client.PostAsync(
            $"{nodeAddress.TrimEnd('/')}/_internal/buckets",
            content,
            ct
        );
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync(
            StorageObjectJsonContext.Default.Bucket,
            ct
        );
    }

    public async Task DeleteBucketAsync(string nodeAddress, string bucketName, CancellationToken ct)
    {
        using var client = CreateClient();
        var response = await client.DeleteAsync(
            $"{nodeAddress.TrimEnd('/')}/_internal/buckets/{Uri.EscapeDataString(bucketName)}",
            ct
        );
        response.EnsureSuccessStatusCode();
    }

    public async Task SetVersioningAsync(
        string nodeAddress,
        string bucket,
        string status,
        CancellationToken ct
    )
    {
        using var client = CreateClient();
        var body = new InternalSetVersioningRequest(status);
        var content = JsonContent.Create(
            body,
            StorageObjectJsonContext.Default.InternalSetVersioningRequest
        );
        var response = await client.PostAsync(
            $"{nodeAddress.TrimEnd('/')}/_internal/buckets/{Uri.EscapeDataString(bucket)}/versioning",
            content,
            ct
        );
        response.EnsureSuccessStatusCode();
    }

    public async Task<NodeHealth> HealthCheckAsync(string nodeAddress, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            client.Timeout = TimeSpan.FromMilliseconds(_options.HealthCheckTimeoutMs);
            var response = await client.GetAsync(
                $"{nodeAddress.TrimEnd('/')}/_internal/health",
                ct
            );
            if (response.IsSuccessStatusCode)
            {
                return new NodeHealth(NodeHealthStatus.Healthy, DateTime.UtcNow, 0);
            }
            return new NodeHealth(NodeHealthStatus.Suspect, DateTime.UtcNow, 1);
        }
        catch
        {
            return new NodeHealth(NodeHealthStatus.Dead, DateTime.UtcNow, int.MaxValue);
        }
    }
}
