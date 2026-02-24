// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotBucket.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DotBucket.Server.Tests.Auth;

public class SigV4AuthenticatorTests
{
    private readonly ICredentialStore _credentialStore = Substitute.For<ICredentialStore>();
    private readonly ILogger<SigV4Authenticator> _logger = Substitute.For<
        ILogger<SigV4Authenticator>
    >();
    private readonly SigV4Authenticator _sut;

    private const string TestAccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string TestSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string TestRegion = "us-east-1";
    private const string TestService = "s3";

    public SigV4AuthenticatorTests()
    {
        _sut = new SigV4Authenticator(_credentialStore, _logger);
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsFalse_WhenAuthorizationHeaderIsMissing()
    {
        // Arrange
        var context = new DefaultHttpContext();

        // Act
        var result = await _sut.AuthenticateAsync(
            context,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsFalse_WhenAccessKeyIsUnknown()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization =
            "AWS4-HMAC-SHA256 Credential=unknown/20260221/us-east-1/s3/aws4_request, SignedHeaders=host, Signature=abc";
        _credentialStore
            .GetSecretKeyAsync("unknown", Arg.Any<CancellationToken>())
            .Returns((string?)null);

        // Act
        var result = await _sut.AuthenticateAsync(
            context,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsFalse_WhenPayloadHashMismatch()
    {
        // Arrange: sign with one body hash, but put a different body
        var dateTime = DateTime.UtcNow;
        var body = "original-body"u8.ToArray();
        var tamperedBody = "tampered-body"u8.ToArray();
        var bodyHash = ComputeSha256Hex(body);

        var context = CreateSignedContext(
            HttpMethod.Put,
            "/test-bucket/test-key",
            bodyHash,
            body,
            dateTime
        );

        // Replace body with tampered content (keeping original hash in header)
        context.Request.Body = new MemoryStream(tamperedBody);

        _credentialStore
            .GetSecretKeyAsync(TestAccessKey, Arg.Any<CancellationToken>())
            .Returns(TestSecretKey);

        // Act
        var result = await _sut.AuthenticateAsync(
            context,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsTrue_WhenPayloadHashMatches()
    {
        // Arrange
        var dateTime = DateTime.UtcNow;
        var body = "hello-world"u8.ToArray();
        var bodyHash = ComputeSha256Hex(body);

        var context = CreateSignedContext(
            HttpMethod.Put,
            "/test-bucket/test-key",
            bodyHash,
            body,
            dateTime
        );

        _credentialStore
            .GetSecretKeyAsync(TestAccessKey, Arg.Any<CancellationToken>())
            .Returns(TestSecretKey);

        // Act
        var result = await _sut.AuthenticateAsync(
            context,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_Accepts_UnsignedPayload()
    {
        // Arrange: use UNSIGNED-PAYLOAD — body hash validation should be skipped
        var dateTime = DateTime.UtcNow;
        var body = "any-body-content"u8.ToArray();

        var context = CreateSignedContext(
            HttpMethod.Put,
            "/test-bucket/test-key",
            "UNSIGNED-PAYLOAD",
            body,
            dateTime
        );

        _credentialStore
            .GetSecretKeyAsync(TestAccessKey, Arg.Any<CancellationToken>())
            .Returns(TestSecretKey);

        // Act
        var result = await _sut.AuthenticateAsync(
            context,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsFalse_WhenTimestampOutsideWindow()
    {
        // Arrange: use a timestamp 20 minutes in the past (outside +-15 min window)
        var dateTime = DateTime.UtcNow.AddMinutes(-20);
        var body = Array.Empty<byte>();
        var bodyHash = ComputeSha256Hex(body);

        var context = CreateSignedContext(
            HttpMethod.Get,
            "/test-bucket",
            bodyHash,
            body,
            dateTime
        );

        _credentialStore
            .GetSecretKeyAsync(TestAccessKey, Arg.Any<CancellationToken>())
            .Returns(TestSecretKey);

        // Act
        var result = await _sut.AuthenticateAsync(
            context,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().BeFalse();
    }

    // ========================================================================
    // SigV4 Test Helper
    // ========================================================================

    private DefaultHttpContext CreateSignedContext(
        HttpMethod method,
        string path,
        string payloadHash,
        byte[] body,
        DateTime dateTime
    )
    {
        var amzDate = dateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = dateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = "localhost";

        var context = new DefaultHttpContext();
        context.Request.Method = method.Method;
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        context.Request.Headers["x-amz-date"] = amzDate;
        context.Request.Headers["x-amz-content-sha256"] = payloadHash;
        context.Request.Body = new MemoryStream(body);

        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var signature = ComputeSigV4Signature(
            TestAccessKey,
            TestSecretKey,
            method.Method,
            path,
            host,
            amzDate,
            payloadHash,
            signedHeaders,
            dateStamp,
            TestRegion,
            TestService
        );

        context.Request.Headers.Authorization =
            $"AWS4-HMAC-SHA256 Credential={TestAccessKey}/{dateStamp}/{TestRegion}/{TestService}/aws4_request, SignedHeaders={signedHeaders}, Signature={signature}";

        return context;
    }

    private static string ComputeSigV4Signature(
        string accessKey,
        string secretKey,
        string method,
        string path,
        string host,
        string amzDate,
        string payloadHash,
        string signedHeaders,
        string dateStamp,
        string region,
        string service
    )
    {
        // Build canonical headers
        var canonicalHeaders =
            $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";

        // Canonicalize URI
        var uri = CanonicalizeUri(path);

        // Build canonical request
        var canonicalRequest =
            $"{method}\n{uri}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        // Build string to sign
        using var sha256 = SHA256.Create();
        var canonicalRequestHash = Convert
            .ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest)))
            .ToLowerInvariant();
        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{dateStamp}/{region}/{service}/aws4_request\n{canonicalRequestHash}";

        // Derive signing key
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        var kSigning = HmacSha256(kService, "aws4_request");

        return Convert.ToHexString(HmacSha256(kSigning, stringToSign)).ToLowerInvariant();
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

    private static string ComputeSha256Hex(byte[] data)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(data)).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
