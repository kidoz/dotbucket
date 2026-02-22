// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Configuration;
using DotBucket.Server.Iam;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Auth;

/// <summary>
/// Credential store that checks root user config first, then falls back to IAM database.
/// </summary>
public class DatabaseCredentialStore(IOptions<AuthOptions> authOptions, IamStore iamStore)
    : ICredentialStore
{
    private readonly AuthOptions _authOptions = authOptions.Value;

    public async Task<string?> GetSecretKeyAsync(
        string accessKey,
        CancellationToken cancellationToken = default
    )
    {
        // 1. Check root user
        if (
            !string.IsNullOrEmpty(_authOptions.RootAccessKey)
            && string.Equals(accessKey, _authOptions.RootAccessKey, StringComparison.Ordinal)
        )
        {
            return _authOptions.RootSecretKey;
        }

        // 2. Check IAM database
        return await iamStore.LookupSecretKeyAsync(accessKey, cancellationToken);
    }
}
