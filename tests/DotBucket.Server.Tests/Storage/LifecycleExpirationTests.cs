// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Models;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DotBucket.Server.Tests.Storage;

public class LifecycleExpirationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        $"dotbucket-lifecycle-{Guid.NewGuid():N}"
    );
    private readonly LocalFileSystemStorageEngine _storage;

    public LifecycleExpirationTests()
    {
        Directory.CreateDirectory(_rootPath);
        var options = Options.Create(
            new StorageOptions
            {
                RootPath = _rootPath,
                MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var dispatcher = new NotificationDispatcher(
            httpClientFactory,
            Substitute.For<ILogger<NotificationDispatcher>>()
        );
        _storage = new LocalFileSystemStorageEngine(options, dispatcher);
    }

    private async Task<StorageObject> PutAsync(string bucket, string key, CancellationToken ct)
    {
        return await _storage.PutObjectAsync(
            bucket,
            key,
            new MemoryStream(Encoding.UTF8.GetBytes("data")),
            "text/plain",
            cancellationToken: ct
        );
    }

    private LifecycleExpirationService CreateService(LifecycleOptions opts)
    {
        // Non-distributed ClusterState (Enabled = false => IsDistributed = false).
        var cluster = new ClusterState(
            Options.Create(new ClusterOptions { Enabled = false }),
            Substitute.For<ILogger<ClusterState>>()
        );
        return new LifecycleExpirationService(
            _storage,
            cluster,
            Options.Create(opts),
            Substitute.For<ILogger<LifecycleExpirationService>>()
        );
    }

    [Fact]
    public async Task ListExpiredObjects_ReturnsOnlyOlderMatchingObjects()
    {
        var ct = TestContext.Current.CancellationToken;
        await _storage.CreateBucketAsync("b", false, ct);
        await PutAsync("b", "tmp/old.txt", ct);
        await PutAsync("b", "keep/new.txt", ct);

        // cutoff in the future => everything matches; prefix filters to tmp/
        var expired = (
            await _storage.ListExpiredObjectsAsync("b", "tmp/", DateTime.UtcNow.AddDays(1), 100, ct)
        ).ToList();

        expired.Should().ContainSingle();
        expired[0].Key.Should().Be("tmp/old.txt");
    }

    [Fact]
    public async Task ListExpiredObjects_ExcludesObjectsNewerThanCutoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await _storage.CreateBucketAsync("b", false, ct);
        await PutAsync("b", "x.txt", ct);

        // cutoff in the past => nothing is old enough
        var expired = await _storage.ListExpiredObjectsAsync(
            "b",
            null,
            DateTime.UtcNow.AddDays(-1),
            100,
            ct
        );

        expired.Should().BeEmpty();
    }

    [Fact]
    public async Task Service_HardDeletesExpiredObject_InUnversionedBucket()
    {
        var ct = TestContext.Current.CancellationToken;
        await _storage.CreateBucketAsync("b", false, ct);
        await PutAsync("b", "gone.txt", ct);
        await _storage.SetLifecycleAsync(
            "b",
            new LifecycleConfiguration
            {
                Rules = [new LifecycleRule { Enabled = true, ExpirationDays = 0 }],
            },
            ct
        );

        // Days=0 with cutoff=now means anything created at/before now expires.
        var service = CreateService(new LifecycleOptions { ScanIntervalSeconds = 1 });
        await InvokeScanAsync(service, ct);

        var head = await _storage.HeadObjectAsync("b", "gone.txt", cancellationToken: ct);
        head.Should().BeNull();
    }

    [Fact]
    public async Task Service_SkipsLegalHeldObject()
    {
        var ct = TestContext.Current.CancellationToken;
        await _storage.CreateBucketAsync("b", true, ct);
        // Object-lock buckets are versioned, so the object has a real version id.
        var put = await PutAsync("b", "locked.txt", ct);
        await _storage.SetObjectLegalHoldAsync("b", "locked.txt", put.VersionId, true, ct);
        await _storage.SetLifecycleAsync(
            "b",
            new LifecycleConfiguration
            {
                Rules = [new LifecycleRule { Enabled = true, ExpirationDays = 0 }],
            },
            ct
        );

        var service = CreateService(new LifecycleOptions { ScanIntervalSeconds = 1 });
        await InvokeScanAsync(service, ct);

        var head = await _storage.HeadObjectAsync("b", "locked.txt", cancellationToken: ct);
        head.Should().NotBeNull(); // protected object survives expiration
    }

    // Invokes a single scan pass via the private ScanPassAsync method using reflection,
    // avoiding the timing of the BackgroundService loop.
    private static async Task InvokeScanAsync(
        LifecycleExpirationService service,
        CancellationToken ct
    )
    {
        var method = typeof(LifecycleExpirationService).GetMethod(
            "ScanPassAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        )!;
        await (Task)method.Invoke(service, [ct])!;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch { }
    }
}
