// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Auth;

/// <summary>
/// Provides a mechanism to retrieve secret keys associated with an access key.
/// </summary>
public interface ICredentialStore
{
    Task<string?> GetSecretKeyAsync(
        string accessKey,
        CancellationToken cancellationToken = default
    );
}
