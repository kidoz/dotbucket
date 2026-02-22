// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Auth;

/// <summary>
/// Validates incoming HTTP requests matching the AWS Signature Version 4 protocol.
/// </summary>
public interface ISigV4Authenticator
{
    Task<bool> AuthenticateAsync(
        HttpContext context,
        CancellationToken cancellationToken = default
    );
}
