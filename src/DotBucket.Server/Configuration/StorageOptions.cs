// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

/// <summary>
/// Configuration options for the local filesystem storage engine.
/// </summary>
public class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// The root directory where all buckets and objects will be stored.
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Base64 encoded 32-byte master key for SSE-S3 encryption.
    /// </summary>
    public string MasterKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional storage-layout prefix. When set, all bucket data is stored under
    /// "{RootPath}/{BasePrefix}/...". The metadata database remains at the RootPath
    /// root. Useful for namespacing multiple tenants/instances under a shared root.
    /// </summary>
    public string BasePrefix { get; set; } = string.Empty;

    /// <summary>
    /// Buffer size, in bytes, used when streaming multipart upload parts to and from
    /// disk. Larger values improve throughput for large parts. Defaults to 80 KiB.
    /// </summary>
    public int MultipartBufferSizeBytes { get; set; } = 81920;

    /// <summary>
    /// Buckets to create automatically at startup (idempotent provisioning).
    /// </summary>
    public List<BucketSeed> Buckets { get; set; } = new();

    /// <summary>
    /// Declarative description of a bucket to provision at startup.
    /// </summary>
    public class BucketSeed
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional versioning state to apply: "Enabled" or "Suspended".
        /// </summary>
        public string? Versioning { get; set; }

        /// <summary>
        /// When true, the bucket is created with Object Lock enabled.
        /// </summary>
        public bool ObjectLock { get; set; }
    }
}
