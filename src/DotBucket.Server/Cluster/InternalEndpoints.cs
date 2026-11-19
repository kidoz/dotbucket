// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Cluster;

public static class InternalEndpoints
{
    public static void MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/_internal").AddEndpointFilter<ClusterTokenFilter>();

        // PUT /_internal/coordinator/objects/{bucket}/{**key}
        group.MapPut(
            "/coordinator/objects/{bucket}/{**key}",
            async (
                string bucket,
                string key,
                HttpRequest request,
                DistributedStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var contentType = request.ContentType ?? "application/octet-stream";

                // Extract encryption flag
                string? encryption = request.Headers["X-DotBucket-Encryption"].FirstOrDefault();

                // Extract metadata from headers
                Dictionary<string, string>? metadata = null;
                foreach (var header in request.Headers)
                {
                    if (
                        header.Key.StartsWith(
                            "X-DotBucket-Meta-",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        metadata ??= new Dictionary<string, string>();
                        var metaKey = header.Key["X-DotBucket-Meta-".Length..];
                        metadata[metaKey] = header.Value.ToString();
                    }
                }

                var obj = await storage.PutOnPrimaryAsync(
                    bucket,
                    key,
                    request.Body,
                    contentType,
                    metadata,
                    encryption,
                    ct
                );
                return Results.Ok(obj);
            }
        );

        group.MapPut(
            "/staged/{operation:guid}",
            async (
                Guid operation,
                bool encrypted,
                bool replay,
                string bucket,
                string key,
                string versionId,
                HttpRequest request,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var existing = await storage.GetBucketAsync(bucket, ct);
                if (
                    existing == null
                    || (existing.Versioning == VersioningStatus.Enabled) != (versionId != "null")
                )
                    return Results.Conflict();
                await storage.PrepareReplicaAsync(
                    operation,
                    bucket,
                    key,
                    versionId,
                    request.Body,
                    encrypted,
                    replay,
                    ct
                );
                return Results.NoContent();
            }
        );
        group.MapPost(
            "/staged/{operation:guid}",
            async (
                Guid operation,
                StorageObject obj,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                try
                {
                    await storage.CommitReplicaAsync(operation, obj, ct);
                }
                catch (FileNotFoundException)
                {
                    return Results.NotFound();
                }
                return Results.NoContent();
            }
        );
        group.MapDelete(
            "/staged/{operation:guid}",
            async (Guid operation, LocalFileSystemStorageEngine storage, CancellationToken ct) =>
            {
                await storage.DiscardReplicaStageAsync(operation, ct);
                return Results.NoContent();
            }
        );

        // GET /_internal/objects/{bucket}/{**key}
        group.MapGet(
            "/objects/{bucket}/{**key}",
            async (
                string bucket,
                string key,
                string? versionId,
                HttpContext httpContext,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var result = await storage.GetObjectAsync(bucket, key, versionId, ct);
                if (result == null)
                    return Results.NotFound();

                var (metadata, content) = result.Value;

                // Send metadata as a header so NodeClient can reconstruct StorageObject
                var metadataJson = JsonSerializer.Serialize(
                    metadata,
                    StorageObjectJsonContext.Default.StorageObject
                );
                httpContext.Response.Headers["X-DotBucket-Object-Metadata"] = metadataJson;

                return Results.Stream(content, metadata.ContentType);
            }
        );

        // HEAD /_internal/objects/{bucket}/{**key}
        group.MapMethods(
            "/objects/{bucket}/{**key}",
            ["HEAD"],
            async (
                string bucket,
                string key,
                string? versionId,
                HttpContext httpContext,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var obj = await storage.HeadObjectAsync(bucket, key, versionId, ct);
                if (obj == null)
                    return Results.NotFound();

                var metadataJson = JsonSerializer.Serialize(
                    obj,
                    StorageObjectJsonContext.Default.StorageObject
                );
                httpContext.Response.Headers["X-DotBucket-Object-Metadata"] = metadataJson;

                return Results.Ok();
            }
        );

        // DELETE /_internal/objects/{bucket}/{**key}
        group.MapDelete(
            "/objects/{bucket}/{**key}",
            async (
                string bucket,
                string key,
                string? versionId,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var deleted = await storage.DeleteObjectAsync(bucket, key, versionId, ct);
                return deleted ? Results.Ok() : Results.NotFound();
            }
        );

        group.MapPost(
            "/coordinator/buckets",
            async (
                InternalCreateBucketRequest body,
                ClusterState cluster,
                DistributedStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                if (!cluster.IsLeader)
                    return Results.Conflict();
                return Results.Ok(await storage.CreateBucketAsync(body.Name, body.ObjectLock, ct));
            }
        );

        // POST /_internal/buckets
        group.MapPost(
            "/buckets",
            async (
                HttpRequest request,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var body = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    StorageObjectJsonContext.Default.InternalCreateBucketRequest,
                    ct
                );
                if (body == null || string.IsNullOrEmpty(body.Name))
                    return Results.BadRequest();

                try
                {
                    var bucket = await storage.CreateBucketAsync(body.Name, body.ObjectLock, ct);
                    return Results.Ok(bucket);
                }
                catch (InvalidOperationException)
                {
                    // Bucket already exists — idempotent for cluster replication
                    var existing = await storage.GetBucketAsync(body.Name, ct);
                    return existing?.ObjectLock.Enabled == body.ObjectLock
                        ? Results.Ok(existing)
                        : Results.Conflict();
                }
            }
        );

