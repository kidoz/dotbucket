// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DotBucket.Server.Configuration;

namespace DotBucket.Server.Cluster;

/// <summary>
/// Builds the primary HTTP message handler used for inter-node cluster calls,
/// optionally pinning trust to a custom CA bundle for private/enterprise CAs.
/// </summary>
public static class ClusterHttpClientFactory
{
    public static HttpMessageHandler CreateHandler(ClusterOptions options)
    {
        var handler = new SocketsHttpHandler();

        if (!string.IsNullOrWhiteSpace(options.TrustedCaBundlePath))
        {
            var customRoots = new X509Certificate2Collection();
            customRoots.ImportFromPemFile(options.TrustedCaBundlePath);

            handler.SslOptions.RemoteCertificateValidationCallback = (
                _,
                cert,
                _,
                errors
            ) =>
            {
                if (errors == SslPolicyErrors.None)
                    return true;
                if (cert is null)
                    return false;

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(customRoots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert));
            };
        }

        return handler;
    }
}
