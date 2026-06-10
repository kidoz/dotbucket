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
}
