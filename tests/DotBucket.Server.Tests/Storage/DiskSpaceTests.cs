// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Net;
using System.Security.Cryptography;
using AwesomeAssertions;
using DotBucket.Server.Configuration;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotBucket.Server.Tests.Storage;

/// <summary>
/// Tests for the disk-space guarding added to <see cref="LocalFileSystemStorageEngine"/>.
/// Covers three behaviors:
///
/// 1. Pre-write refusal: when <see cref="StorageOptions.MinFreeSpaceBytes"/> /
///    <see cref="StorageOptions.MinFreeSpacePercent"/> thresholds are violated, a
///    PUT throws <see cref="StorageWriteException"/> with code
///    <see cref="StorageWriteErrorCodes.LowDiskSpace"/> BEFORE any bytes are written.
/// 2. ENOSPC classification: an <see cref="IOException"/> with the ENOSPC HResult is
///    classified as <see cref="StorageWriteErrorCodes.DiskFull"/> (the mid-write
///    failure path that the catch blocks wrap).
/// 3. Normal writes succeed when the thresholds are not set or the disk has space.
///
/// We cannot deterministically fill a real disk in a unit test, so the pre-write
/// refusal is exercised by setting MinFreeSpaceBytes to long.MaxValue (every real
/// disk is below that). The mid-write ENOSPC classifier is exercised via reflection
/// on the static helper.
/// </summary>
public class DiskSpaceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        $"dotbucket-diskspace-{Guid.NewGuid():N}"
    );

    public DiskSpaceTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LocalFileSystemStorageEngine CreateEngine(StorageOptions options)
    {
        var opts = Options.Create(options);
        var httpClientFactory = new StaticHttpClientFactory();
        var dispatcher = new NotificationDispatcher(
            httpClientFactory,
            NullLogger<NotificationDispatcher>.Instance
        );
        var engine = new LocalFileSystemStorageEngine(
            opts,
            dispatcher,
            NullLogger<LocalFileSystemStorageEngine>.Instance
        );
        // Force the schema migration.
        _ = engine.ListBucketsAsync(CancellationToken.None).GetAwaiter().GetResult();
        return engine;
    }

    private static StorageOptions BaseOptions() =>
        new()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"dotbucket-diskspace-{Guid.NewGuid():N}"),
            MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };

    // --- GetStorageRoot ---

    [Fact]
    public void GetStorageRoot_ReturnsDataRoot()
    {
        var opts = BaseOptions();
        opts.RootPath = _rootPath;
        Directory.CreateDirectory(_rootPath);
        var engine = CreateEngine(opts);

        engine.GetStorageRoot().Should().Be(_rootPath);
    }

    [Fact]
    public void GetStorageRoot_IncludesBasePrefix_WhenSet()
    {
        var opts = BaseOptions();
        opts.RootPath = _rootPath;
        opts.BasePrefix = "tenant-a";
        var engine = CreateEngine(opts);

        engine.GetStorageRoot().Should().EndWith("tenant-a");
    }

    // --- Pre-write refusal (MinFreeSpaceBytes) ---

    [Fact]
    public async Task PutObject_RefusesWrite_WhenMinFreeSpaceBytesExceedsAvailable()
    {
        var opts = BaseOptions();
        opts.RootPath = _rootPath;
        // long.MaxValue => every real disk fails the threshold; the guard must
        // reject the write before touching disk.
        opts.MinFreeSpaceBytes = long.MaxValue;
        opts.MinFreeSpacePercent = null;
        var engine = CreateEngine(opts);
        await engine.CreateBucketAsync("b1", cancellationToken: Ct);

        using var stream = new MemoryStream("hello"u8.ToArray());
        var act = async () =>
            await engine.PutObjectAsync("b1", "k", stream, "text/plain", null, null, Ct, null);

        var ex = await act.Should().ThrowAsync<StorageWriteException>();
        ex.Which.Code.Should().Be(StorageWriteErrorCodes.LowDiskSpace);
        ex.Which.InnerException.Should().BeOfType<IOException>();
    }

    [Fact]
    public async Task PutObject_Succeeds_WhenMinFreeSpaceBytesIsNull()
    {
        var opts = BaseOptions();
        opts.RootPath = _rootPath;
        opts.MinFreeSpaceBytes = null;
        opts.MinFreeSpacePercent = null;
        var engine = CreateEngine(opts);
        await engine.CreateBucketAsync("b1", cancellationToken: Ct);

        using var stream = new MemoryStream("hello"u8.ToArray());
        var act = async () =>
            await engine.PutObjectAsync("b1", "k", stream, "text/plain", null, null, Ct, null);

        await act.Should().NotThrowAsync();
    }

    // --- Pre-write refusal (MinFreeSpacePercent) ---

    [Fact]
    public async Task PutObject_RefusesWrite_WhenMinFreeSpacePercentExceedsAvailable()
    {
        var opts = BaseOptions();
        opts.RootPath = _rootPath;
        opts.MinFreeSpaceBytes = null;
        // 100% free required — no disk ever satisfies this.
        opts.MinFreeSpacePercent = 100.0;
        var engine = CreateEngine(opts);
        await engine.CreateBucketAsync("b1", cancellationToken: Ct);

        using var stream = new MemoryStream("hello"u8.ToArray());
        var ex = await Assert.ThrowsAsync<StorageWriteException>(() =>
            engine.PutObjectAsync("b1", "k", stream, "text/plain", null, null, Ct, null)
        );
        ex.Code.Should().Be(StorageWriteErrorCodes.LowDiskSpace);
    }

    [Fact]
    public async Task PutObject_Succeeds_WhenMinFreeSpacePercentIsZero()
    {
        var opts = BaseOptions();
        opts.RootPath = _rootPath;
        opts.MinFreeSpaceBytes = null;
        // 0% threshold = feature disabled.
        opts.MinFreeSpacePercent = 0.0;
        var engine = CreateEngine(opts);
        await engine.CreateBucketAsync("b1", cancellationToken: Ct);

        using var stream = new MemoryStream("hello"u8.ToArray());
        await engine.PutObjectAsync("b1", "k", stream, "text/plain", null, null, Ct, null);

        // Verify the object actually landed.
        var objects = await engine.ListObjectsAsync("b1", null, false, Ct);
        objects.Should().ContainSingle();
    }

    // --- Multipart paths get the same guard ---

    [Fact]
    public async Task UploadPart_RefusesWrite_WhenMinFreeSpaceBytesExceedsAvailable()
    {
        var opts = BaseOptions();
        opts.RootPath = _rootPath;
        opts.MinFreeSpaceBytes = long.MaxValue;
        opts.MinFreeSpacePercent = null;
        var engine = CreateEngine(opts);
        await engine.CreateBucketAsync("b1", cancellationToken: Ct);
        var uploadId = await engine.InitiateMultipartUploadAsync(
            "b1",
            "k",
            "application/octet-stream",
            null,
            null,
            Ct
        );

        using var stream = new MemoryStream("part-data"u8.ToArray());
        var ex = await Assert.ThrowsAsync<StorageWriteException>(() =>
            engine.UploadPartAsync("b1", "k", uploadId, 1, stream, Ct)
        );
        ex.Code.Should().Be(StorageWriteErrorCodes.LowDiskSpace);
    }

    // --- ENOSPC classification (catch-block path) ---

    private static string InvokeClassify(IOException ioex)
    {
        var method =
            typeof(LocalFileSystemStorageEngine).GetMethod(
                "ClassifyWriteFailure",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            ) ?? throw new InvalidOperationException("ClassifyWriteFailure not found.");
        var result = (StorageWriteException)method.Invoke(null, new object[] { ioex })!;
        return result.Code;
    }

    [Fact]
    public void ClassifyWriteFailure_ENOSPC_HResult_MapsToDiskFull()
    {
        // 0x80070070 = HRESULT for ERROR_DISK_FULL (POSIX ENOSPC).
        var ioex = new IOException("No space left on device", unchecked((int)0x80070070));
        InvokeClassify(ioex).Should().Be(StorageWriteErrorCodes.DiskFull);
    }

    [Fact]
    public void ClassifyWriteFailure_EDQUOT_HResult_MapsToDiskFull()
    {
        // 0x80070027 = HRESULT for EDQUOT (disk quota exceeded) in this codebase's mapping.
        var ioex = new IOException("Disk quota exceeded", unchecked((int)0x80070027));
        InvokeClassify(ioex).Should().Be(StorageWriteErrorCodes.DiskFull);
    }

    [Fact]
    public void ClassifyWriteFailure_PlainIOException_MapsToIoError()
    {
        // A non-ENOSPC IO failure (e.g. permission denied) must not be classified
        // as DiskFull — that would mislead operators into thinking the disk is full.
        var ioex = new IOException("Permission denied");
        InvokeClassify(ioex).Should().Be(StorageWriteErrorCodes.IoError);
    }

    [Fact]
    public void ClassifyWriteFailure_MessageHeuristic_DetectsENOSPC()
    {
        // On some platforms the HResult isn't propagated; fall back to the message.
        var ioex = new IOException("No space left on device");
        InvokeClassify(ioex).Should().Be(StorageWriteErrorCodes.DiskFull);
    }

    // --- StorageWriteException surface ---

    [Fact]
    public void StorageWriteException_PreservesCodeAndInnerException()
    {
        var inner = new IOException("boom");
        var ex = new StorageWriteException(StorageWriteErrorCodes.DiskFull, "out of space", inner);

        ex.Code.Should().Be(StorageWriteErrorCodes.DiskFull);
        ex.Message.Should().Be("out of space");
        ex.InnerException.Should().BeSameAs(inner);
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
