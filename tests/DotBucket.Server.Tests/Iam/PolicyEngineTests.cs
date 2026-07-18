// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using AwesomeAssertions;
using DotBucket.Server.Configuration;
using DotBucket.Server.Iam;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Tests.Iam;

/// <summary>
/// Unit tests for <see cref="PolicyEngine"/>. The engine is the most security-
/// sensitive code path in the IAM surface, so these cover the precedence rules
/// (root bypass, explicit-deny-wins, implicit-deny default), wildcard matching,
/// cache invalidation, and disabled-user / unknown-key edge cases.
///
/// A real <see cref="IamStore"/> against an isolated temp SQLite DB is used
/// (matching the pattern in <see cref="IamStoreSecurityTests"/>). IamStore is a
/// concrete class with a required-argument constructor and is not safely
/// substitutable, and seeding real data keeps the cache-invalidation tests
/// faithful to the production read path.
/// </summary>
public class PolicyEngineTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        $"dotbucket-policy-test-{Guid.NewGuid():N}"
    );

    private readonly string _masterKey;

    public PolicyEngineTests()
    {
        Directory.CreateDirectory(_rootPath);
        _masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private IamStore CreateStore()
    {
        // IamStore does not own its schema migrations — the storage engine does
        // (LocalFileSystemStorageEngine runs the CREATE TABLE statements on
        // first connection). Touch the engine once to provision the IAM tables,
        // matching the production startup sequence (Program.cs force-resolves
        // IStorageEngine before IAM seeding) and IamStoreSecurityTests.
        var storageOptions = Options.Create(
            new StorageOptions { RootPath = _rootPath, MasterKey = _masterKey }
        );
        var httpClientFactory = new StaticHttpClientFactory();
        var dispatcher = new NotificationDispatcher(
            httpClientFactory,
            NullLogger<NotificationDispatcher>.Instance
        );
        var storage = new LocalFileSystemStorageEngine(
            storageOptions,
            dispatcher,
            NullLogger<LocalFileSystemStorageEngine>.Instance
        );
        _ = storage.ListBucketsAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new IamStore(storageOptions);
    }

    /// <summary>
    /// Minimal IHttpClientFactory for the notification dispatcher. Returns a
    /// plain HttpClient; no webhook calls are made by these tests.
    /// </summary>
    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private PolicyEngine CreateEngine(AuthOptions? authOptions = null, IamStore? store = null)
    {
        var opts = Options.Create(authOptions ?? new AuthOptions());
        return new PolicyEngine(store ?? CreateStore(), opts, NullLogger<PolicyEngine>.Instance);
    }

    private static S3AuthorizationContext Context(
        string action,
        string resource,
        string accessKey
    ) =>
        new()
        {
            Action = action,
            Resource = resource,
            AccessKey = accessKey,
        };

    private static IamPolicyDocument Doc(params IamPolicyStatement[] statements) =>
        new() { Statement = statements.ToList() };

    private static IamPolicyStatement Statement(
        string effect,
        List<string> actions,
        List<string> resources
    ) =>
        new()
        {
            Effect = effect,
            Action = actions,
            Resource = resources,
        };

    /// <summary>
    /// Seeds a user with an access key, then creates and attaches the supplied
    /// policy documents (each as a one-statement policy with a unique name).
    /// Returns the access key to use in <see cref="Context"/>.
    /// </summary>
    private static async Task<string> SeedUserWithPoliciesAsync(
        IamStore store,
        string userName,
        IEnumerable<IamPolicyDocument> docs,
        CancellationToken ct
    )
    {
        await store.CreateUserAsync(userName, ct);
        var accessKey = await store.CreateAccessKeyAsync(userName, ct);
        var i = 0;
        foreach (var doc in docs)
        {
            var policyName = $"{userName}-policy-{i++}";
            await store.CreatePolicyAsync(policyName, doc, ct: ct);
            await store.AttachPolicyAsync(policyName, "user", userName, ct);
        }
        return accessKey.AccessKey;
    }

    // --- Root bypass ---

    [Fact]
    public async Task EvaluateAsync_RootAccessKey_BypassesAllChecks()
    {
        // Root bypass must not even touch the store: the engine returns Allow
        // before any lookup. We still construct a real store to prove the path
        // is never reached.
        var store = CreateStore();
        var engine = CreateEngine(new AuthOptions { RootAccessKey = "ROOTAKIA" }, store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::any", "ROOTAKIA"),
            TestContext.Current.CancellationToken
        );

        result.Should().Be(AuthorizationResult.Allow);
    }

    // --- Unknown / disabled users ---

    [Fact]
    public async Task EvaluateAsync_UnknownAccessKey_IsImplicitDeny()
    {
        var engine = CreateEngine();
        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/key", "AKIA_UNKNOWN"),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.ImplicitDeny);
    }

    [Fact]
    public async Task EvaluateAsync_DisabledAccessKey_IsImplicitDeny()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "carol",
            new[] { Doc(Statement("Allow", ["s3:*"], ["arn:aws:s3:::*"])) },
            TestContext.Current.CancellationToken
        );

        await store.SetAccessKeyStatusAsync(
            accessKey,
            "inactive",
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::b/k", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.ImplicitDeny);
    }

    [Fact]
    public async Task EvaluateAsync_DisabledUser_IsImplicitDeny()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "dave",
            new[] { Doc(Statement("Allow", ["s3:*"], ["arn:aws:s3:::*"])) },
            TestContext.Current.CancellationToken
        );

        await store.SetUserStatusAsync("dave", "disabled", TestContext.Current.CancellationToken);
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::b/k", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.ImplicitDeny);
    }

    // --- Implicit deny default ---

    [Fact]
    public async Task EvaluateAsync_NoMatchingStatement_IsImplicitDeny()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "alice",
            new[] { Doc(Statement("Allow", ["s3:PutObject"], ["arn:aws:s3:::bucket/*"])) },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/key", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.ImplicitDeny);
    }

    [Fact]
    public async Task EvaluateAsync_NoPoliciesAttached_IsImplicitDeny()
    {
        var store = CreateStore();
        await store.CreateUserAsync("nobody", TestContext.Current.CancellationToken);
        var accessKey = await store.CreateAccessKeyAsync(
            "nobody",
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/key", accessKey.AccessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.ImplicitDeny);
    }

    // --- Allow path ---

    [Fact]
    public async Task EvaluateAsync_MatchingAllow_IsAllow()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "alice",
            new[]
            {
                Doc(
                    Statement("Allow", ["s3:GetObject", "s3:PutObject"], ["arn:aws:s3:::bucket/*"])
                ),
            },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/key", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.Allow);
    }

    // --- Explicit deny wins ---

    [Fact]
    public async Task EvaluateAsync_DenyOverridesAllowAcrossPolicies_IsDeny()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "alice",
            new[]
            {
                Doc(Statement("Allow", ["s3:*"], ["arn:aws:s3:::bucket/*"])),
                Doc(Statement("Deny", ["s3:DeleteObject"], ["arn:aws:s3:::bucket/protected/*"])),
            },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:DeleteObject", "arn:aws:s3:::bucket/protected/secret", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.Deny);
    }

    [Fact]
    public async Task EvaluateAsync_DenyInSamePolicyAsAllow_IsDeny()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "alice",
            new[]
            {
                Doc(
                    Statement("Allow", ["s3:GetObject"], ["arn:aws:s3:::bucket/*"]),
                    Statement("Deny", ["s3:GetObject"], ["arn:aws:s3:::bucket/secret"])
                ),
            },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/secret", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.Deny);
    }

    [Fact]
    public async Task EvaluateAsync_DenyOnDifferentResource_StillAllowsOthers()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "alice",
            new[]
            {
                Doc(
                    Statement("Allow", ["s3:GetObject"], ["arn:aws:s3:::bucket/*"]),
                    Statement("Deny", ["s3:GetObject"], ["arn:aws:s3:::bucket/secret"])
                ),
            },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var allowed = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/public", accessKey),
            TestContext.Current.CancellationToken
        );
        var denied = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/secret", accessKey),
            TestContext.Current.CancellationToken
        );

        allowed.Should().Be(AuthorizationResult.Allow);
        denied.Should().Be(AuthorizationResult.Deny);
    }

    // --- Effect case-insensitivity ---

    [Theory]
    [InlineData("allow")]
    [InlineData("ALLOW")]
    [InlineData("Allow")]
    public async Task EvaluateAsync_EffectIsCaseInsensitive(string effect)
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "casey",
            new[] { Doc(Statement(effect, ["s3:GetObject"], ["arn:aws:s3:::b/*"])) },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::b/k", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.Allow);
    }

    // --- Wildcard matching ---

    [Theory]
    [InlineData("s3:*", "s3:GetObject")]
    [InlineData("s3:Get*", "s3:GetObject")]
    [InlineData("s3:GetObj*", "s3:GetObject")]
    [InlineData("s3:Get?bject", "s3:GetObject")]
    [InlineData("s3:GetObject", "s3:GetObject")]
    public async Task EvaluateAsync_ActionWildcards_MatchAsExpected(string pattern, string action)
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "alex",
            new[] { Doc(Statement("Allow", [pattern], ["arn:aws:s3:::b/*"])) },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context(action, "arn:aws:s3:::b/k", accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.Allow);
    }

    [Theory]
    [InlineData("arn:aws:s3:::*", "arn:aws:s3:::bucket/key")]
    [InlineData("arn:aws:s3:::bucket/*", "arn:aws:s3:::bucket/deep/nested/key")]
    [InlineData("arn:aws:s3:::bucket/file*", "arn:aws:s3:::bucket/file.txt")]
    [InlineData("arn:aws:s3:::bucket/?.txt", "arn:aws:s3:::bucket/a.txt")]
    public async Task EvaluateAsync_ResourceWildcards_MatchAsExpected(
        string pattern,
        string resource
    )
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "ruth",
            new[] { Doc(Statement("Allow", ["s3:GetObject"], [pattern])) },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var result = await engine.EvaluateAsync(
            Context("s3:GetObject", resource, accessKey),
            TestContext.Current.CancellationToken
        );
        result.Should().Be(AuthorizationResult.Allow);
    }

    [Fact]
    public async Task EvaluateAsync_ResourceWithoutWildcard_MustMatchExactly()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "elliot",
            new[] { Doc(Statement("Allow", ["s3:GetObject"], ["arn:aws:s3:::bucket/exact-key"])) },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var exact = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/exact-key", accessKey),
            TestContext.Current.CancellationToken
        );
        var nested = await engine.EvaluateAsync(
            Context("s3:GetObject", "arn:aws:s3:::bucket/exact-key/extra", accessKey),
            TestContext.Current.CancellationToken
        );

        exact.Should().Be(AuthorizationResult.Allow);
        nested.Should().Be(AuthorizationResult.ImplicitDeny);
    }

    // --- Multi-action / multi-resource within a single statement ---

    [Fact]
    public async Task EvaluateAsync_AnyActionAndAnyResourceMatch_Allows()
    {
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "morgan",
            new[]
            {
                Doc(
                    Statement(
                        "Allow",
                        ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
                        ["arn:aws:s3:::a/*", "arn:aws:s3:::b/*"]
                    )
                ),
            },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        (
            await engine.EvaluateAsync(
                Context("s3:PutObject", "arn:aws:s3:::b/key", accessKey),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(AuthorizationResult.Allow);
        (
            await engine.EvaluateAsync(
                Context("s3:GetObject", "arn:aws:s3:::a/key", accessKey),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(AuthorizationResult.Allow);
        (
            await engine.EvaluateAsync(
                Context("s3:GetObject", "arn:aws:s3:::c/key", accessKey),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(AuthorizationResult.ImplicitDeny);
    }

    // --- Cache invalidation ---

    [Fact]
    public async Task InvalidateCache_ForcesNextEvaluationToRefetch()
    {
        // Two policies: allow GetObject only. After invalidation, the engine
        // must observe an attached DeleteObject-allow policy that was added
        // between evaluations (without waiting for the 60s TTL).
        var store = CreateStore();
        var accessKey = await SeedUserWithPoliciesAsync(
            store,
            "ivy",
            new[] { Doc(Statement("Allow", ["s3:GetObject"], ["arn:aws:s3:::b/*"])) },
            TestContext.Current.CancellationToken
        );
        var engine = CreateEngine(store: store);

        var before = await engine.EvaluateAsync(
            Context("s3:DeleteObject", "arn:aws:s3:::b/k", accessKey),
            TestContext.Current.CancellationToken
        );
        before.Should().Be(AuthorizationResult.ImplicitDeny);

        // Admin grants delete permission + invalidates cache, simulating the
        // IamEndpoints mutation path (which calls engine.InvalidateCache()).
        await store.CreatePolicyAsync(
            "ivy-delete",
            Doc(Statement("Allow", ["s3:GetObject", "s3:DeleteObject"], ["arn:aws:s3:::b/*"])),
            ct: TestContext.Current.CancellationToken
        );
        await store.AttachPolicyAsync(
            "ivy-delete",
            "user",
            "ivy",
            TestContext.Current.CancellationToken
        );
        engine.InvalidateCache();

        var after = await engine.EvaluateAsync(
            Context("s3:DeleteObject", "arn:aws:s3:::b/k", accessKey),
            TestContext.Current.CancellationToken
        );
        after.Should().Be(AuthorizationResult.Allow);
    }

    [Fact]
    public async Task InvalidateCache_TakesEffectAcrossUserBases()
    {
        // Sanity: invalidation clears the whole cache, so a brand new user's
        // evaluation must observe fresh state immediately.
        var store = CreateStore();
        var engine = CreateEngine(store: store);

        await store.CreateUserAsync("kara", TestContext.Current.CancellationToken);
        var accessKey = await store.CreateAccessKeyAsync(
            "kara",
            TestContext.Current.CancellationToken
        );

        // First evaluation populates the (empty) cache for kara.
        (
            await engine.EvaluateAsync(
                Context("s3:GetObject", "arn:aws:s3:::b/k", accessKey.AccessKey),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(AuthorizationResult.ImplicitDeny);

        await store.CreatePolicyAsync(
            "kara-get",
            Doc(Statement("Allow", ["s3:GetObject"], ["arn:aws:s3:::b/*"])),
            ct: TestContext.Current.CancellationToken
        );
        await store.AttachPolicyAsync(
            "kara-get",
            "user",
            "kara",
            TestContext.Current.CancellationToken
        );
        engine.InvalidateCache();

        (
            await engine.EvaluateAsync(
                Context("s3:GetObject", "arn:aws:s3:::b/k", accessKey.AccessKey),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(AuthorizationResult.Allow);
    }
}
