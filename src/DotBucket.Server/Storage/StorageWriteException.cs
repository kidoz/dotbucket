// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Storage;

/// <summary>
/// Well-known classification codes for <see cref="StorageWriteException"/>.
/// </summary>
public static class StorageWriteErrorCodes
{
    /// <summary>
    /// The underlying write failed because the target filesystem is out of space
    /// (POSIX ENOSPC) or out of quota (POSIX EDQUOT). Mapped to HTTP 507.
    /// </summary>
    public const string DiskFull = "DiskFull";

    /// <summary>
    /// A pre-write free-space guard refused the request because available bytes
    /// fell below the configured threshold. Same wire behavior as
    /// <see cref="DiskFull"/> (the write never started) but distinct so operators
    /// can tell proactive rejection from a failed mid-write.
    /// </summary>
    public const string LowDiskSpace = "LowDiskSpace";

    /// <summary>
    /// Catch-all for IO failures during a write that are not ENOSPC/EDQUOT
    /// (e.g. permission denied, disk failure, file handle exhaustion).
    /// </summary>
    public const string IoError = "IoError";
}

/// <summary>
/// Thrown by storage write paths when an I/O failure prevents an object or
/// multipart part from being durably written. Carries a
/// <see cref="StorageWriteErrorCodes"/> so the S3 error mapper can return a
/// precise S3 code (HTTP 507 for disk-full, 500 for other IO errors) instead
/// of an opaque generic 500.
/// </summary>
public sealed class StorageWriteException(string code, string message, Exception inner)
    : Exception(message, inner)
{
    /// <summary>
    /// One of <see cref="StorageWriteErrorCodes"/>. Drives S3 wire behavior.
    /// </summary>
    public string Code { get; } = code;
}
