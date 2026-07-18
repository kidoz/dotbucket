// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Text.RegularExpressions;
using DotBucket.Server.Auth;
using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Endpoints.Admin;

public static class AdminEndpoints
{
    private static readonly Regex _bucketNameRegex = new(
        @"^(?!xn--)(?!.*-s3alias$)[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$",
        RegexOptions.Compiled
    );

    private static bool IsValidBucketName(string name)
    {
        if (name.Length < 3 || name.Length > 63)
            return false;
        if (!_bucketNameRegex.IsMatch(name))
            return false;
        if (name.Contains(".."))
            return false;
        // No IP-format check: a.b.c.d where all are digits
        var parts = name.Split('.');
        if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
            return false;
        return true;
    }

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .WithTags("Admin API")
            .AddEndpointFilter<AdminTokenEndpointFilter>()
            .RequireRateLimiting("admin");

        // List Buckets
        group.MapGet(
            "/buckets",
            async (IStorageEngine storageEngine, CancellationToken cancellationToken) =>
            {
                var buckets = await storageEngine.ListBucketsAsync(cancellationToken);
                return Results.Ok(buckets);
            }
        );

        // Create Bucket
        group.MapPost(
            "/buckets",
            async (
                CreateBucketRequest request,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || !IsValidBucketName(request.Name))
                {
                    return Results.BadRequest(
                        "Bucket name must be 3-63 characters, lowercase, and contain only letters, numbers, dots, or hyphens. No leading/trailing dots or hyphens, no consecutive dots, no IP format, no xn-- prefix, no -s3alias suffix."
                    );
                }

                try
                {
                    var bucket = await storageEngine.CreateBucketAsync(
                        request.Name,
                        false,
                        cancellationToken
                    );
                    return Results.Ok(bucket);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Conflict(ex.Message);
                }
            }
        );

        // Delete Bucket
        group.MapDelete(
            "/buckets/{bucketName}",
            async (
                string bucketName,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                if (!await storageEngine.BucketExistsAsync(bucketName, cancellationToken))
                {
                    return Results.NotFound($"Bucket '{bucketName}' not found.");
                }

                try
                {
                    await storageEngine.DeleteBucketAsync(bucketName, cancellationToken);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex) when (ex.Message == "BucketNotEmpty")
                {
                    return Results.Conflict("Bucket is not empty. Delete all objects first.");
                }
            }
        );

        // List Objects (paginated)
        group.MapGet(
            "/buckets/{bucketName}/objects",
            async (
                string bucketName,
                bool versions,
                int page,
                int pageSize,
                string? prefix,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                if (!await storageEngine.BucketExistsAsync(bucketName, cancellationToken))
                {
                    return Results.NotFound($"Bucket '{bucketName}' not found.");
                }

                if (page < 1)
                    page = 1;
                if (pageSize < 1)
                    pageSize = 50;
                if (pageSize > 1000)
                    pageSize = 1000;

                var allObjects = await storageEngine.ListObjectsAsync(
                    bucketName,
                    prefix,
                    versions,
                    cancellationToken
                );
                var objectList = allObjects.ToList();
                var totalCount = objectList.Count;
                var pagedObjects = objectList.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                return Results.Ok(new AdminObjectListResponse(pagedObjects, totalCount));
            }
        );

        // Get Bucket Versioning
        group.MapGet(
            "/buckets/{bucketName}/versioning",
            async (
                string bucketName,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                var bucket = await storageEngine.GetBucketAsync(bucketName, cancellationToken);
                if (bucket == null)
                    return Results.NotFound();
                return Results.Ok(new AdminVersioningResponse(bucket.Versioning.ToString()));
            }
        );

        // Set Bucket Versioning
        group.MapPost(
            "/buckets/{bucketName}/versioning",
            async (
                string bucketName,
                SetVersioningRequest request,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                if (!Enum.TryParse<VersioningStatus>(request.Status, true, out var status))
                {
                    return Results.BadRequest(
                        "Invalid versioning status. Use Off, Enabled, or Suspended."
                    );
                }

                await storageEngine.SetVersioningAsync(bucketName, status, cancellationToken);
                return Results.NoContent();
            }
        );

        // Get Bucket Notifications
        group.MapGet(
            "/buckets/{bucketName}/notifications",
            async (
                string bucketName,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                var configs = await storageEngine.GetNotificationsAsync(
                    bucketName,
                    cancellationToken
                );
                return Results.Ok(configs);
            }
        );

        // Set Bucket Notifications
        group.MapPost(
            "/buckets/{bucketName}/notifications",
            async (
                string bucketName,
                List<NotificationConfiguration> request,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                await storageEngine.SetNotificationsAsync(bucketName, request, cancellationToken);
                return Results.NoContent();
            }
        );

        // Upload Object (Admin)
        group
            .MapPost(
                "/buckets/{bucketName}/upload",
                async (
                    string bucketName,
                    IFormFile file,
                    IStorageEngine storageEngine,
                    CancellationToken cancellationToken
                ) =>
                {
                    if (!await storageEngine.BucketExistsAsync(bucketName, cancellationToken))
                    {
                        return Results.NotFound($"Bucket '{bucketName}' not found.");
                    }

                    using var stream = file.OpenReadStream();
                    var obj = await storageEngine.PutObjectAsync(
                        bucketName,
                        file.FileName,
                        stream,
                        file.ContentType ?? "application/octet-stream",
                        null,
                        null,
                        cancellationToken
                    );

                    return Results.Ok(obj);
                }
            )
            .DisableAntiforgery(); // Simple upload for Admin Dashboard

        // Download Object (Admin)
        group.MapGet(
            "/buckets/{bucketName}/download/{**objectKey}",
            async (
                string bucketName,
                string objectKey,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                var result = await storageEngine.GetObjectAsync(
                    bucketName,
                    objectKey,
                    cancellationToken: cancellationToken
                );
                if (result == null)
                {
                    return Results.NotFound(
                        $"Object '{objectKey}' not found in bucket '{bucketName}'."
                    );
                }

                var (metadata, content) = result.Value;
                return Results.Stream(content, metadata.ContentType, objectKey.Split('/').Last());
            }
        );

        // Delete Object
        group.MapDelete(
            "/buckets/{bucketName}/objects/{**objectKey}",
            async (
                string bucketName,
                string objectKey,
                string? versionId,
                IStorageEngine storageEngine,
                CancellationToken cancellationToken
            ) =>
            {
                var deleted = await storageEngine.DeleteObjectAsync(
                    bucketName,
                    objectKey,
                    versionId,
                    cancellationToken
                );
                if (deleted)
                {
                    return Results.NoContent();
                }
                return Results.NotFound();
            }
        );

        // Cluster Status
        group.MapGet(
            "/cluster",
            (ClusterState clusterState, IOptions<ClusterOptions> clusterOptions) =>
            {
                var opts = clusterOptions.Value;
                if (!opts.Enabled)
                {
                    return Results.Ok(new ClusterStatusResponse { Enabled = false });
                }

                var nodes = clusterState
                    .AllNodes.Select(n =>
                    {
                        var health = clusterState.GetNodeHealth(n.NodeId);
                        return new ClusterNodeStatusResponse
                        {
                            NodeId = n.NodeId,
                            Address = n.Address,
                            IsSelf = n.IsSelf,
                            IsLeader = n.NodeId == clusterState.LeaderNodeId,
                            Status = health.Status.ToString(),
                            LastSeen = health.LastSeen.ToString("O"),
                        };
                    })
                    .ToList();

                return Results.Ok(
                    new ClusterStatusResponse
                    {
                        Enabled = true,
                        SelfNodeId = clusterState.SelfNodeId,
                        LeaderNodeId = clusterState.LeaderNodeId,
                        ReplicationFactor = opts.ReplicationFactor,
                        WriteQuorum = opts.WriteQuorum,
                        ReadQuorum = opts.ReadQuorum,
                        Nodes = nodes,
                    }
                );
            }
        );
    }
}
