// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using DotBucket.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Iam;

public class PolicyEngine(
    IamStore store,
    IOptions<AuthOptions> authOptions,
    ILogger<PolicyEngine> logger
)
{
    private readonly AuthOptions _authOptions = authOptions.Value;
    private readonly ConcurrentDictionary<
        string,
        (List<IamPolicyDocument> Docs, DateTime CachedAt)
    > _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const int MaxCacheSize = 1000;

    public async Task<AuthorizationResult> EvaluateAsync(
        S3AuthorizationContext authContext,
        CancellationToken ct = default
    )
    {
        // Root user bypasses all checks
        if (
            !string.IsNullOrEmpty(_authOptions.RootAccessKey)
            && string.Equals(
                authContext.AccessKey,
                _authOptions.RootAccessKey,
                StringComparison.Ordinal
            )
        )
        {
            return AuthorizationResult.Allow;
        }

        // Resolve user from access key
        var userName = await store.LookupUserNameByAccessKeyAsync(authContext.AccessKey, ct);
        if (userName == null)
        {
            logger.LogWarning(
                "No IAM user found for access key {AccessKey}",
                authContext.AccessKey
            );
            return AuthorizationResult.ImplicitDeny;
        }

        // Get effective policies (with cache)
        var policies = await GetCachedPoliciesAsync(userName, ct);

        var hasAllow = false;

        foreach (var doc in policies)
        {
            foreach (var statement in doc.Statement)
            {
                if (!MatchesAction(statement.Action, authContext.Action))
                    continue;
                if (!MatchesResource(statement.Resource, authContext.Resource))
                    continue;

                if (string.Equals(statement.Effect, "Deny", StringComparison.OrdinalIgnoreCase))
                    return AuthorizationResult.Deny;

                if (string.Equals(statement.Effect, "Allow", StringComparison.OrdinalIgnoreCase))
                    hasAllow = true;
            }
        }

        return hasAllow ? AuthorizationResult.Allow : AuthorizationResult.ImplicitDeny;
    }

    public void InvalidateCache()
    {
        _cache.Clear();
    }

    private async Task<List<IamPolicyDocument>> GetCachedPoliciesAsync(
        string userName,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;

        if (_cache.TryGetValue(userName, out var cached) && now - cached.CachedAt < CacheTtl)
            return cached.Docs;

        var docs = await store.GetEffectivePoliciesForUserAsync(userName, ct);

        // Evict stale entries when cache grows too large
        if (_cache.Count >= MaxCacheSize)
        {
            foreach (var kvp in _cache)
            {
                if (now - kvp.Value.CachedAt >= CacheTtl)
                    _cache.TryRemove(kvp.Key, out _);
            }
        }

        _cache[userName] = (docs, now);
        return docs;
    }

    private static bool MatchesAction(List<string> patterns, string action)
    {
        foreach (var pattern in patterns)
        {
            if (WildcardMatch(pattern, action))
                return true;
        }
        return false;
    }

    private static bool MatchesResource(List<string> patterns, string resource)
    {
        foreach (var pattern in patterns)
        {
            if (WildcardMatch(pattern, resource))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Iterative two-pointer wildcard matching (supports * and ?).
    /// </summary>
    internal static bool WildcardMatch(string pattern, string text)
    {
        int pIdx = 0,
            tIdx = 0;
        int pStar = -1,
            tStar = -1;

        while (tIdx < text.Length)
        {
            if (pIdx < pattern.Length && (pattern[pIdx] == '?' || pattern[pIdx] == text[tIdx]))
            {
                pIdx++;
                tIdx++;
            }
            else if (pIdx < pattern.Length && pattern[pIdx] == '*')
            {
                pStar = pIdx;
                tStar = tIdx;
                pIdx++;
            }
            else if (pStar >= 0)
            {
                pIdx = pStar + 1;
                tStar++;
                tIdx = tStar;
            }
            else
            {
                return false;
            }
        }

        while (pIdx < pattern.Length && pattern[pIdx] == '*')
            pIdx++;

        return pIdx == pattern.Length;
    }
}
