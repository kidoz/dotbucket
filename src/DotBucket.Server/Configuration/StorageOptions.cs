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
    /// If set, object/part writes are refused up-front when the storage filesystem
    /// has fewer than this many bytes free. Disabled when null (default). When both
    /// this and <see cref="MinFreeSpacePercent"/> are set, the stricter threshold wins.
    /// </summary>
    public long? MinFreeSpaceBytes { get; set; }

    /// <summary>
    /// Pre-write refusal and /health degradation threshold expressed as a
    /// percentage of total filesystem capacity. When set (e.g. 5.0), writes are
    /// refused below 5% free and /health returns 503 below 5% free. Default null
    /// (feature disabled) — opt in via appsettings.json or env var. Set to 0
    /// to explicitly disable. When both this and <see cref="MinFreeSpaceBytes"/>
    /// are set, the stricter threshold wins.
    /// </summary>
    public double? MinFreeSpacePercent { get; set; }

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
