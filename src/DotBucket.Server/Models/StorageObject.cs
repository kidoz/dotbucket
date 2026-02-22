// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Models;

/// <summary>
/// Represents an object stored within a bucket, containing metadata and the storage key.
/// </summary>
public record StorageObject
{
    public required string BucketName { get; init; }
    public required string ObjectKey { get; init; }
    public required long Size { get; init; }
    public required string ContentType { get; init; }
    public required string ETag { get; init; }
    public required DateTime LastModified { get; init; }

    // Versioning support
    public string VersionId { get; init; } = "null";
    public bool IsLatest { get; init; } = true;
    public bool IsDeleteMarker { get; init; } = false;
    public string? Encryption { get; init; }
    public ObjectLockStatus LockStatus { get; init; } = new();

    // Custom metadata mapping
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public record ObjectLockStatus
{
    public string? RetentionMode { get; init; }
    public DateTime? RetainUntilDate { get; init; }
    public bool LegalHold { get; init; }
}

public record MultipartUploadInfo
{
    public required string UploadId { get; init; }
    public required string BucketName { get; init; }
    public required string ObjectKey { get; init; }
    public required DateTime Initiated { get; init; }
}

public record PartInfo
{
    public required int PartNumber { get; init; }
    public required string ETag { get; init; }
    public required long Size { get; init; }
}
