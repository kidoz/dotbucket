// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

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
            .GetSecretKeyAsync("unknown", TestContext.Current.CancellationToken)
            .Returns((string?)null);

        // Act
        var result = await _sut.AuthenticateAsync(
            context,
            TestContext.Current.CancellationToken
        );

        // Assert
        result.Should().BeFalse();
    }
}
