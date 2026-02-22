// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Configuration;

public class AuthOptions
{
    public const string SectionName = "Auth";

    public string AdminToken { get; set; } = string.Empty;
    public List<string> AllowedOrigins { get; set; } = new();
    public string RootAccessKey { get; set; } = "minioadmin";
    public string RootSecretKey { get; set; } = "minioadmin";
}
