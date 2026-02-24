// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using DotBucket.Server.Configuration;
using DotBucket.Server.Iam;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Security.Cryptography;

namespace DotBucket.Server.Tests.Iam;

public class IamStoreSecurityTests
{
    [Fact]
    public async Task CreateAccessKey_EncryptsSecretAtRest_AndLookupReturnsPlainSecret()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"dotbucket-iam-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);

        try
        {
            var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var storageOptions = Options.Create(
                new StorageOptions
                {
                    RootPath = rootPath,
                    MasterKey = masterKey,
                }
            );

            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
            var dispatcher = new NotificationDispatcher(
                httpClientFactory,
                Substitute.For<ILogger<NotificationDispatcher>>()
            );
            var storage = new LocalFileSystemStorageEngine(storageOptions, dispatcher);
            await storage.ListBucketsAsync(TestContext.Current.CancellationToken);

            var store = new IamStore(storageOptions);
            await store.CreateUserAsync("alice", TestContext.Current.CancellationToken);
            await store.CreateAccessKeyWithCredentialsAsync(
                "alice",
                "AKIA_TEST_1234567890",
                "secret-plaintext-value",
                TestContext.Current.CancellationToken
            );

            var dbPath = Path.Combine(rootPath, "metadata.db");
            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT secret_key FROM iam_access_keys WHERE access_key = $ak";
            cmd.Parameters.AddWithValue("$ak", "AKIA_TEST_1234567890");
            var storedSecret = (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

            storedSecret.Should().NotBe("secret-plaintext-value");
            storedSecret.Should().StartWith("enc:v1:");

            var resolvedSecret = await store.LookupSecretKeyAsync(
                "AKIA_TEST_1234567890",
                TestContext.Current.CancellationToken
            );
            resolvedSecret.Should().Be("secret-plaintext-value");
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }
}
