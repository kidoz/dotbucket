// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

/// <summary>
/// Configuration options for the background lifecycle/expiration service.
/// </summary>
public class LifecycleOptions
{
    public const string SectionName = "Lifecycle";

    /// <summary>
    /// When true, the background expiration service scans buckets and deletes
    /// expired objects according to their lifecycle rules. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval, in seconds, between expiration scan passes. Default 3600 (1 hour).
    /// </summary>
    public int ScanIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of objects deleted per bucket+rule per query during a scan
    /// pass. The pass re-queries until a batch is smaller than this. Default 500.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// When true, the multipart-upload reaper periodically aborts in-progress
    /// uploads older than <see cref="AbortedMultipartUploadRetentionDays"/>.
    /// Closes the disk-leak vector where a client initiates uploads and never
    /// completes or aborts them (each abandoned upload holds a parts directory
    /// on disk with no TTL). Default true.
    /// </summary>
    public bool ReaperEnabled { get; set; } = true;

    /// <summary>
    /// In-progress multipart uploads older than this many days are aborted by
    /// the reaper. Default 1. Set to 0 to abort every in-progress upload on
    /// each scan (useful for tests).
    /// </summary>
    public int AbortedMultipartUploadRetentionDays { get; set; } = 1;
}
