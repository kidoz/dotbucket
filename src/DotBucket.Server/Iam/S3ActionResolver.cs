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
        var path = context.Request.Path.Value ?? "/";
        var query = context.Request.Query;

        // Parse path segments: /bucket or /bucket/key...
        var trimmed = path.TrimStart('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            // Service-level: GET / → ListAllMyBuckets
            if (method == "GET")
                return ("s3:ListAllMyBuckets", "arn:aws:s3:::*");
            return null;
        }

        var slashIndex = trimmed.IndexOf('/');
        string bucket;
        string? key = null;

        if (slashIndex < 0)
        {
            bucket = trimmed;
        }
        else
        {
            bucket = trimmed[..slashIndex];
            key = trimmed[(slashIndex + 1)..];
            if (string.IsNullOrEmpty(key))
                key = null;
        }

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
        var bucketArn = $"arn:aws:s3:::{bucket}";

        return method switch
        {
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

    /// <summary>
    /// Parses the x-amz-copy-source header to produce the source object ARN for CopyObject authorization.
    /// </summary>
    public static string? ResolveCopySource(HttpContext context)
    {
        var copySource = context.Request.Headers["x-amz-copy-source"].FirstOrDefault();
        if (string.IsNullOrEmpty(copySource))
            return null;

        // Strip leading slash
        var source = copySource.TrimStart('/');
        // Strip query string (e.g. ?versionId=...)
        var qIndex = source.IndexOf('?');
        if (qIndex >= 0)
            source = source[..qIndex];

        // Must contain at least bucket/key
        if (string.IsNullOrEmpty(source) || !source.Contains('/'))
            return null;

        return $"arn:aws:s3:::{source}";
    }
}
