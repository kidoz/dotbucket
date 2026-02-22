// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Models;

public enum VersioningStatus
{
    Off = 0,
    Enabled = 1,
    Suspended = 2,
}

/// <summary>
/// Represents an S3-compatible bucket, which is a container for storage objects.
/// </summary>
public record Bucket
{
    public required string Name { get; init; }
    public required DateTime CreatedAt { get; init; }
    public VersioningStatus Versioning { get; init; } = VersioningStatus.Off;
    public ObjectLockConfig ObjectLock { get; init; } = new();
}

public record ObjectLockConfig
{
    public bool Enabled { get; init; }
    public string? DefaultRetentionMode { get; init; } // Governance or Compliance
    public int? DefaultRetentionDays { get; init; }
}
