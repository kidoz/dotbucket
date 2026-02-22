// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Auth;

/// <summary>
/// A simple, static credential store for development and testing.
/// </summary>
public class StaticCredentialStore : ICredentialStore
{
    // For demo purposes only. In production, these should be stored securely.
    private readonly Dictionary<string, string> _credentials = new() { { "admin", "admin123" } };

    public Task<string?> GetSecretKeyAsync(
        string accessKey,
        CancellationToken cancellationToken = default
    )
    {
        _credentials.TryGetValue(accessKey, out var secretKey);
        return Task.FromResult(secretKey);
    }
}
