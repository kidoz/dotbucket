// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Storage;

public class DistributedStorageEngine(
    LocalFileSystemStorageEngine local,
    ClusterState cluster,
    DataPlacement placement,
    NodeClient nodeClient,
    IOptions<ClusterOptions> options,
    ILogger<DistributedStorageEngine> logger
) : IStorageEngine
{
    private readonly ClusterOptions _options = options.Value;

    // ========================================================================
    // Bucket metadata: forward to leader, leader fans out to all nodes
    // ========================================================================

    public async Task<Bucket> CreateBucketAsync(
        string bucketName,
        bool objectLock = false,
        CancellationToken cancellationToken = default
    )
    {
        if (cluster.IsLeader)
        {
            var bucket = await local.CreateBucketAsync(bucketName, objectLock, cancellationToken);
            var successCount = 1;

            var peers = cluster.AllNodes.Where(n => !n.IsSelf).ToList();
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    await nodeClient.CreateBucketAsync(peer.Address, bucketName, cancellationToken);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to replicate bucket creation to {NodeId}",
                        peer.NodeId
                    );
                    return false;
                }
            });
            var results = await Task.WhenAll(tasks);
            successCount += results.Count(r => r);

            var requiredQuorum = (cluster.AllNodes.Count / 2) + 1;
            if (successCount < requiredQuorum)
            {
                // Roll back local create to prevent divergence
                try
                {
                    await local.DeleteBucketAsync(bucketName, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to roll back local bucket creation for {Bucket}",
                        bucketName
                    );
                }

                throw new InvalidOperationException(
                    $"Bucket creation quorum not met: {successCount}/{requiredQuorum} nodes acknowledged."
                );
            }

            return bucket;
        }

        var leader = cluster.GetLeaderNode();
        var result = await nodeClient.CreateBucketAsync(
            leader.Address,
            bucketName,
            cancellationToken
        );
        return result ?? throw new InvalidOperationException("Leader failed to create bucket.");
    }

    public async Task SetObjectLockConfigAsync(
        string bucketName,
        ObjectLockConfig config,
        CancellationToken cancellationToken = default
    )
    {
        if (cluster.IsLeader)
        {
            await local.SetObjectLockConfigAsync(bucketName, config, cancellationToken);
            var successCount = 1;

            var peers = cluster.AllNodes.Where(n => !n.IsSelf).ToList();
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    await nodeClient.SetObjectLockConfigAsync(
                        peer.Address,
                        bucketName,
                        config,
                        cancellationToken
                    );
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to replicate object-lock config to {NodeId}",
                        peer.NodeId
                    );
                    return false;
                }
            });
            var results = await Task.WhenAll(tasks);
            successCount += results.Count(r => r);

            var requiredQuorum = (cluster.AllNodes.Count / 2) + 1;
            if (successCount < requiredQuorum)
            {
                throw new InvalidOperationException(
                    $"Set object-lock config quorum not met: {successCount}/{requiredQuorum} nodes acknowledged."
                );
            }
        }
        else
        {
            var leader = cluster.GetLeaderNode();
            await nodeClient.SetObjectLockConfigAsync(
                leader.Address,
                bucketName,
                config,
                cancellationToken
            );
        }
    }

    public async Task SetObjectRetentionAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        string mode,
        DateTime retainUntil,
        CancellationToken cancellationToken = default
    )
    {
        var preferenceList = placement.GetPreferenceList(bucketName, objectKey);
        var tasks = preferenceList.Select(async node =>
        {
            try
            {
                if (node.IsSelf)
                {
                    await local.SetObjectRetentionAsync(
                        bucketName,
                        objectKey,
                        versionId,
                        mode,
                        retainUntil,
                        cancellationToken
                    );
                }
                else
                {
                    await nodeClient.SetObjectRetentionAsync(
                        node.Address,
                        bucketName,
                        objectKey,
                        versionId,
                        mode,
                        retainUntil,
                        cancellationToken
                    );
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to set retention on {NodeId} for {Bucket}/{Key}",
                    node.NodeId,
                    bucketName,
                    objectKey
                );
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);
        if (successCount < _options.WriteQuorum)
        {
            throw new InvalidOperationException(
                $"Set retention quorum not met: {successCount}/{_options.WriteQuorum} nodes acknowledged."
            );
        }
    }

    public async Task SetObjectLegalHoldAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        bool hold,
        CancellationToken cancellationToken = default
    )
    {
        var preferenceList = placement.GetPreferenceList(bucketName, objectKey);
        var tasks = preferenceList.Select(async node =>
        {
            try
            {
                if (node.IsSelf)
                {
                    await local.SetObjectLegalHoldAsync(
                        bucketName,
                        objectKey,
                        versionId,
                        hold,
                        cancellationToken
                    );
                }
                else
                {
                    await nodeClient.SetObjectLegalHoldAsync(
                        node.Address,
                        bucketName,
                        objectKey,
                        versionId,
                        hold,
                        cancellationToken
                    );
                }
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to set legal hold on {NodeId} for {Bucket}/{Key}",
                    node.NodeId,
                    bucketName,
                    objectKey
                );
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);
        if (successCount < _options.WriteQuorum)
        {
            throw new InvalidOperationException(
                $"Set legal hold quorum not met: {successCount}/{_options.WriteQuorum} nodes acknowledged."
            );
        }
    }

    public async Task DeleteBucketAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    )
    {
        if (cluster.IsLeader)
        {
            // Replicate deletes to peers FIRST, only delete locally on quorum success
            // to prevent divergence where leader has deleted but peers haven't.
            var peers = cluster.AllNodes.Where(n => !n.IsSelf).ToList();
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    await nodeClient.DeleteBucketAsync(peer.Address, bucketName, cancellationToken);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to replicate bucket deletion to {NodeId}",
                        peer.NodeId
                    );
                    return false;
                }
            });
            var results = await Task.WhenAll(tasks);
            var successCount = results.Count(r => r);

            // Check quorum (peer successes + 1 for self, which we haven't committed yet)
            var requiredQuorum = (cluster.AllNodes.Count / 2) + 1;
            if (successCount + 1 < requiredQuorum)
            {
                throw new InvalidOperationException(
                    $"Bucket deletion quorum not met: {successCount + 1}/{requiredQuorum} nodes acknowledged."
                );
            }

            // Quorum achieved — commit locally
            await local.DeleteBucketAsync(bucketName, cancellationToken);
        }
        else
        {
            var leader = cluster.GetLeaderNode();
            await nodeClient.DeleteBucketAsync(leader.Address, bucketName, cancellationToken);
        }
    }

    public async Task SetVersioningAsync(
        string bucketName,
        VersioningStatus status,
        CancellationToken cancellationToken = default
    )
    {
        if (cluster.IsLeader)
        {
            // Capture previous state for rollback
            var bucket = await local.GetBucketAsync(bucketName, cancellationToken);
            var previousStatus = bucket?.Versioning ?? VersioningStatus.Off;

            await local.SetVersioningAsync(bucketName, status, cancellationToken);
            var successCount = 1;

            var peers = cluster.AllNodes.Where(n => !n.IsSelf).ToList();
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    await nodeClient.SetVersioningAsync(
                        peer.Address,
                        bucketName,
                        status.ToString(),
                        cancellationToken
                    );
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to replicate versioning to {NodeId}",
                        peer.NodeId
                    );
                    return false;
                }
            });
            var results = await Task.WhenAll(tasks);
            successCount += results.Count(r => r);

            var requiredQuorum = (cluster.AllNodes.Count / 2) + 1;
            if (successCount < requiredQuorum)
            {
                // Roll back local versioning change
                try
                {
                    await local.SetVersioningAsync(bucketName, previousStatus, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to roll back versioning for {Bucket}", bucketName);
                }

                throw new InvalidOperationException(
                    $"Set versioning quorum not met: {successCount}/{requiredQuorum} nodes acknowledged."
                );
            }
        }
        else
        {
            var leader = cluster.GetLeaderNode();
            await nodeClient.SetVersioningAsync(
                leader.Address,
                bucketName,
                status.ToString(),
                cancellationToken
            );
        }
    }

    public async Task SetNotificationsAsync(
        string bucketName,
        List<NotificationConfiguration> notifications,
        CancellationToken cancellationToken = default
    )
    {
        // Notifications are local-only: each node manages its own notification configs
        await local.SetNotificationsAsync(bucketName, notifications, cancellationToken);
    }

    // ========================================================================
    // Bucket reads: serve locally (all nodes have bucket metadata)
    // ========================================================================

    public Task<IEnumerable<Bucket>> ListBucketsAsync(
        CancellationToken cancellationToken = default
    ) => local.ListBucketsAsync(cancellationToken);

    public Task<bool> BucketExistsAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    ) => local.BucketExistsAsync(bucketName, cancellationToken);

    public Task<Bucket?> GetBucketAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    ) => local.GetBucketAsync(bucketName, cancellationToken);

    public Task<List<NotificationConfiguration>> GetNotificationsAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    ) => local.GetNotificationsAsync(bucketName, cancellationToken);

    // NOTE: Lifecycle configuration is node-local and is NOT replicated to peers (unlike
    // bucket existence/versioning/object-lock, which are replicated on write). A lifecycle
    // policy set on one node only applies on that node. Apply it on every node, or front the
    // cluster with sticky routing for configuration calls. See the cluster startup warning.
    public Task SetLifecycleAsync(
        string bucketName,
        LifecycleConfiguration config,
        CancellationToken cancellationToken = default
    ) => local.SetLifecycleAsync(bucketName, config, cancellationToken);

    public Task<LifecycleConfiguration?> GetLifecycleAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    ) => local.GetLifecycleAsync(bucketName, cancellationToken);

    public Task DeleteLifecycleAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    ) => local.DeleteLifecycleAsync(bucketName, cancellationToken);

    public Task<IEnumerable<(string Key, string VersionId)>> ListExpiredObjectsAsync(
        string bucketName,
        string? prefix,
        DateTime cutoffUtc,
        int limit,
        CancellationToken cancellationToken = default
    ) => local.ListExpiredObjectsAsync(bucketName, prefix, cutoffUtc, limit, cancellationToken);

    // ========================================================================
    // Object writes: write to preference list, wait for WriteQuorum acks
    // ========================================================================

    public async Task<StorageObject> PutObjectAsync(
        string bucketName,
        string objectKey,
        Stream content,
        string contentType,
        Dictionary<string, string>? metadata = null,
        string? encryption = null,
        CancellationToken cancellationToken = default
    )
    {
        var preferenceList = placement.GetPreferenceList(bucketName, objectKey);
        if (preferenceList.Count == 0)
            throw new InvalidOperationException("No healthy nodes available for write.");

        var primaryNode = preferenceList[0];

        // If we are not the primary node, proxy to the primary node
        if (!primaryNode.IsSelf)
        {
            var result = await nodeClient.PutObjectAsync(
                primaryNode.Address,
                bucketName,
                objectKey,
                content,
                contentType,
                metadata,
                cancellationToken,
                encryption: encryption
            );
            return result
                ?? throw new InvalidOperationException("Primary node failed to store object.");
        }

        // We are the primary node: save locally first
        var localResult = await local.PutObjectAsync(
            bucketName,
            objectKey,
            content,
            contentType,
            metadata,
            encryption,
            cancellationToken
        );

        var successCount = 1; // local write succeeded

        // Replicate to other nodes in the preference list and wait for quorum
        var replicaTargets = preferenceList.Skip(1).ToList();
        if (replicaTargets.Count > 0)
        {
            var replicaTasks = replicaTargets
                .Select(async target =>
                {
                    try
                    {
                        var replicaReadResult = await local.GetObjectAsync(
                            bucketName,
                            objectKey,
                            localResult.VersionId,
                            cancellationToken
                        );
                        if (replicaReadResult != null)
                        {
                            await using var replicaStream = replicaReadResult.Value.Content;
                            await nodeClient.PutObjectAsync(
                                target.Address,
                                bucketName,
                                objectKey,
                                replicaStream,
                                localResult.ContentType,
                                localResult.Metadata,
                                cancellationToken,
                                localResult.VersionId,
                                localResult.Encryption
                            );
                            return true;
                        }
                        return false;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Failed to replicate PUT {Bucket}/{Key} to {NodeId}",
                            bucketName,
                            objectKey,
                            target.NodeId
                        );
                        return false;
                    }
                })
                .ToList();

            var results = await Task.WhenAll(replicaTasks);
            successCount += results.Count(r => r);
        }

        if (successCount < _options.WriteQuorum)
        {
            logger.LogError(
                "Write quorum not met for PUT {Bucket}/{Key}: {Count}/{Quorum}",
                bucketName,
                objectKey,
                successCount,
                _options.WriteQuorum
            );

            // Roll back local write to prevent divergence
            try
            {
                await local.DeleteObjectAsync(
                    bucketName,
                    objectKey,
                    localResult.VersionId,
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to roll back local write for {Bucket}/{Key}",
                    bucketName,
                    objectKey
                );
            }

            throw new InvalidOperationException(
                $"Write quorum not met: {successCount}/{_options.WriteQuorum} nodes acknowledged the write."
            );
        }

        return localResult;
    }

    public async Task<bool> DeleteObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    )
    {
        var preferenceList = placement.GetPreferenceList(bucketName, objectKey);
        var isSelfInList = preferenceList.Any(n => n.IsSelf);

        // Replicate deletes to remote peers FIRST
        var remoteTasks = preferenceList
            .Where(n => !n.IsSelf)
            .Select(async node =>
            {
                try
                {
                    return await nodeClient.DeleteObjectAsync(
                        node.Address,
                        bucketName,
                        objectKey,
                        versionId,
                        cancellationToken
                    );
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to replicate DELETE {Bucket}/{Key} to {NodeId}",
                        bucketName,
                        objectKey,
                        node.NodeId
                    );
                    return false;
                }
            })
            .ToList();

        var results = await Task.WhenAll(remoteTasks);
        var remoteSuccessCount = results.Count(r => r);

        // Check if quorum is achievable (remote successes + self if in list)
        var totalPossible = remoteSuccessCount + (isSelfInList ? 1 : 0);
        if (totalPossible < _options.WriteQuorum)
        {
            logger.LogError(
                "Delete quorum not met for {Bucket}/{Key}: {Count}/{Quorum}",
                bucketName,
                objectKey,
                totalPossible,
                _options.WriteQuorum
            );
            throw new InvalidOperationException(
                $"Delete quorum not met: {totalPossible}/{_options.WriteQuorum} nodes acknowledged."
            );
        }

        // Quorum achievable — commit local delete
        if (isSelfInList)
        {
            await local.DeleteObjectAsync(bucketName, objectKey, versionId, cancellationToken);
        }

        return true;
    }

    public async Task<StorageObject> CopyObjectAsync(
        string srcBucket,
        string srcKey,
        string? srcVersionId,
        string destBucket,
        string destKey,
        Dictionary<string, string>? metadataOverride = null,
        CancellationToken cancellationToken = default
    )
    {
        // Read from source (distributed read path)
        var srcResult = await GetObjectAsync(srcBucket, srcKey, srcVersionId, cancellationToken);
        if (srcResult == null)
            throw new InvalidOperationException($"Source object '{srcBucket}/{srcKey}' not found.");

        var (srcMeta, srcContent) = srcResult.Value;
        await using (srcContent)
        {
            var metadata = metadataOverride ?? srcMeta.Metadata;
            return await PutObjectAsync(
                destBucket,
                destKey,
                srcContent,
                srcMeta.ContentType,
                metadata,
                srcMeta.Encryption,
                cancellationToken
            );
        }
    }

    public async Task<
        List<(string Key, bool Success, string? ErrorCode, string? ErrorMessage)>
    > DeleteObjectsAsync(
        string bucketName,
        IEnumerable<(string Key, string? VersionId)> objects,
        bool quiet,
        CancellationToken cancellationToken = default
    )
    {
        var results =
            new List<(string Key, bool Success, string? ErrorCode, string? ErrorMessage)>();
        foreach (var (key, versionId) in objects)
        {
            try
            {
                var deleted = await DeleteObjectAsync(
                    bucketName,
                    key,
                    versionId,
                    cancellationToken
                );
                if (deleted)
                {
                    results.Add((key, true, null, null));
                }
                else
                {
                    results.Add((key, false, "InternalError", "Delete failed on all replicas."));
                }
            }
            catch (Exception ex)
            {
                results.Add((key, false, "InternalError", ex.Message));
            }
        }
        return results;
    }

    // ========================================================================
    // Object reads: local-first if owner, else proxy to preference list
    // ========================================================================

    public async Task<StorageObject?> HeadObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    )
    {
        // Try local first if we're in the preference list
        if (placement.IsOwner(bucketName, objectKey))
        {
            var localResult = await local.HeadObjectAsync(
                bucketName,
                objectKey,
                versionId,
                cancellationToken
            );
            if (localResult != null)
                return localResult;
        }

        // Proxy to preference list nodes
        var preferenceList = placement.GetPreferenceList(bucketName, objectKey);
        foreach (var node in preferenceList.Where(n => !n.IsSelf))
        {
            try
            {
                var result = await nodeClient.HeadObjectAsync(
                    node.Address,
                    bucketName,
                    objectKey,
                    versionId,
                    cancellationToken
                );
                if (result != null)
                    return result;
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "HEAD failed on {NodeId} for {Bucket}/{Key}",
                    node.NodeId,
                    bucketName,
                    objectKey
                );
            }
        }

        return null;
    }

    public async Task<(StorageObject Metadata, Stream Content)?> GetObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    )
    {
        // Try local first if we're in the preference list
        if (placement.IsOwner(bucketName, objectKey))
        {
            var localResult = await local.GetObjectAsync(
                bucketName,
                objectKey,
                versionId,
                cancellationToken
            );
            if (localResult != null)
                return localResult;
        }

        // Proxy to preference list nodes
        var preferenceList = placement.GetPreferenceList(bucketName, objectKey);
        foreach (var node in preferenceList.Where(n => !n.IsSelf))
        {
            try
            {
                var result = await nodeClient.GetObjectAsync(
                    node.Address,
                    bucketName,
                    objectKey,
                    versionId,
                    cancellationToken
                );
                if (result != null)
                    return result;
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "GET failed on {NodeId} for {Bucket}/{Key}",
                    node.NodeId,
                    bucketName,
                    objectKey
                );
            }
        }

        return null;
    }

    // ========================================================================
    // Object listing: fan out to all nodes, merge & deduplicate
    // ========================================================================

    public async Task<IEnumerable<StorageObject>> ListObjectsAsync(
        string bucketName,
        string? prefix = null,
        bool versions = false,
        CancellationToken cancellationToken = default
    )
    {
        var allResults = new List<StorageObject>();
        var tasks = cluster.AllNodes.Select(async node =>
        {
            try
            {
                if (node.IsSelf)
                {
                    return (
                        await local.ListObjectsAsync(
                            bucketName,
                            prefix,
                            versions,
                            cancellationToken
                        )
                    ).ToList();
                }
                else
                {
                    return (
                        await nodeClient.ListObjectsAsync(
                            node.Address,
                            bucketName,
                            prefix,
                            versions,
                            cancellationToken
                        )
                    ).ToList();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to list objects from node {NodeId}", node.NodeId);
                return new List<StorageObject>();
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var resultSet in results)
        {
            allResults.AddRange(resultSet);
        }

        // Deduplicate by (bucket, key, versionId) and sort
        return allResults
            .GroupBy(o => (o.BucketName, o.ObjectKey, o.VersionId))
            .Select(g => g.First())
            .OrderBy(o => o.ObjectKey)
            .ThenByDescending(o => o.LastModified);
    }

    public async Task<(
        IEnumerable<StorageObject> Objects,
        string? NextContinuationToken,
        bool IsTruncated
    )> ListObjectsPagedAsync(
        string bucketName,
        string? prefix = null,
        string? continuationToken = null,
        string? startAfter = null,
        int maxKeys = 1000,
        CancellationToken cancellationToken = default
    )
    {
        var allObjects = (
            await ListObjectsAsync(bucketName, prefix, versions: false, cancellationToken)
        ).ToList();

        string? cursorKey = startAfter;
        if (continuationToken != null)
        {
            try
            {
                cursorKey = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(continuationToken)
                );
            }
            catch
            {
                cursorKey = null;
            }
        }

        var filtered = string.IsNullOrEmpty(cursorKey)
            ? allObjects
            : allObjects.Where(o => string.CompareOrdinal(o.ObjectKey, cursorKey) > 0).ToList();

        var page = filtered.Take(maxKeys + 1).ToList();
        var isTruncated = page.Count > maxKeys;
        if (isTruncated)
        {
            page.RemoveAt(page.Count - 1);
        }

        string? nextToken = null;
        if (isTruncated && page.Count > 0)
        {
            nextToken = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(page[^1].ObjectKey)
            );
        }

        return (page, nextToken, isTruncated);
    }

    // ========================================================================
    // Multipart uploads are NODE-LOCAL until completion.
    //
    // Initiate, every UploadPart, and Complete must all be served by the SAME node: the upload
    // record and the staged parts live only on the node that received them — they are NOT
    // replicated. Behind a load balancer this REQUIRES session affinity (sticky routing) keyed
    // on the bucket/key or uploadId; without it, parts scatter across nodes and Complete fails
    // with InvalidPart. Only the FINAL assembled object is replicated (see Complete below).
    // ========================================================================

    public async Task<string> InitiateMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string contentType,
        Dictionary<string, string>? metadata = null,
        string? encryption = null,
        CancellationToken cancellationToken = default
    )
    {
        // Staged node-locally; the caller must route all subsequent part/complete calls for
        // this uploadId back to this same node (see the section comment above).
        return await local.InitiateMultipartUploadAsync(
            bucketName,
            objectKey,
            contentType,
            metadata,
            encryption,
            cancellationToken
        );
    }

    public async Task<string> UploadPartAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default
    )
    {
        return await local.UploadPartAsync(
            bucketName,
            objectKey,
            uploadId,
            partNumber,
            content,
            cancellationToken
        );
    }

    public async Task<StorageObject> CompleteMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        IEnumerable<(int PartNumber, string ETag)> parts,
        CancellationToken cancellationToken = default
    )
    {
        // Complete locally first
        var result = await local.CompleteMultipartUploadAsync(
            bucketName,
            objectKey,
            uploadId,
            parts,
            cancellationToken
        );

        // Then replicate the completed object to other nodes in the preference list
        var replicaTargets = placement.GetReplicaTargets(bucketName, objectKey);
        if (replicaTargets.Count <= 0)
        {
            return result;
        }

        var localResult = await local.GetObjectAsync(
            bucketName,
            objectKey,
            result.VersionId,
            cancellationToken
        );
        if (localResult == null)
        {
            return result;
        }

        var (meta, content) = localResult.Value;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        await content.DisposeAsync();

        var successCount = 1; // local complete succeeded
        var tasks = replicaTargets.Select(async node =>
        {
            try
            {
                var ms = new MemoryStream(buffer.ToArray());
                await nodeClient.PutObjectAsync(
                    node.Address,
                    bucketName,
                    objectKey,
                    ms,
                    meta.ContentType,
                    meta.Metadata,
                    cancellationToken,
                    result.VersionId,
                    meta.Encryption
                );
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to replicate completed multipart to {NodeId}",
                    node.NodeId
                );
                return false;
            }
        });
        var replicationResults = await Task.WhenAll(tasks);
        successCount += replicationResults.Count(r => r);

        if (successCount < _options.WriteQuorum)
        {
            logger.LogError(
                "Write quorum not met for completed multipart {Bucket}/{Key}: {Count}/{Quorum}",
                bucketName,
                objectKey,
                successCount,
                _options.WriteQuorum
            );

            try
            {
                await local.DeleteObjectAsync(
                    bucketName,
                    objectKey,
                    result.VersionId,
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to roll back completed multipart object for {Bucket}/{Key}",
                    bucketName,
                    objectKey
                );
            }

            throw new InvalidOperationException(
                $"Write quorum not met: {successCount}/{_options.WriteQuorum} nodes acknowledged completed multipart upload."
            );
        }

        return result;
    }

    public Task AbortMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    ) => local.AbortMultipartUploadAsync(bucketName, objectKey, uploadId, cancellationToken);

    public Task<IEnumerable<MultipartUploadInfo>> ListMultipartUploadsAsync(
        string bucketName,
        string? prefix = null,
        CancellationToken cancellationToken = default
    ) => local.ListMultipartUploadsAsync(bucketName, prefix, cancellationToken);

    public Task<IEnumerable<PartInfo>> ListPartsAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    ) => local.ListPartsAsync(bucketName, objectKey, uploadId, cancellationToken);
}
