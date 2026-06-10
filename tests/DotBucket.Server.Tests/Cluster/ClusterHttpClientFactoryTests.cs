// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;

namespace DotBucket.Server.Tests.Cluster;

public class ClusterHttpClientFactoryTests
{
    private static X509Certificate2 CreateSelfSigned(string cn)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={cn}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
    }

    [Fact]
    public void CreateHandler_WithoutBundle_HasNoCustomValidation()
    {
        var handler = ClusterHttpClientFactory.CreateHandler(new ClusterOptions());

        handler.Should().BeOfType<SocketsHttpHandler>();
        ((SocketsHttpHandler)handler)
            .SslOptions.RemoteCertificateValidationCallback.Should()
            .BeNull();
    }

    [Fact]
    public void CreateHandler_WithBundle_TrustsChainedCertAndRejectsOthers()
    {
        using var trusted = CreateSelfSigned("dotbucket-test-ca");
        using var untrusted = CreateSelfSigned("rogue");

        var pemPath = Path.Combine(Path.GetTempPath(), $"ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(pemPath, trusted.ExportCertificatePem());

        try
        {
            var handler = (SocketsHttpHandler)
                ClusterHttpClientFactory.CreateHandler(
                    new ClusterOptions { TrustedCaBundlePath = pemPath }
                );

            var callback = handler.SslOptions.RemoteCertificateValidationCallback;
            callback.Should().NotBeNull();

            // A cert in the custom trust store validates; an unrelated one does not.
            // (errors is forced non-None so the custom-root path is exercised.)
            callback!(this, trusted, null, SslPolicyErrors.RemoteCertificateChainErrors)
                .Should()
                .BeTrue();
            callback!(this, untrusted, null, SslPolicyErrors.RemoteCertificateChainErrors)
                .Should()
                .BeFalse();
        }
        finally
        {
            File.Delete(pemPath);
        }
    }
}
