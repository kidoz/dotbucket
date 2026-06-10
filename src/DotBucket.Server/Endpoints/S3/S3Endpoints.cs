// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DotBucket.Server.Middleware;
using DotBucket.Server.Models;
using DotBucket.Server.Storage;

namespace DotBucket.Server.Endpoints.S3;

public static class S3Endpoints
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly HashSet<string> ReservedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "health",
        "_internal",
        "openapi",
        "assets",
        "favicon.ico",
        "robots.txt",
        "dotbucket-logo.svg",
    };

    private static bool IsReservedBucketName(string bucket) => ReservedPrefixes.Contains(bucket);

    /// <summary>
    /// Exposes the reserved-bucket-name check for reuse (e.g. virtual-host routing).
    /// </summary>
    internal static bool IsReservedBucketNamePublic(string bucket) => IsReservedBucketName(bucket);

    private static async Task<IResult> WriteInvalidSseHeaderAsync(HttpContext context)
    {
        await S3ErrorResponses.WriteErrorAsync(
            context,
            400,
            "InvalidArgument",
            "Unsupported x-amz-server-side-encryption value. Only AES256 is supported."
        );
        return Results.Empty;
    }

    private static bool TryGetSseAlgorithm(HttpRequest request, out string? algorithm)
    {
        var sseHeader = request.Headers["x-amz-server-side-encryption"].ToString();
        if (string.IsNullOrEmpty(sseHeader))
        {
            algorithm = null;
            return true;
        }

        if (!string.Equals(sseHeader, "AES256", StringComparison.Ordinal))
        {
            algorithm = null;
            return false;
        }

        algorithm = sseHeader;
        return true;
    }

    private static async Task SkipBytesAsync(
        Stream stream,
        long bytesToSkip,
        CancellationToken cancellationToken
    )
    {
        if (bytesToSkip <= 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long remaining = bytesToSkip;
            while (remaining > 0)
            {
                var chunkSize = (int)Math.Min(buffer.Length, remaining);
                var read = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream while applying range.");
                }

                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static void MapS3Endpoints(this IEndpointRouteBuilder app)
    {
        // All S3 endpoints require authentication via the S3AuthRequiredFilter.
        // This is the final defense layer — even if middleware is bypassed or misconfigured,
        // no S3 endpoint will serve content without a validated AccessKey in context.
        var s3 = app.MapGroup("").AddEndpointFilter<S3AuthRequiredFilter>();

        s3.MapPut(
            "/{bucket}",
            async (string bucket, IStorageEngine storageEngine, HttpContext context) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                if (context.Request.Query.ContainsKey("versioning"))
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var xml = await reader.ReadToEndAsync(context.RequestAborted);
                    var doc = XDocument.Parse(xml);
                    var statusStr = doc.Root?.Element(doc.Root.Name.Namespace + "Status")?.Value;

                    var status = statusStr switch
                    {
                        "Enabled" => VersioningStatus.Enabled,
                        "Suspended" => VersioningStatus.Suspended,
                        _ => VersioningStatus.Off,
                    };

                    await storageEngine.SetVersioningAsync(bucket, status, context.RequestAborted);
                    return Results.Ok();
                }

                if (context.Request.Query.ContainsKey("object-lock"))
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var xml = await reader.ReadToEndAsync(context.RequestAborted);
                    var doc = XDocument.Parse(xml);
                    var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                    var enabled = doc.Root?.Element(ns + "ObjectLockEnabled")?.Value == "Enabled";
                    var rule = doc.Root?.Element(ns + "Rule")?.Element(ns + "DefaultRetention");

                    var config = new ObjectLockConfig
                    {
                        Enabled = enabled,
                        DefaultRetentionMode = rule?.Element(ns + "Mode")?.Value,
                        DefaultRetentionDays = int.TryParse(
                            rule?.Element(ns + "Days")?.Value,
                            out var d
                        )
                            ? d
                            : null,
                    };

                    await storageEngine.SetObjectLockConfigAsync(
                        bucket,
                        config,
                        context.RequestAborted
                    );
                    return Results.Ok();
                }

                if (context.Request.Query.ContainsKey("notification"))
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var xml = await reader.ReadToEndAsync(context.RequestAborted);
                    var doc = XDocument.Parse(xml);

                    var notifications =
                        doc.Root?.Elements(doc.Root.Name.Namespace + "QueueConfiguration")
                            .Select(q =>
                            {
                                var filter = q.Element(q.Name.Namespace + "Filter")
                                    ?.Element(q.Name.Namespace + "S3Key");
                                var rule = filter
                                    ?.Elements(q.Name.Namespace + "FilterRule")
                                    .FirstOrDefault(r =>
                                        r.Element(r.Name.Namespace + "Name")?.Value == "prefix"
                                    );

                                return new NotificationConfiguration
                                {
                                    Id =
                                        q.Element(q.Name.Namespace + "Id")?.Value
                                        ?? Guid.NewGuid().ToString(),
                                    WebhookUrl = q.Element(q.Name.Namespace + "Queue")?.Value ?? "",
                                    Events = q.Elements(q.Name.Namespace + "Event")
                                        .Select(e => e.Value)
                                        .ToList(),
                                    FilterPrefix = rule?.Element(q.Name.Namespace + "Value")?.Value,
                                };
                            })
                            .ToList()
                        ?? new List<NotificationConfiguration>();

                    await storageEngine.SetNotificationsAsync(
                        bucket,
                        notifications,
                        context.RequestAborted
                    );
                    return Results.Ok();
                }

                if (context.Request.Query.ContainsKey("lifecycle"))
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var xml = await reader.ReadToEndAsync(context.RequestAborted);
                    LifecycleConfiguration config;
                    try
                    {
                        config = S3LifecycleXml.Parse(xml);
                    }
                    catch (Exception ex) when (ex is FormatException or System.Xml.XmlException)
                    {
                        await S3ErrorResponses.MalformedXmlAsync(context, ex.Message);
                        return Results.Empty;
                    }

                    await storageEngine.SetLifecycleAsync(bucket, config, context.RequestAborted);
                    return Results.Ok();
                }

                try
                {
                    await storageEngine.CreateBucketAsync(bucket, false, context.RequestAborted);
                    context.Response.Headers.Location = $"/{bucket}";
                    return Results.Ok();
                }
                catch (InvalidOperationException)
                {
                    await S3ErrorResponses.BucketAlreadyExistsAsync(context);
                    return Results.Empty;
                }
            }
        );

        // Head Bucket (HEAD /{bucket})
        s3.MapMethods(
            "/{bucket}",
            ["HEAD"],
            async (
                string bucket,
                IStorageEngine storageEngine,
                Microsoft.Extensions.Options.IOptions<DotBucket.Server.Configuration.S3Options> s3Options,
                HttpContext context
            ) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                if (await storageEngine.BucketExistsAsync(bucket, context.RequestAborted))
                {
                    context.Response.Headers["x-amz-bucket-region"] = s3Options.Value.Region;
                    context.Response.StatusCode = 200;
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
                return Results.Empty;
            }
        );

        // Delete Bucket (DELETE /{bucket})
        s3.MapDelete(
            "/{bucket}",
            async (string bucket, IStorageEngine storageEngine, HttpContext context) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                // DELETE /{bucket}?lifecycle removes the lifecycle config, not the bucket.
                if (context.Request.Query.ContainsKey("lifecycle"))
                {
                    await storageEngine.DeleteLifecycleAsync(bucket, context.RequestAborted);
                    return Results.NoContent();
                }

                if (!await storageEngine.BucketExistsAsync(bucket, context.RequestAborted))
                {
                    await S3ErrorResponses.NoSuchBucketAsync(context);
                    return Results.Empty;
                }

                try
                {
                    await storageEngine.DeleteBucketAsync(bucket, context.RequestAborted);
                    return Results.NoContent();
                }
                catch (InvalidOperationException ex) when (ex.Message == "BucketNotEmpty")
                {
                    await S3ErrorResponses.BucketNotEmptyAsync(context);
                    return Results.Empty;
                }
            }
        );

        // Get Bucket / List Objects V2 (GET /{bucket})
        s3.MapGet(
            "/{bucket}",
            async (
                string bucket,
                string? prefix,
                IStorageEngine storageEngine,
                Microsoft.Extensions.Options.IOptions<DotBucket.Server.Configuration.S3Options> s3Options,
                HttpContext context
            ) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                if (!await storageEngine.BucketExistsAsync(bucket, context.RequestAborted))
                {
                    await S3ErrorResponses.NoSuchBucketAsync(context);
                    return Results.Empty;
                }

                if (context.Request.Query.ContainsKey("location"))
                {
                    var region = s3Options.Value.Region;
                    // S3 convention: us-east-1 is represented by an empty LocationConstraint.
                    var doc = new XDocument(
                        new XElement(
                            S3Ns + "LocationConstraint",
                            string.Equals(region, "us-east-1", StringComparison.OrdinalIgnoreCase)
                                ? null
                                : region
                        )
                    );

                    context.Response.Headers["x-amz-bucket-region"] = region;
                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(doc.ToString(), context.RequestAborted);
                    return Results.Empty;
                }

                if (context.Request.Query.ContainsKey("lifecycle"))
                {
                    var config = await storageEngine.GetLifecycleAsync(
                        bucket,
                        context.RequestAborted
                    );
                    if (config == null || config.Rules.Count == 0)
                    {
                        await S3ErrorResponses.NoSuchLifecycleConfigurationAsync(context);
                        return Results.Empty;
                    }

                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(
                        S3LifecycleXml.Build(config),
                        context.RequestAborted
                    );
                    return Results.Empty;
                }

                if (context.Request.Query.ContainsKey("versioning"))
                {
                    var bucketObj = await storageEngine.GetBucketAsync(
                        bucket,
                        context.RequestAborted
                    );
                    var status = bucketObj?.Versioning switch
                    {
                        VersioningStatus.Enabled => "Enabled",
                        VersioningStatus.Suspended => "Suspended",
                        _ => null,
                    };

                    var doc = new XDocument(
                        new XElement(
                            S3Ns + "VersioningConfiguration",
                            status != null ? new XElement(S3Ns + "Status", status) : null
                        )
                    );

                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(doc.ToString(), context.RequestAborted);
                    return Results.Empty;
                }

                if (context.Request.Query.ContainsKey("notification"))
                {
                    var configs = await storageEngine.GetNotificationsAsync(
                        bucket,
                        context.RequestAborted
                    );
                    var doc = new XDocument(
                        new XElement(
                            S3Ns + "NotificationConfiguration",
                            configs.Select(c => new XElement(
                                S3Ns + "QueueConfiguration",
                                new XElement(S3Ns + "Id", c.Id),
                                new XElement(S3Ns + "Queue", c.WebhookUrl),
                                c.Events.Select(e => new XElement(S3Ns + "Event", e)),
                                c.FilterPrefix != null
                                    ? new XElement(
                                        S3Ns + "Filter",
                                        new XElement(
                                            S3Ns + "S3Key",
                                            new XElement(
                                                S3Ns + "FilterRule",
                                                new XElement(S3Ns + "Name", "prefix"),
                                                new XElement(S3Ns + "Value", c.FilterPrefix)
                                            )
                                        )
                                    )
                                    : null
                            ))
                        )
                    );

                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(doc.ToString(), context.RequestAborted);
                    return Results.Empty;
                }

                if (context.Request.Query.ContainsKey("uploads"))
                {
                    var uploads = await storageEngine.ListMultipartUploadsAsync(
                        bucket,
                        prefix,
                        context.RequestAborted
                    );
                    var settings = new XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Encoding = Encoding.UTF8,
                    };
                    using var ms = new MemoryStream();
                    using var writer = XmlWriter.Create(ms, settings);

                    writer.WriteStartElement("ListMultipartUploadsResult", S3Ns.NamespaceName);
                    writer.WriteElementString("Bucket", bucket);
                    writer.WriteElementString("Prefix", prefix ?? "");

                    foreach (var upload in uploads)
                    {
                        writer.WriteStartElement("Upload");
                        writer.WriteElementString("Key", upload.ObjectKey);
                        writer.WriteElementString("UploadId", upload.UploadId);
                        writer.WriteElementString(
                            "Initiated",
                            upload.Initiated.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                        );
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.Flush();

                    context.Response.ContentType = "application/xml";
                    await context.Response.Body.WriteAsync(ms.ToArray(), context.RequestAborted);
                    return Results.Empty;
                }

                if (context.Request.Query.ContainsKey("versions"))
                {
                    var allVersions = await storageEngine.ListObjectsAsync(
                        bucket,
                        prefix,
                        true,
                        context.RequestAborted
                    );

                    var doc = new XDocument(
                        new XElement(
                            S3Ns + "ListVersionsResult",
                            new XElement(S3Ns + "Name", bucket),
                            new XElement(S3Ns + "Prefix", prefix ?? ""),
                            new XElement(S3Ns + "MaxKeys", "1000"),
                            new XElement(S3Ns + "IsTruncated", "false"),
                            allVersions.Select(v =>
                                v.IsDeleteMarker
                                    ? new XElement(
                                        S3Ns + "DeleteMarker",
                                        new XElement(S3Ns + "Key", v.ObjectKey),
                                        new XElement(S3Ns + "VersionId", v.VersionId),
                                        new XElement(
                                            S3Ns + "IsLatest",
                                            v.IsLatest.ToString().ToLower()
                                        ),
                                        new XElement(
                                            S3Ns + "LastModified",
                                            v.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                                        )
                                    )
                                    : new XElement(
                                        S3Ns + "Version",
                                        new XElement(S3Ns + "Key", v.ObjectKey),
                                        new XElement(S3Ns + "VersionId", v.VersionId),
                                        new XElement(
                                            S3Ns + "IsLatest",
                                            v.IsLatest.ToString().ToLower()
                                        ),
                                        new XElement(
                                            S3Ns + "LastModified",
                                            v.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                                        ),
                                        new XElement(S3Ns + "ETag", v.ETag),
                                        new XElement(S3Ns + "Size", v.Size),
                                        new XElement(S3Ns + "StorageClass", "STANDARD")
                                    )
                            )
                        )
                    );

                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(doc.ToString(), context.RequestAborted);
                    return Results.Empty;
                }

                // Default: ListObjectsV2 with pagination
                var maxKeysStr = context.Request.Query["max-keys"].ToString();
                var maxKeys = 1000;
                if (!string.IsNullOrEmpty(maxKeysStr) && int.TryParse(maxKeysStr, out var mk))
                    maxKeys = Math.Min(mk, 1000);

                var continuationToken = context.Request.Query["continuation-token"].ToString();
                if (string.IsNullOrEmpty(continuationToken))
                    continuationToken = null;

                var startAfter = context.Request.Query["start-after"].ToString();
                if (string.IsNullOrEmpty(startAfter))
                    startAfter = null;

                var (objects, nextToken, isTruncated) = await storageEngine.ListObjectsPagedAsync(
                    bucket,
                    prefix,
                    continuationToken,
                    startAfter,
                    maxKeys,
                    context.RequestAborted
                );
                var objectList = objects.ToList();

                var xmlSettings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Encoding = Encoding.UTF8,
                };
                using var listMs = new MemoryStream();
                using var listWriter = XmlWriter.Create(listMs, xmlSettings);

                listWriter.WriteStartElement("ListBucketResult", S3Ns.NamespaceName);
                listWriter.WriteElementString("Name", bucket);
                listWriter.WriteElementString("Prefix", prefix ?? string.Empty);
                listWriter.WriteElementString("KeyCount", objectList.Count.ToString());
                listWriter.WriteElementString("MaxKeys", maxKeys.ToString());
                listWriter.WriteElementString("IsTruncated", isTruncated.ToString().ToLower());

                if (nextToken != null)
                {
                    listWriter.WriteElementString("NextContinuationToken", nextToken);
                }
                if (continuationToken != null)
                {
                    listWriter.WriteElementString("ContinuationToken", continuationToken);
                }
                if (startAfter != null)
                {
                    listWriter.WriteElementString("StartAfter", startAfter);
                }

                foreach (var obj in objectList)
                {
                    listWriter.WriteStartElement("Contents");
                    listWriter.WriteElementString("Key", obj.ObjectKey);
                    listWriter.WriteElementString(
                        "LastModified",
                        obj.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    );
                    listWriter.WriteElementString("ETag", obj.ETag);
                    listWriter.WriteElementString("Size", obj.Size.ToString());
                    listWriter.WriteElementString("StorageClass", "STANDARD");
                    listWriter.WriteEndElement(); // Contents
                }

                listWriter.WriteEndElement(); // ListBucketResult
                listWriter.Flush();

                context.Response.ContentType = "application/xml";
                await context.Response.Body.WriteAsync(listMs.ToArray(), context.RequestAborted);
                return Results.Empty;
            }
        );

        // Multipart: Upload Part (PUT /{bucket}/{**key}?partNumber=X&uploadId=Y)
        // CopyObject: PUT /{bucket}/{**key} with x-amz-copy-source
        // Standard: Put Object (PUT /{bucket}/{**key})
        s3.MapPut(
            "/{bucket}/{**key}",
            async (
                string bucket,
                string key,
                int? partNumber,
                string? uploadId,
                string? versionId,
                IStorageEngine storageEngine,
                HttpContext context
            ) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                if (!await storageEngine.BucketExistsAsync(bucket, context.RequestAborted))
                {
                    await S3ErrorResponses.NoSuchBucketAsync(context);
                    return Results.Empty;
                }

                // Object Lock: Retention
                if (context.Request.Query.ContainsKey("retention"))
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var xml = await reader.ReadToEndAsync(context.RequestAborted);
                    var doc = XDocument.Parse(xml);
                    var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                    var mode = doc.Root?.Element(ns + "Mode")?.Value ?? "GOVERNANCE";
                    var date = DateTime.Parse(
                        doc.Root?.Element(ns + "RetainUntilDate")?.Value
                            ?? DateTime.UtcNow.ToString()
                    );
                    await storageEngine.SetObjectRetentionAsync(
                        bucket,
                        key,
                        versionId,
                        mode,
                        date,
                        context.RequestAborted
                    );
                    return Results.Ok();
                }

                // Object Lock: Legal Hold
                if (context.Request.Query.ContainsKey("legal-hold"))
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var xml = await reader.ReadToEndAsync(context.RequestAborted);
                    var doc = XDocument.Parse(xml);
                    var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                    var hold = doc.Root?.Element(ns + "Status")?.Value == "ON";
                    await storageEngine.SetObjectLegalHoldAsync(
                        bucket,
                        key,
                        versionId,
                        hold,
                        context.RequestAborted
                    );
                    return Results.Ok();
                }

                // Multipart: Upload Part
                if (uploadId != null && partNumber != null)
                {
                    var etag = await storageEngine.UploadPartAsync(
                        bucket,
                        key,
                        uploadId,
                        partNumber.Value,
                        context.Request.Body,
                        context.RequestAborted
                    );
                    context.Response.Headers.ETag = etag;
                    return Results.Ok();
                }

                // CopyObject
                var copySource = context.Request.Headers["x-amz-copy-source"].ToString();
                if (!string.IsNullOrEmpty(copySource))
                {
                    // Parse source: /bucket/key or bucket/key, optionally ?versionId=...
                    copySource = Uri.UnescapeDataString(copySource);
                    if (copySource.StartsWith('/'))
                        copySource = copySource[1..];

                    string? srcVersionId = null;
                    var qIdx = copySource.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        var qs = copySource[(qIdx + 1)..];
                        copySource = copySource[..qIdx];
                        if (qs.StartsWith("versionId="))
                            srcVersionId = qs["versionId=".Length..];
                    }

                    var slashIdx = copySource.IndexOf('/');
                    if (slashIdx < 0)
                    {
                        await S3ErrorResponses.WriteErrorAsync(
                            context,
                            400,
                            "InvalidArgument",
                            "Invalid x-amz-copy-source header."
                        );
                        return Results.Empty;
                    }

                    var srcBucket = copySource[..slashIdx];
                    var srcKey = copySource[(slashIdx + 1)..];

                    try
                    {
                        var result = await storageEngine.CopyObjectAsync(
                            srcBucket,
                            srcKey,
                            srcVersionId,
                            bucket,
                            key,
                            cancellationToken: context.RequestAborted
                        );

                        var doc = new XDocument(
                            new XElement(
                                S3Ns + "CopyObjectResult",
                                new XElement(S3Ns + "ETag", result.ETag),
                                new XElement(
                                    S3Ns + "LastModified",
                                    result.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                                )
                            )
                        );

                        context.Response.ContentType = "application/xml";
                        await context.Response.WriteAsync(doc.ToString(), context.RequestAborted);
                        return Results.Empty;
                    }
                    catch (InvalidOperationException)
                    {
                        await S3ErrorResponses.NoSuchKeyAsync(context);
                        return Results.Empty;
                    }
                }

                // Standard PutObject
                var contentType = context.Request.ContentType ?? "application/octet-stream";
                if (!TryGetSseAlgorithm(context.Request, out var encryption))
                {
                    return await WriteInvalidSseHeaderAsync(context);
                }

                var metadata = new Dictionary<string, string>();
                foreach (var header in context.Request.Headers)
                {
                    if (header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                    {
                        metadata[header.Key] = header.Value.ToString();
                    }
                }

                var putResult = await storageEngine.PutObjectAsync(
                    bucket,
                    key,
                    context.Request.Body,
                    contentType,
                    metadata,
                    encryption,
                    context.RequestAborted
                );

                context.Response.Headers.ETag = putResult.ETag;
                if (!string.IsNullOrEmpty(putResult.Encryption))
                {
                    context.Response.Headers["x-amz-server-side-encryption"] = putResult.Encryption;
                }
                if (putResult.VersionId != "null")
                {
                    context.Response.Headers["x-amz-version-id"] = putResult.VersionId;
                }
                return Results.Ok();
            }
        );

        // Multipart: Initiate or Complete (POST /{bucket}/{**key}?uploads OR uploadId=Z)
        s3.MapPost(
            "/{bucket}/{**key}",
            async (
                string bucket,
                string key,
                string? uploads,
                string? uploadId,
                IStorageEngine storageEngine,
                HttpContext context
            ) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                if (!await storageEngine.BucketExistsAsync(bucket, context.RequestAborted))
                {
                    await S3ErrorResponses.NoSuchBucketAsync(context);
                    return Results.Empty;
                }

                if (context.Request.Query.ContainsKey("uploads"))
                {
                    var contentType = context.Request.ContentType ?? "application/octet-stream";
                    if (!TryGetSseAlgorithm(context.Request, out var encryption))
                    {
                        return await WriteInvalidSseHeaderAsync(context);
                    }

                    var metadata = new Dictionary<string, string>();
                    foreach (var header in context.Request.Headers)
                    {
                        if (
                            header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            metadata[header.Key] = header.Value.ToString();
                        }
                    }

                    var newUploadId = await storageEngine.InitiateMultipartUploadAsync(
                        bucket,
                        key,
                        contentType,
                        metadata,
                        encryption,
                        context.RequestAborted
                    );

                    if (!string.IsNullOrEmpty(encryption))
                    {
                        context.Response.Headers["x-amz-server-side-encryption"] = encryption;
                    }

                    var settings = new XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Encoding = Encoding.UTF8,
                    };
                    using var ms = new MemoryStream();
                    using var writer = XmlWriter.Create(ms, settings);
                    writer.WriteStartElement("InitiateMultipartUploadResult", S3Ns.NamespaceName);
                    writer.WriteElementString("Bucket", bucket);
                    writer.WriteElementString("Key", key);
                    writer.WriteElementString("UploadId", newUploadId);
                    writer.WriteEndElement();
                    writer.Flush();

                    context.Response.ContentType = "application/xml";
                    await context.Response.Body.WriteAsync(ms.ToArray(), context.RequestAborted);
                    return Results.Empty;
                }

                if (uploadId != null)
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var xml = await reader.ReadToEndAsync(context.RequestAborted);
                    var doc = XDocument.Parse(xml);
                    var parts =
                        doc.Root?.Elements(doc.Root.Name.Namespace + "Part")
                            .Select(p =>
                                (
                                    PartNumber: int.Parse(
                                        p.Element(p.Name.Namespace + "PartNumber")?.Value ?? "0"
                                    ),
                                    ETag: p.Element(p.Name.Namespace + "ETag")?.Value ?? ""
                                )
                            )
                            .ToList()
                        ?? new();

                    var obj = await storageEngine.CompleteMultipartUploadAsync(
                        bucket,
                        key,
                        uploadId,
                        parts,
                        context.RequestAborted
                    );

                    var settings = new XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Encoding = Encoding.UTF8,
                    };
                    using var ms = new MemoryStream();
                    using var writer = XmlWriter.Create(ms, settings);
                    writer.WriteStartElement("CompleteMultipartUploadResult", S3Ns.NamespaceName);
                    writer.WriteElementString("Location", $"/{bucket}/{key}");
                    writer.WriteElementString("Bucket", bucket);
                    writer.WriteElementString("Key", key);
                    writer.WriteElementString("ETag", obj.ETag);
                    if (obj.VersionId != "null")
                    {
                        writer.WriteElementString("VersionId", obj.VersionId);
                    }
                    writer.WriteEndElement();
                    writer.Flush();

                    if (obj.VersionId != "null")
                    {
                        context.Response.Headers["x-amz-version-id"] = obj.VersionId;
                    }

                    context.Response.ContentType = "application/xml";
                    await context.Response.Body.WriteAsync(ms.ToArray(), context.RequestAborted);
                    return Results.Empty;
                }

                return Results.BadRequest();
            }
        );

        // Batch Delete (POST /{bucket}?delete)
        s3.MapPost(
            "/{bucket}",
            async (string bucket, IStorageEngine storageEngine, HttpContext context) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                if (!context.Request.Query.ContainsKey("delete"))
                {
                    return Results.BadRequest();
                }

                if (!await storageEngine.BucketExistsAsync(bucket, context.RequestAborted))
                {
                    await S3ErrorResponses.NoSuchBucketAsync(context);
                    return Results.Empty;
                }

                using var reader = new StreamReader(context.Request.Body);
                var xml = await reader.ReadToEndAsync(context.RequestAborted);
                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var quiet = doc.Root?.Element(ns + "Quiet")?.Value == "true";

                var objectsToDelete =
                    doc.Root?.Elements(ns + "Object")
                        .Select(o =>
                            (
                                Key: o.Element(ns + "Key")?.Value ?? "",
                                VersionId: o.Element(ns + "VersionId")?.Value
                            )
                        )
                        .Where(o => !string.IsNullOrEmpty(o.Key))
                        .ToList()
                    ?? new();

                var results = await storageEngine.DeleteObjectsAsync(
                    bucket,
                    objectsToDelete,
                    quiet,
                    context.RequestAborted
                );

                var settings = new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Encoding = Encoding.UTF8,
                };
                using var ms = new MemoryStream();
                using var writer = XmlWriter.Create(ms, settings);
                writer.WriteStartElement("DeleteResult", S3Ns.NamespaceName);

                foreach (var (key, success, errorCode, errorMessage) in results)
                {
                    if (success && !quiet)
                    {
                        writer.WriteStartElement("Deleted");
                        writer.WriteElementString("Key", key);
                        writer.WriteEndElement();
                    }
                    else if (!success)
                    {
                        writer.WriteStartElement("Error");
                        writer.WriteElementString("Key", key);
                        writer.WriteElementString("Code", errorCode ?? "InternalError");
                        writer.WriteElementString("Message", errorMessage ?? "Internal error");
                        writer.WriteEndElement();
                    }
                }

                writer.WriteEndElement();
                writer.Flush();

                context.Response.ContentType = "application/xml";
                await context.Response.Body.WriteAsync(ms.ToArray(), context.RequestAborted);
                return Results.Empty;
            }
        );

        // Head Object (HEAD /{bucket}/{**key})
        s3.MapMethods(
            "/{bucket}/{**key}",
            ["HEAD"],
            async (
                string bucket,
                string key,
                string? versionId,
                IStorageEngine storageEngine,
                HttpContext context
            ) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                var metadata = await storageEngine.HeadObjectAsync(
                    bucket,
                    key,
                    versionId,
                    context.RequestAborted
                );
                if (metadata == null)
                {
                    context.Response.StatusCode = 404;
                    return Results.NotFound();
                }

                context.Response.Headers.ETag = metadata.ETag;
                context.Response.Headers.LastModified = metadata.LastModified.ToString("R");
                context.Response.Headers.ContentLength = metadata.Size;
                context.Response.ContentType = metadata.ContentType;

                if (metadata.VersionId != "null")
                {
                    context.Response.Headers["x-amz-version-id"] = metadata.VersionId;
                }

                foreach (var meta in metadata.Metadata)
                {
                    context.Response.Headers[meta.Key] = meta.Value;
                }

                context.Response.Headers["Accept-Ranges"] = "bytes";
                return Results.Empty;
            }
        );

        // Get Object (GET /{bucket}/{**key})
        // List Parts (GET /{bucket}/{**key}?uploadId=...)
        s3.MapGet(
            "/{bucket}/{**key}",
            async (
                string bucket,
                string key,
                string? versionId,
                string? uploadId,
                IStorageEngine storageEngine,
                HttpContext context
            ) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                // List Parts
                if (uploadId != null)
                {
                    var parts = await storageEngine.ListPartsAsync(
                        bucket,
                        key,
                        uploadId,
                        context.RequestAborted
                    );
                    var partsList = parts.ToList();

                    var settings = new XmlWriterSettings
                    {
                        OmitXmlDeclaration = false,
                        Encoding = Encoding.UTF8,
                    };
                    using var ms = new MemoryStream();
                    using var writer = XmlWriter.Create(ms, settings);

                    writer.WriteStartElement("ListPartsResult", S3Ns.NamespaceName);
                    writer.WriteElementString("Bucket", bucket);
                    writer.WriteElementString("Key", key);
                    writer.WriteElementString("UploadId", uploadId);

                    foreach (var part in partsList)
                    {
                        writer.WriteStartElement("Part");
                        writer.WriteElementString("PartNumber", part.PartNumber.ToString());
                        writer.WriteElementString("ETag", part.ETag);
                        writer.WriteElementString("Size", part.Size.ToString());
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.Flush();

                    context.Response.ContentType = "application/xml";
                    await context.Response.Body.WriteAsync(ms.ToArray(), context.RequestAborted);
                    return Results.Empty;
                }

                // Get Object with conditional headers and range support
                var metadata = await storageEngine.HeadObjectAsync(
                    bucket,
                    key,
                    versionId,
                    context.RequestAborted
                );
                if (metadata == null)
                {
                    await S3ErrorResponses.NoSuchKeyAsync(context);
                    return Results.Empty;
                }

                // Evaluate conditional headers
                var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == metadata.ETag)
                {
                    context.Response.StatusCode = 304;
                    return Results.Empty;
                }

                var ifMatch = context.Request.Headers.IfMatch.ToString();
                if (!string.IsNullOrEmpty(ifMatch) && ifMatch != metadata.ETag)
                {
                    await S3ErrorResponses.PreconditionFailedAsync(context);
                    return Results.Empty;
                }

                if (
                    context.Request.Headers.TryGetValue(
                        "If-Modified-Since",
                        out var ifModifiedSince
                    )
                    && DateTimeOffset.TryParse(ifModifiedSince.ToString(), out var modSince)
                    && metadata.LastModified <= modSince.UtcDateTime
                )
                {
                    context.Response.StatusCode = 304;
                    return Results.Empty;
                }

                if (
                    context.Request.Headers.TryGetValue(
                        "If-Unmodified-Since",
                        out var ifUnmodifiedSince
                    )
                    && DateTimeOffset.TryParse(ifUnmodifiedSince.ToString(), out var unmodSince)
                    && metadata.LastModified > unmodSince.UtcDateTime
                )
                {
                    await S3ErrorResponses.PreconditionFailedAsync(context);
                    return Results.Empty;
                }

                // Set common headers
                context.Response.Headers.ETag = metadata.ETag;
                context.Response.Headers.LastModified = metadata.LastModified.ToString("R");
                context.Response.Headers["Accept-Ranges"] = "bytes";
                if (metadata.VersionId != "null")
                {
                    context.Response.Headers["x-amz-version-id"] = metadata.VersionId;
                }

                foreach (var meta in metadata.Metadata)
                {
                    context.Response.Headers[meta.Key] = meta.Value;
                }

                // Parse Range header
                var rangeHeader = context.Request.Headers.Range.ToString();
                if (
                    !string.IsNullOrEmpty(rangeHeader)
                    && System.Net.Http.Headers.RangeHeaderValue.TryParse(
                        rangeHeader,
                        out var rangeValue
                    )
                    && rangeValue.Ranges.Count == 1
                )
                {
                    var range = rangeValue.Ranges.First();
                    var totalSize = metadata.Size;

                    long start;
                    long end;

                    if (range.From.HasValue && range.To.HasValue)
                    {
                        start = range.From.Value;
                        end = Math.Min(range.To.Value, totalSize - 1);
                    }
                    else if (range.From.HasValue)
                    {
                        start = range.From.Value;
                        end = totalSize - 1;
                    }
                    else if (range.To.HasValue)
                    {
                        // Suffix range: last N bytes
                        start = Math.Max(0, totalSize - range.To.Value);
                        end = totalSize - 1;
                    }
                    else
                    {
                        await S3ErrorResponses.InvalidRangeAsync(context);
                        return Results.Empty;
                    }

                    if (start > end || start >= totalSize)
                    {
                        await S3ErrorResponses.InvalidRangeAsync(context);
                        return Results.Empty;
                    }

                    var result = await storageEngine.GetObjectAsync(
                        bucket,
                        key,
                        versionId,
                        context.RequestAborted
                    );
                    if (result == null)
                    {
                        await S3ErrorResponses.NoSuchKeyAsync(context);
                        return Results.Empty;
                    }

                    var (_, content) = result.Value;
                    if (content.CanSeek)
                    {
                        content.Seek(start, SeekOrigin.Begin);
                    }
                    else
                    {
                        await SkipBytesAsync(content, start, context.RequestAborted);
                    }
                    var length = end - start + 1;

                    context.Response.StatusCode = 206;
                    context.Response.ContentType = metadata.ContentType;
                    context.Response.Headers.ContentLength = length;
                    context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{totalSize}";

                    var limitedStream = new LengthLimitedStream(content, length);
                    await limitedStream.CopyToAsync(context.Response.Body, context.RequestAborted);
                    await content.DisposeAsync();
                    return Results.Empty;
                }

                // Full object response
                var fullResult = await storageEngine.GetObjectAsync(
                    bucket,
                    key,
                    versionId,
                    context.RequestAborted
                );
                if (fullResult == null)
                {
                    await S3ErrorResponses.NoSuchKeyAsync(context);
                    return Results.Empty;
                }

                var (fullMeta, fullContent) = fullResult.Value;
                return Results.Stream(fullContent, fullMeta.ContentType);
            }
        );

        // Delete Object (DELETE /{bucket}/{**key})
        s3.MapDelete(
            "/{bucket}/{**key}",
            async (
                string bucket,
                string key,
                string? versionId,
                string? uploadId,
                IStorageEngine storageEngine,
                HttpContext context
            ) =>
            {
                if (IsReservedBucketName(bucket))
                {
                    return Results.NotFound();
                }

                if (uploadId != null)
                {
                    await storageEngine.AbortMultipartUploadAsync(
                        bucket,
                        key,
                        uploadId,
                        context.RequestAborted
                    );
                    return Results.NoContent();
                }

                try
                {
                    await storageEngine.DeleteObjectAsync(
                        bucket,
                        key,
                        versionId,
                        context.RequestAborted
                    );
                }
                catch (InvalidOperationException ex)
                    when (ex.Message is "ObjectUnderLegalHold" or "ObjectUnderRetention")
                {
                    await S3ErrorResponses.WriteErrorAsync(
                        context,
                        403,
                        "MethodNotAllowed",
                        "Object is locked."
                    );
                    return Results.Empty;
                }

                if (versionId != null)
                {
                    context.Response.Headers["x-amz-version-id"] = versionId;
                }

                return Results.NoContent();
            }
        );
    }
}
