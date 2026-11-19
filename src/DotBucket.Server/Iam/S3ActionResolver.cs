// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Iam;

/// <summary>
/// Resolves an incoming HTTP request to an S3 action and ARN resource.
/// </summary>
public static class S3ActionResolver
{
    /// <summary>
    /// Returns (Action, Resource) for the given request, or null if the path cannot be mapped.
    /// </summary>
    public static (string Action, string Resource)? Resolve(HttpContext context)
    {
        var method = context.Request.Method;
        var query = context.Request.Query;
        if (context.Request.RouteValues["bucket"] is not string bucket)
            return method == "GET" && context.Request.Path == "/"
                ? ("s3:ListAllMyBuckets", "arn:aws:s3:::*")
                : null;
        var key = context.Request.RouteValues["key"] as string;

        // Object-level operations (key is present)
        if (key != null)
        {
            var objectArn = $"arn:aws:s3:::{bucket}/{key}";

            return method switch
            {
                "PUT" when query.ContainsKey("retention") => ("s3:PutObjectRetention", objectArn),
                "PUT" when query.ContainsKey("legal-hold") => ("s3:PutObjectLegalHold", objectArn),
                "PUT" => ("s3:PutObject", objectArn),
                "POST" => ("s3:PutObject", objectArn),
                "HEAD" => ("s3:GetObject", objectArn),
                "GET" when query.ContainsKey("uploadId") => (
                    "s3:ListMultipartUploadParts",
                    objectArn
                ),
                "GET" => ("s3:GetObject", objectArn),
                "DELETE" when query.ContainsKey("uploadId") => (
                    "s3:AbortMultipartUpload",
                    objectArn
                ),
                "DELETE" => ("s3:DeleteObject", objectArn),
                _ => null,
            };
        }

        // Bucket-level operations
        // A bucket request can select only one operation. Reject conflicting selectors
        // before dispatch so middleware and handlers cannot select different actions.
        if (BucketSubresources.Count(query.ContainsKey) > 1)
            return null;

        var bucketArn = $"arn:aws:s3:::{bucket}";

        return method switch
        {
            "PUT" or "DELETE" when query.ContainsKey("lifecycle") => (
                "s3:PutLifecycleConfiguration",
                bucketArn
            ),
            "GET" when query.ContainsKey("lifecycle") => (
                "s3:GetLifecycleConfiguration",
                bucketArn
            ),
            "GET" when query.ContainsKey("location") => ("s3:GetBucketLocation", bucketArn),
            "PUT" when query.ContainsKey("versioning") => ("s3:PutBucketVersioning", bucketArn),
            "PUT" when query.ContainsKey("notification") => ("s3:PutBucketNotification", bucketArn),
            "PUT" when query.ContainsKey("object-lock") => (
                "s3:PutObjectLockConfiguration",
                bucketArn
            ),
            "PUT" => ("s3:CreateBucket", bucketArn),
            "HEAD" => ("s3:ListBucket", bucketArn),
            "DELETE" => ("s3:DeleteBucket", bucketArn),
            "GET" when query.ContainsKey("versioning") => ("s3:GetBucketVersioning", bucketArn),
            "GET" when query.ContainsKey("notification") => ("s3:GetBucketNotification", bucketArn),
            "GET" when query.ContainsKey("object-lock") => (
                "s3:GetObjectLockConfiguration",
                bucketArn
            ),
            "GET" when query.ContainsKey("uploads") => ("s3:ListBucketMultipartUploads", bucketArn),
            "GET" => ("s3:ListBucket", bucketArn),
            "POST" when query.ContainsKey("delete") => ("s3:DeleteObject", bucketArn),
            _ => null,
        };
    }

    private static readonly string[] BucketSubresources =
    [
        "versioning",
        "notification",
        "object-lock",
        "uploads",
        "versions",
        "location",
        "lifecycle",
        "delete",
    ];

    internal sealed record CopySource(string Bucket, string Key, string? VersionId);

    internal static CopySource? ParseCopySource(HttpRequest request)
    {
        var values = request.Headers["x-amz-copy-source"];
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
            return null;
        var raw = values[0]!;
        var queryStart = raw.IndexOf('?');
        string? version = null;
        if (queryStart >= 0)
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(
                raw[queryStart..]
            );
            if (
                query.Count != 1
                || !query.TryGetValue("versionId", out var ids)
                || ids.Count != 1
                || string.IsNullOrEmpty(ids[0])
            )
                return null;
            version = ids[0];
            raw = raw[..queryStart];
        }
        var path = Uri.UnescapeDataString(raw);
        if (path.StartsWith('/'))
            path = path[1..];
        var slash = path.IndexOf('/');
        return slash <= 0 || slash == path.Length - 1
            ? null
            : new CopySource(path[..slash], path[(slash + 1)..], version);
    }
}