        // DELETE /_internal/buckets/{bucketName}
        group.MapDelete(
            "/buckets/{bucketName}",
            async (string bucketName, LocalFileSystemStorageEngine storage, CancellationToken ct) =>
            {
                try
                {
                    await storage.DeleteBucketAsync(bucketName, ct);
                    return Results.Ok();
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict();
                }
            }
        );

        // POST /_internal/buckets/{bucketName}/versioning
        group.MapPost(
            "/buckets/{bucketName}/versioning",
            async (
                string bucketName,
                HttpRequest request,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var body = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    StorageObjectJsonContext.Default.InternalSetVersioningRequest,
                    ct
                );
                if (body == null || string.IsNullOrEmpty(body.Status))
                    return Results.BadRequest();

                if (!Enum.TryParse<VersioningStatus>(body.Status, true, out var status))
                    return Results.BadRequest();

                await storage.SetVersioningAsync(bucketName, status, ct);
                return Results.Ok();
            }
        );

        // POST /_internal/buckets/{bucketName}/object-lock
        group.MapPost(
            "/buckets/{bucketName}/object-lock",
            async (
                string bucketName,
                HttpRequest request,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var body = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    StorageObjectJsonContext.Default.InternalSetObjectLockConfigRequest,
                    ct
                );
                if (body == null)
                    return Results.BadRequest();

                var config = new ObjectLockConfig
                {
                    Enabled = body.Enabled,
                    DefaultRetentionMode = body.DefaultRetentionMode,
                    DefaultRetentionDays = body.DefaultRetentionDays,
                };
                await storage.SetObjectLockConfigAsync(bucketName, config, ct);
                return Results.Ok();
            }
        );

        // POST /_internal/objects/{bucket}/retention/{**key}
        group.MapPost(
            "/objects/{bucket}/retention/{**key}",
            async (
                string bucket,
                string key,
                string? versionId,
                HttpRequest request,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var body = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    StorageObjectJsonContext.Default.InternalSetObjectRetentionRequest,
                    ct
                );
                if (body == null)
                    return Results.BadRequest();

                await storage.SetObjectRetentionAsync(
                    bucket,
                    key,
                    versionId,
                    body.Mode,
                    body.RetainUntil,
                    ct
                );
                return Results.Ok();
            }
        );

        // POST /_internal/objects/{bucket}/legal-hold/{**key}
        group.MapPost(
            "/objects/{bucket}/legal-hold/{**key}",
            async (
                string bucket,
                string key,
                string? versionId,
                HttpRequest request,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var body = await JsonSerializer.DeserializeAsync(
                    request.Body,
                    StorageObjectJsonContext.Default.InternalSetObjectLegalHoldRequest,
                    ct
                );
                if (body == null)
                    return Results.BadRequest();

                await storage.SetObjectLegalHoldAsync(bucket, key, versionId, body.Hold, ct);
                return Results.Ok();
            }
        );

        // GET /_internal/health
        group.MapGet("/health", () => Results.Ok(new AdminHealthResponse("Healthy")));

        // GET /_internal/buckets/{bucketName}/objects
        group.MapGet(
            "/buckets/{bucketName}/objects",
            async (
                string bucketName,
                string? prefix,
                bool versions,
                LocalFileSystemStorageEngine storage,
                CancellationToken ct
            ) =>
            {
                var objects = await storage.ListObjectsAsync(bucketName, prefix, versions, ct);
                return Results.Ok(objects);
            }
        );
    }
}

public class ClusterTokenFilter(IOptions<ClusterOptions> options) : IEndpointFilter
{
    private readonly ClusterOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        if (string.IsNullOrEmpty(_options.ClusterToken))
        {
            // SEC: Critical - Do not allow internal calls if token is not configured
            return Results.Unauthorized();
        }

        var token = context.HttpContext.Request.Headers["X-DotBucket-Cluster-Token"].ToString();
        if (!FixedTimeEquals(token, _options.ClusterToken))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    /// <summary>
    /// Constant-time string comparison for secrets. Returns false for null/empty
    /// inputs without short-circuiting on length, to avoid leaking the configured
    /// token length via timing.
    /// </summary>
    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
