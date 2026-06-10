// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Models;

/// <summary>
/// A bucket lifecycle configuration: an ordered set of expiration rules.
/// </summary>
public record LifecycleConfiguration
{
    public List<LifecycleRule> Rules { get; init; } = new();
}

/// <summary>
/// A single lifecycle rule. Only object Expiration (by Days or Date) is supported.
/// </summary>
public record LifecycleRule
{
    public string? Id { get; init; }

    /// <summary>Key prefix the rule applies to (empty/null = whole bucket).</summary>
    public string? Prefix { get; init; }

    /// <summary>True when Status == "Enabled".</summary>
    public bool Enabled { get; init; }

    /// <summary>Expire objects older than this many days after creation.</summary>
    public int? ExpirationDays { get; init; }

    /// <summary>Expire objects on/after this UTC date (midnight).</summary>
    public DateTime? ExpirationDate { get; init; }
}
