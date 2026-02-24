// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Routing;

public static class RoutingConstants
{
    public static readonly HashSet<string> ReservedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "health",
        "_internal",
        "openapi",
        "assets",
        "favicon.ico",
        "robots.txt",
        "dotbucket-logo.svg",
        "index.html"
    };

    public static bool IsReservedPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && ReservedPrefixes.Contains(segments[0]))
            return true;

        return false;
    }
}
