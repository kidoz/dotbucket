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
}
