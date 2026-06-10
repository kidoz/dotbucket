// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Models;

namespace DotBucket.Server.Storage;

public interface IStorageEngine
{
    // Bucket Operations
    Task<Bucket> CreateBucketAsync(
        string bucketName,
        bool objectLock = false,
        CancellationToken cancellationToken = default
    );
    Task<IEnumerable<Bucket>> ListBucketsAsync(CancellationToken cancellationToken = default);
    Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default);
    Task<Bucket?> GetBucketAsync(string bucketName, CancellationToken cancellationToken = default);
    Task DeleteBucketAsync(string bucketName, CancellationToken cancellationToken = default);

    // Versioning Operations
    Task SetVersioningAsync(
        string bucketName,
        VersioningStatus status,
        CancellationToken cancellationToken = default
    );

    // Object Locking Operations
    Task SetObjectLockConfigAsync(
        string bucketName,
        ObjectLockConfig config,
        CancellationToken cancellationToken = default
    );
    Task SetObjectRetentionAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        string mode,
        DateTime retainUntil,
        CancellationToken cancellationToken = default
    );
    Task SetObjectLegalHoldAsync(
        string bucketName,
        string objectKey,
        string? versionId,
        bool hold,
        CancellationToken cancellationToken = default
    );

    // Notification Operations
    Task SetNotificationsAsync(
        string bucketName,
        List<NotificationConfiguration> notifications,
        CancellationToken cancellationToken = default
    );
    Task<List<NotificationConfiguration>> GetNotificationsAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    );

    // Lifecycle Operations
    Task SetLifecycleAsync(
        string bucketName,
        LifecycleConfiguration config,
        CancellationToken cancellationToken = default
    );
    Task<LifecycleConfiguration?> GetLifecycleAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    );
    Task DeleteLifecycleAsync(
        string bucketName,
        CancellationToken cancellationToken = default
    );
    Task<IEnumerable<(string Key, string VersionId)>> ListExpiredObjectsAsync(
        string bucketName,
        string? prefix,
        DateTime cutoffUtc,
        int limit,
        CancellationToken cancellationToken = default
    );

    // Object Operations
    Task<StorageObject> PutObjectAsync(
        string bucketName,
        string objectKey,
        Stream content,
        string contentType,
        Dictionary<string, string>? metadata = null,
        string? encryption = null,
        CancellationToken cancellationToken = default
    );
    Task<StorageObject?> HeadObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    );
    Task<(StorageObject Metadata, Stream Content)?> GetObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    );
    Task<bool> DeleteObjectAsync(
        string bucketName,
        string objectKey,
        string? versionId = null,
        CancellationToken cancellationToken = default
    );
    Task<StorageObject> CopyObjectAsync(
        string srcBucket,
        string srcKey,
        string? srcVersionId,
        string destBucket,
        string destKey,
        Dictionary<string, string>? metadataOverride = null,
        CancellationToken cancellationToken = default
    );
    Task<
        List<(string Key, bool Success, string? ErrorCode, string? ErrorMessage)>
    > DeleteObjectsAsync(
        string bucketName,
        IEnumerable<(string Key, string? VersionId)> objects,
        bool quiet,
        CancellationToken cancellationToken = default
    );

    // Listing Objects
    Task<IEnumerable<StorageObject>> ListObjectsAsync(
        string bucketName,
        string? prefix = null,
        bool versions = false,
        CancellationToken cancellationToken = default
    );
    Task<(
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
    );

    // Multipart Operations
    Task<string> InitiateMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string contentType,
        Dictionary<string, string>? metadata = null,
        string? encryption = null,
        CancellationToken cancellationToken = default
    );
    Task<string> UploadPartAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default
    );
    Task<StorageObject> CompleteMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        IEnumerable<(int PartNumber, string ETag)> parts,
        CancellationToken cancellationToken = default
    );
    Task AbortMultipartUploadAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    );
    Task<IEnumerable<MultipartUploadInfo>> ListMultipartUploadsAsync(
        string bucketName,
        string? prefix = null,
        CancellationToken cancellationToken = default
    );
    Task<IEnumerable<PartInfo>> ListPartsAsync(
        string bucketName,
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default
    );
}
