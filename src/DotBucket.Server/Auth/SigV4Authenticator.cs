// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DotBucket.Server.Auth;

/// <summary>
/// A minimal implementation of AWS Signature Version 4 authentication.
/// </summary>
public class SigV4Authenticator(
    ICredentialStore credentialStore,
    ILogger<SigV4Authenticator> logger
) : ISigV4Authenticator
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(15);

    // Matches 'Credential=.../YYYYMMDD/region/service/aws4_request'
    private static readonly Regex CredentialRegex = new(
        @"Credential=([^/]+)/([^/]+)/([^/]+)/([^/]+)/aws4_request",
        RegexOptions.Compiled
    );

    // Matches 'SignedHeaders=host;x-amz-date...'
    private static readonly Regex SignedHeadersRegex = new(
        @"SignedHeaders=([^,]+)",
        RegexOptions.Compiled
    );

    // Matches 'Signature=...'
    private static readonly Regex SignatureRegex = new(
        @"Signature=([a-f0-9]+)",
        RegexOptions.Compiled
    );

    public async Task<bool> AuthenticateAsync(
        HttpContext context,
        CancellationToken cancellationToken = default
    )
    {
        // Check for presigned URL (query-string authentication)
        if (context.Request.Query.ContainsKey("X-Amz-Algorithm"))
        {
            return await AuthenticatePresignedAsync(context, cancellationToken);
        }

        var authHeader = context.Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith($"{Algorithm} "))
        {
            logger.LogWarning("Missing or invalid Authorization header format.");
            return false;
        }

        var matchCred = CredentialRegex.Match(authHeader);
        var matchHeaders = SignedHeadersRegex.Match(authHeader);
        var matchSig = SignatureRegex.Match(authHeader);

        if (!matchCred.Success || !matchHeaders.Success || !matchSig.Success)
        {
            logger.LogWarning("Malformed AWS4-HMAC-SHA256 Authorization header.");
            return false;
        }

        var accessKey = matchCred.Groups[1].Value;
        var dateStamp = matchCred.Groups[2].Value;
        var region = matchCred.Groups[3].Value;
        var service = matchCred.Groups[4].Value;
        var signedHeadersStr = matchHeaders.Groups[1].Value;
        var providedSignature = matchSig.Groups[1].Value;

        var secretKey = await credentialStore.GetSecretKeyAsync(accessKey, cancellationToken);
        if (string.IsNullOrEmpty(secretKey))
        {
            logger.LogWarning("Invalid access key: {AccessKey}", accessKey);
            return false;
        }

        var amzDate = context.Request.Headers["x-amz-date"].ToString();
        if (string.IsNullOrEmpty(amzDate))
        {
            amzDate = context.Request.Headers["Date"].ToString();
        }

        if (string.IsNullOrEmpty(amzDate))
        {
            logger.LogWarning("Missing x-amz-date or Date header.");
            return false;
        }

        // Timestamp validation (±15 minutes)
        if (!ValidateTimestamp(amzDate))
        {
            logger.LogWarning("Request timestamp is outside the allowed ±15 minute window.");
            return false;
        }

        context.Request.EnableBuffering();

        string payloadHash;
        if (context.Request.Headers.TryGetValue("x-amz-content-sha256", out var headerHash))
        {
            var headerValue = headerHash.ToString();

            // UNSIGNED-PAYLOAD and streaming modes skip body hash validation
            if (
                headerValue == "UNSIGNED-PAYLOAD"
                || headerValue.StartsWith("STREAMING-", StringComparison.Ordinal)
            )
            {
                payloadHash = headerValue;
            }
            else
            {
                // Compute actual body hash and verify it matches the header
                using var sha256 = SHA256.Create();
                var bodyHashBytes = await sha256.ComputeHashAsync(
                    context.Request.Body,
                    cancellationToken
                );
                var computedHash = Convert.ToHexString(bodyHashBytes).ToLowerInvariant();
                context.Request.Body.Position = 0;

                if (computedHash != headerValue)
                {
                    logger.LogWarning(
                        "Payload hash mismatch: expected {Expected}, computed {Computed}",
                        headerValue,
                        computedHash
                    );
                    return false;
                }

                payloadHash = headerValue;
            }
        }
        else
        {
            using var sha256 = SHA256.Create();
            var bodyHashBytes = await sha256.ComputeHashAsync(
                context.Request.Body,
                cancellationToken
            );
            payloadHash = Convert.ToHexString(bodyHashBytes).ToLowerInvariant();
            context.Request.Body.Position = 0;
        }

        var canonicalRequest = BuildCanonicalRequest(
            context.Request,
            signedHeadersStr,
            payloadHash
        );
        var stringToSign = BuildStringToSign(amzDate, dateStamp, region, service, canonicalRequest);
        var signatureKey = GetSignatureKey(secretKey, dateStamp, region, service);
        var calculatedSignature = Convert
            .ToHexString(HmacSha256(signatureKey, stringToSign))
            .ToLowerInvariant();

        // Constant-time signature comparison
        var calculatedBytes = Encoding.UTF8.GetBytes(calculatedSignature);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature);

        if (!CryptographicOperations.FixedTimeEquals(calculatedBytes, providedBytes))
        {
            logger.LogWarning("Signature mismatch for access key: {AccessKey}", accessKey);
            return false;
        }

        logger.LogInformation(
            "Successfully authenticated user with access key: {AccessKey}",
            accessKey
        );

        // Stash the authenticated user key in the HttpContext for downstream handlers
        context.Items["AccessKey"] = accessKey;

        return true;
    }

    private async Task<bool> AuthenticatePresignedAsync(
        HttpContext context,
        CancellationToken cancellationToken
    )
    {
        var query = context.Request.Query;

        var algorithm = query["X-Amz-Algorithm"].ToString();
        if (algorithm != Algorithm)
        {
            logger.LogWarning("Unsupported presigned URL algorithm: {Algorithm}", algorithm);
            return false;
        }

        var credential = query["X-Amz-Credential"].ToString();
        var parts = credential.Split('/');
        if (parts.Length != 5 || parts[4] != "aws4_request")
        {
            logger.LogWarning("Malformed X-Amz-Credential in presigned URL.");
            return false;
        }

        var accessKey = parts[0];
        var dateStamp = parts[1];
        var region = parts[2];
        var service = parts[3];
        var signedHeadersStr = query["X-Amz-SignedHeaders"].ToString();
        var providedSignature = query["X-Amz-Signature"].ToString();
        var amzDate = query["X-Amz-Date"].ToString();
        var expiresStr = query["X-Amz-Expires"].ToString();

        if (
            string.IsNullOrEmpty(signedHeadersStr)
            || string.IsNullOrEmpty(providedSignature)
            || string.IsNullOrEmpty(amzDate)
            || string.IsNullOrEmpty(expiresStr)
        )
        {
            logger.LogWarning("Missing required presigned URL query parameters.");
            return false;
        }

        var secretKey = await credentialStore.GetSecretKeyAsync(accessKey, cancellationToken);
        if (string.IsNullOrEmpty(secretKey))
        {
            logger.LogWarning("Invalid access key in presigned URL: {AccessKey}", accessKey);
            return false;
        }

        // Validate expiration
        if (
            !DateTime.TryParseExact(
                amzDate,
                "yyyyMMddTHHmmssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var requestTime
            )
        )
        {
            logger.LogWarning("Invalid X-Amz-Date format in presigned URL.");
            return false;
        }

        if (!int.TryParse(expiresStr, out var expiresSeconds) || expiresSeconds <= 0)
        {
            logger.LogWarning("Invalid X-Amz-Expires value in presigned URL.");
            return false;
        }

        if (DateTime.UtcNow > requestTime.AddSeconds(expiresSeconds))
        {
            logger.LogWarning("Presigned URL has expired.");
            return false;
        }

        // Build canonical query string excluding X-Amz-Signature
        var canonicalQueryString = BuildCanonicalQueryString(
            context.Request.QueryString.Value?.TrimStart('?') ?? "",
            excludeSignature: true
        );

        // Presigned URLs always use UNSIGNED-PAYLOAD
        var payloadHash = "UNSIGNED-PAYLOAD";

        var canonicalRequest = BuildCanonicalRequestWithQueryString(
            context.Request,
            signedHeadersStr,
            payloadHash,
            canonicalQueryString
        );
        var stringToSign = BuildStringToSign(amzDate, dateStamp, region, service, canonicalRequest);
        var signatureKey = GetSignatureKey(secretKey, dateStamp, region, service);
        var calculatedSignature = Convert
            .ToHexString(HmacSha256(signatureKey, stringToSign))
            .ToLowerInvariant();

        var calculatedBytes = Encoding.UTF8.GetBytes(calculatedSignature);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature);

        if (!CryptographicOperations.FixedTimeEquals(calculatedBytes, providedBytes))
        {
            logger.LogWarning(
                "Presigned URL signature mismatch for access key: {AccessKey}",
                accessKey
            );
            return false;
        }

        logger.LogInformation(
            "Successfully authenticated presigned URL for access key: {AccessKey}",
            accessKey
        );
        context.Items["AccessKey"] = accessKey;
        return true;
    }

    private static bool ValidateTimestamp(string amzDate)
    {
        if (
            DateTime.TryParseExact(
                amzDate,
                "yyyyMMddTHHmmssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var requestTime
            )
        )
        {
            return Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes)
                <= MaxClockSkew.TotalMinutes;
        }
        return false;
    }

    private static string BuildCanonicalRequest(
        HttpRequest request,
        string signedHeadersStr,
        string payloadHash
    )
    {
        var method = request.Method;
        var uri = CanonicalizeUri(request.Path.Value ?? "/");
        var queryString = BuildCanonicalQueryString(
            request.QueryString.Value?.TrimStart('?') ?? ""
        );

        // Canonical headers
        var canonicalHeaders = new StringBuilder();
        var signedHeaders = signedHeadersStr.Split(';');

        foreach (var header in signedHeaders)
        {
            var value = request.Headers[header].ToString().Trim();
            canonicalHeaders.Append($"{header}:{value}\n");
        }

        return $"{method}\n{uri}\n{queryString}\n{canonicalHeaders}\n{signedHeadersStr}\n{payloadHash}";
    }

    private static string BuildCanonicalRequestWithQueryString(
        HttpRequest request,
        string signedHeadersStr,
        string payloadHash,
        string canonicalQueryString
    )
    {
        var method = request.Method;
        var uri = CanonicalizeUri(request.Path.Value ?? "/");

        var canonicalHeaders = new StringBuilder();
        var signedHeaders = signedHeadersStr.Split(';');

        foreach (var header in signedHeaders)
        {
            var value = request.Headers[header].ToString().Trim();
            canonicalHeaders.Append($"{header}:{value}\n");
        }

        return $"{method}\n{uri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeadersStr}\n{payloadHash}";
    }

    private static string CanonicalizeUri(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";

        var segments = path.Split('/');
        var encoded = new StringBuilder();
        foreach (var segment in segments)
        {
            if (encoded.Length > 0 || segment.Length == 0)
                encoded.Append('/');
            if (segment.Length > 0)
                encoded.Append(Uri.EscapeDataString(segment));
        }

        return encoded.Length == 0 ? "/" : encoded.ToString();
    }

    private static string BuildCanonicalQueryString(
        string queryString,
        bool excludeSignature = false
    )
    {
        if (string.IsNullOrEmpty(queryString))
            return "";

        var pairs = queryString
            .Split('&')
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p =>
            {
                var idx = p.IndexOf('=');
                if (idx < 0)
                    return (Key: Uri.EscapeDataString(Uri.UnescapeDataString(p)), Value: "");
                var key = Uri.EscapeDataString(Uri.UnescapeDataString(p[..idx]));
                var value = Uri.EscapeDataString(Uri.UnescapeDataString(p[(idx + 1)..]));
                return (Key: key, Value: value);
            })
            .Where(p =>
                !excludeSignature || !p.Key.Equals("X-Amz-Signature", StringComparison.Ordinal)
            )
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join("&", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    private static string BuildStringToSign(
        string amzDate,
        string dateStamp,
        string region,
        string service,
        string canonicalRequest
    )
    {
        using var sha256 = SHA256.Create();
        var canonicalRequestHash = Convert
            .ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest)))
            .ToLowerInvariant();
        return $"{Algorithm}\n{amzDate}\n{dateStamp}/{region}/{service}/aws4_request\n{canonicalRequestHash}";
    }

    private static byte[] GetSignatureKey(
        string key,
        string dateStamp,
        string regionName,
        string serviceName
    )
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + key);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, regionName);
        var kService = HmacSha256(kRegion, serviceName);
        var kSigning = HmacSha256(kService, "aws4_request");
        return kSigning;
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
