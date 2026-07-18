// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using AwesomeAssertions;
using DotBucket.Server.Cluster;
using DotBucket.Server.Configuration;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Tests.Services;

/// <summary>
/// Tests for <see cref="MultipartUploadReaperService"/>. Drives a real
/// <see cref="LocalFileSystemStorageEngine"/> against an isolated temp root
/// and exercises the scan pass directly (ExecuteAsync's loop is trivial and
/// covered by inspection). The reaper must abort uploads older than the
/// retention window and leave fresh uploads and object data untouched.
/// </summary>
public class MultipartUploadReaperServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        $"dotbucket-reaper-{Guid.NewGuid():N}"
    );

    private readonly LocalFileSystemStorageEngine _storage;
    private readonly ClusterState _cluster;

    public MultipartUploadReaperServiceTests()
    {
        Directory.CreateDirectory(_rootPath);
        var storageOptions = Options.Create(
            new StorageOptions
            {
                RootPath = _rootPath,
                MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            }
        );
        var httpClientFactory = new StaticHttpClientFactory();
        var dispatcher = new NotificationDispatcher(
            httpClientFactory,
            NullLogger<NotificationDispatcher>.Instance
        );
        _storage = new LocalFileSystemStorageEngine(
            storageOptions,
            dispatcher,
            NullLogger<LocalFileSystemStorageEngine>.Instance
        );
        // Force the schema migration. Constructor runs outside a test context,
        // so TestContext.Current.CancellationToken is not available here.
#pragma warning disable xUnit1051
        _ = _storage.ListBucketsAsync(CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore xUnit1051

        _cluster = new ClusterState(
            Options.Create(new ClusterOptions { Enabled = false }),
            NullLogger<ClusterState>.Instance
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MultipartUploadReaperService CreateReaper(LifecycleOptions options) =>
        new(
            _storage,
            _cluster,
            Options.Create(options),
            NullLogger<MultipartUploadReaperService>.Instance
        );

    /// <summary>
    /// Invoke the private ScanPassAsync via reflection. Returns the underlying
    /// Task so the caller can await it. Unwraps TargetInvocationException so
    /// test failures surface the real error from inside the scan pass.
    /// </summary>
    private static async Task InvokeScanPassAsync(
        MultipartUploadReaperService reaper,
        CancellationToken ct
    )
    {
        var method =
            typeof(MultipartUploadReaperService).GetMethod(
                "ScanPassAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            ) ?? throw new InvalidOperationException("ScanPassAsync not found via reflection.");
        try
        {
            await ((Task)method.Invoke(reaper, new object[] { ct })!);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            // Rethrow the real exception so the test runner shows the cause.
            throw ex.InnerException;
        }
    }

    private async Task CreateBucketAsync(string name) =>
        await _storage.CreateBucketAsync(name, cancellationToken: Ct);

    private async Task<string> InitiateUploadAsync(string bucket, string key)
    {
        return await _storage.InitiateMultipartUploadAsync(
            bucket,
            key,
            "application/octet-stream",
            null,
            null,
            Ct
        );
    }

    /// <summary>
    /// Backdate the created_at column on a multipart upload so the reaper sees it
    /// as stale without having to wait for the retention window to elapse.
    /// </summary>
    private async Task BackdateUploadAsync(string uploadId, DateTime initiated)
    {
        await using var conn = new SqliteConnection(
            $"Data Source={Path.Combine(_rootPath, "metadata.db")}"
        );
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE multipart_uploads SET created_at = $created WHERE upload_id = $id";
        cmd.Parameters.AddWithValue("$created", initiated.ToString("O"));
        cmd.Parameters.AddWithValue("$id", uploadId);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<int> CountUploadsAsync()
    {
        await using var conn = new SqliteConnection(
            $"Data Source={Path.Combine(_rootPath, "metadata.db")}"
        );
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM multipart_uploads";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ScanPassAsync_AbortsUploadsOlderThanRetentionWindow()
    {
        await CreateBucketAsync("b1");
        var staleId = await InitiateUploadAsync("b1", "stale.txt");
        await BackdateUploadAsync(staleId, DateTime.UtcNow.AddDays(-7));

        var reaper = CreateReaper(
            new LifecycleOptions { ReaperEnabled = true, AbortedMultipartUploadRetentionDays = 1 }
        );

        await InvokeScanPassAsync(reaper, Ct);

        (await CountUploadsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ScanPassAsync_LeavesFreshUploadsAlone()
    {
        await CreateBucketAsync("b1");
        var freshId = await InitiateUploadAsync("b1", "fresh.txt");
        // Fresh upload — created_at is "now" already.

        var reaper = CreateReaper(
            new LifecycleOptions { ReaperEnabled = true, AbortedMultipartUploadRetentionDays = 1 }
        );

        await InvokeScanPassAsync(reaper, Ct);

        (await CountUploadsAsync()).Should().Be(1);
        var remaining = (await _storage.ListMultipartUploadsAsync("b1", null, Ct)).Single();
        remaining.UploadId.Should().Be(freshId);
    }

    [Fact]
    public async Task ScanPassAsync_RespectsRetentionBoundary()
    {
        // Boundary: upload exactly at the cutoff should be KEPT (Initiated >= cutoff).
        // One second older than the cutoff should be REAPED.
        await CreateBucketAsync("b1");
        var keepId = await InitiateUploadAsync("b1", "boundary-keep.txt");
        var reapId = await InitiateUploadAsync("b1", "boundary-reap.txt");

        // 1-day retention; keep at exactly -1 day, reap at -1 day - 1 second.
        await BackdateUploadAsync(keepId, DateTime.UtcNow.AddDays(-1).AddSeconds(1));
        await BackdateUploadAsync(reapId, DateTime.UtcNow.AddDays(-1).AddSeconds(-1));

        var reaper = CreateReaper(
            new LifecycleOptions { ReaperEnabled = true, AbortedMultipartUploadRetentionDays = 1 }
        );

        await InvokeScanPassAsync(reaper, Ct);

        var remaining = (await _storage.ListMultipartUploadsAsync("b1", null, Ct)).Single();
        remaining.UploadId.Should().Be(keepId);
    }

    [Fact]
    public async Task ScanPassAsync_AbortsAllUploads_WhenRetentionIsZero()
    {
        // Retention=0 is a useful knob for tests / "abort everything in-progress".
        await CreateBucketAsync("b1");
        await InitiateUploadAsync("b1", "a.txt");
        await InitiateUploadAsync("b1", "b.txt");
        await InitiateUploadAsync("b1", "c.txt");

        var reaper = CreateReaper(
            new LifecycleOptions { ReaperEnabled = true, AbortedMultipartUploadRetentionDays = 0 }
        );

        await InvokeScanPassAsync(reaper, Ct);

        (await CountUploadsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ScanPassAsync_DoesNotTouchObjects()
    {
        // Sanity: the reaper must not delete object data, only in-progress uploads.
        await CreateBucketAsync("b1");
        var staleId = await InitiateUploadAsync("b1", "stale.txt");
        await BackdateUploadAsync(staleId, DateTime.UtcNow.AddDays(-7));

        // Put a real object — must survive the reaper pass.
        var payload = "hello world"u8.ToArray();
        using (var stream = new MemoryStream(payload))
        {
            await _storage.PutObjectAsync(
                "b1",
                "permanent.txt",
                stream,
                "text/plain",
                null,
                null,
                Ct,
                null
            );
        }

        var reaper = CreateReaper(
            new LifecycleOptions { ReaperEnabled = true, AbortedMultipartUploadRetentionDays = 1 }
        );

        await InvokeScanPassAsync(reaper, Ct);

        // Upload is gone, object is intact.
        (await CountUploadsAsync())
            .Should()
            .Be(0);
        var objects = await _storage.ListObjectsAsync("b1", null, false, Ct);
        objects
            .Select(o => o.ObjectKey)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("permanent.txt");
    }

    [Fact]
    public async Task ScanPassAsync_AbortsAcrossMultipleBuckets()
    {
        await CreateBucketAsync("alpha");
        await CreateBucketAsync("beta");
        var alphaStale = await InitiateUploadAsync("alpha", "x.txt");
        var betaStale = await InitiateUploadAsync("beta", "y.txt");
        await BackdateUploadAsync(alphaStale, DateTime.UtcNow.AddDays(-3));
        await BackdateUploadAsync(betaStale, DateTime.UtcNow.AddDays(-3));

        var reaper = CreateReaper(
            new LifecycleOptions { ReaperEnabled = true, AbortedMultipartUploadRetentionDays = 1 }
        );

        await InvokeScanPassAsync(reaper, Ct);

        (await CountUploadsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNothing_WhenReaperDisabled()
    {
        // The disabled flag short-circuits ExecuteAsync before any scan runs.
        // We drive the service through the public IHostedService surface so we
        // never touch the protected ExecuteAsync. A stale upload must remain
        // because no scan executed.
        await CreateBucketAsync("b1");
        var staleId = await InitiateUploadAsync("b1", "stale.txt");
        await BackdateUploadAsync(staleId, DateTime.UtcNow.AddDays(-7));

        // Short scan interval so the loop would fire quickly IF it ran.
        var reaper = CreateReaper(
            new LifecycleOptions { ReaperEnabled = false, ScanIntervalSeconds = 1 }
        );

        await reaper.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Give the early-return path time to execute (it's synchronous in practice).
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }
        finally
        {
            await reaper.StopAsync(TestContext.Current.CancellationToken);
        }

        (await CountUploadsAsync()).Should().Be(1);
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
