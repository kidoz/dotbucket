// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

public class AuthOptions
{
    public const string SectionName = "Auth";

    public string AdminToken { get; set; } = string.Empty;
    public List<string> AllowedOrigins { get; set; } = new();
    public string RootAccessKey { get; set; } = string.Empty;
    public string RootSecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Controls handling of the AWS session token (x-amz-security-token).
    /// "Ignore" (default) accepts static access keys regardless of a token.
    /// "Reject" denies any request that carries a session token.
    /// </summary>
    public string SessionTokenMode { get; set; } = "Ignore";
}
