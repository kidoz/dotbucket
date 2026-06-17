// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Endpoints.S3;

public static class S3ErrorResponses
{
    public static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string? message = null
    )
    {
        var requestId = Guid.NewGuid().ToString("N");
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/xml";

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Error>
                <Code>{code}</Code>
                <Message>{message ?? code}</Message>
                <RequestId>{requestId}</RequestId>
            </Error>
            """;

        await context.Response.WriteAsync(xml, context.RequestAborted);
    }

    public static Task NoSuchKeyAsync(HttpContext context) =>
        WriteErrorAsync(context, 404, "NoSuchKey", "The specified key does not exist.");

    public static Task NoSuchBucketAsync(HttpContext context) =>
        WriteErrorAsync(context, 404, "NoSuchBucket", "The specified bucket does not exist.");

    public static Task BucketAlreadyExistsAsync(HttpContext context) =>
        WriteErrorAsync(context, 409, "BucketAlreadyOwnedByYou", "The bucket already exists.");

    public static Task InvalidRangeAsync(HttpContext context) =>
        WriteErrorAsync(context, 416, "InvalidRange", "The requested range is not satisfiable.");

    public static Task PreconditionFailedAsync(HttpContext context) =>
        WriteErrorAsync(
            context,
            412,
            "PreconditionFailed",
            "At least one of the preconditions you specified did not hold."
        );

    public static Task BucketNotEmptyAsync(HttpContext context) =>
        WriteErrorAsync(
            context,
            409,
            "BucketNotEmpty",
            "The bucket you tried to delete is not empty."
        );

    public static Task NoSuchUploadAsync(HttpContext context) =>
        WriteErrorAsync(
            context,
            404,
            "NoSuchUpload",
            "The specified multipart upload does not exist."
        );

    public static Task AccessDeniedAsync(HttpContext context) =>
        WriteErrorAsync(context, 403, "AccessDenied", "Access Denied");

    public static Task NoSuchLifecycleConfigurationAsync(HttpContext context) =>
        WriteErrorAsync(
            context,
            404,
            "NoSuchLifecycleConfiguration",
            "The lifecycle configuration does not exist."
        );

    public static Task InvalidBucketStateAsync(HttpContext context, string? message = null) =>
        WriteErrorAsync(
            context,
            409,
            "InvalidBucketState",
            message ?? "Object Lock configuration cannot be enabled on existing buckets."
        );

    public static Task InvalidRequestAsync(HttpContext context, string? message = null) =>
        WriteErrorAsync(
            context,
            400,
            "InvalidRequest",
            message ?? "Bucket is missing Object Lock Configuration."
        );

    public static Task EntityTooSmallAsync(HttpContext context, string? message = null) =>
        WriteErrorAsync(
            context,
            400,
            "EntityTooSmall",
            message
                ?? "Your proposed upload is smaller than the minimum allowed object size for a multipart part."
        );

    public static Task InvalidPartAsync(HttpContext context, string? message = null) =>
        WriteErrorAsync(
            context,
            400,
            "InvalidPart",
            message
                ?? "One or more of the specified parts could not be found or the part ETag did not match."
        );

    public static Task InvalidPartOrderAsync(HttpContext context, string? message = null) =>
        WriteErrorAsync(
            context,
            400,
            "InvalidPartOrder",
            message
                ?? "The list of parts was not in ascending order; parts must be ordered by part number."
        );

    public static Task MalformedXmlAsync(HttpContext context, string? message = null) =>
        WriteErrorAsync(
            context,
            400,
            "MalformedXML",
            message ?? "The XML you provided was not well-formed or did not validate."
        );
}
