// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Configuration;
using DotBucket.Server.Iam;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Auth;

/// <summary>
/// A credential store that fetches secrets from the IAM database,
/// with fallback to environment-configured root credentials.
/// </summary>
public class ConfigurableCredentialStore(IamStore iamStore, IOptions<AuthOptions> authOptions)
    : ICredentialStore
{
    private readonly AuthOptions _auth = authOptions.Value;

    public async Task<string?> GetSecretKeyAsync(
        string accessKey,
        CancellationToken cancellationToken = default
    )
    {
        // 1. Check Root Credentials (Highest Priority)
        if (!string.IsNullOrEmpty(_auth.RootAccessKey) && accessKey == _auth.RootAccessKey)
        {
            return _auth.RootSecretKey;
        }

        // 2. Check IAM Database
        var secretKey = await iamStore.LookupSecretKeyAsync(accessKey, cancellationToken);
        if (secretKey != null)
        {
            return secretKey;
        }

        return null;
    }
}
