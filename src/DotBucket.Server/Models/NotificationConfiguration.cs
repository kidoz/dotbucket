// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Models;

public record NotificationConfiguration
{
    public required string Id { get; init; }
    public required string WebhookUrl { get; init; }
    public required List<string> Events { get; init; } = new();
    public string? FilterPrefix { get; init; }
}

public record S3Event
{
    public string EventName { get; init; } = string.Empty;
    public string EventTime { get; init; } = DateTime.UtcNow.ToString("O");
    public string BucketName { get; init; } = string.Empty;
    public string ObjectKey { get; init; } = string.Empty;
    public long Size { get; init; }
    public string ETag { get; init; } = string.Empty;
    public string VersionId { get; init; } = "null";
}
