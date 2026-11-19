// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using DotBucket.Server.Configuration;
using DotBucket.Server.Services;
using DotBucket.Server.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DotBucket.Server.Tests.Storage;

public class StorageEngineHardeningTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        $"dotbucket-hardening-{Guid.NewGuid():N}"
    );
    private readonly LocalFileSystemStorageEngine _storage;

    private readonly IOptions<StorageOptions> _options;
    private readonly NotificationDispatcher _dispatcher;

    public StorageEngineHardeningTests()
    {
        Directory.CreateDirectory(_rootPath);
        _options = Options.Create(
            new StorageOptions
            {
                RootPath = _rootPath,
                MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        _dispatcher = new NotificationDispatcher(
            httpClientFactory,
            Substitute.For<ILogger<NotificationDispatcher>>()
        );
        _storage = new LocalFileSystemStorageEngine(
            _options,
            _dispatcher,
            Substitute.For<ILogger<LocalFileSystemStorageEngine>>()
        );
    }

    private static string Md5ETag(byte[] data) =>
        $"\"{Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant()}\"";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RestartPreservesLegacyAndNewObjectsWithTemporarySuffix()
    {
        await _storage.CreateBucketAsync("documents", false, Ct);
        byte[] bytes = [1, 3, 5];
        foreach (var key in new[] { "legacy.tmp", "current.tmp" })
            await _storage.PutObjectAsync(
                "documents",
                key,
                new MemoryStream(bytes),
                "text/plain",
                cancellationToken: Ct
            );
        // Model the on-disk layout from before the immutable-blob migration.
        await File.WriteAllBytesAsync(
            Path.Combine(_rootPath, "documents", "legacy.tmp"),
            bytes,
            Ct
        );
        using (
            var db = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={Path.Combine(_rootPath, "metadata.db")}"
            )
        )
        {
            await db.OpenAsync(Ct);
            using var command = db.CreateCommand();
            command.CommandText =
                "UPDATE objects SET blob_id = NULL WHERE object_key = 'legacy.tmp'";
            await command.ExecuteNonQueryAsync(Ct);
        }
        var restarted = new LocalFileSystemStorageEngine(
            _options,
            _dispatcher,
            Substitute.For<ILogger<LocalFileSystemStorageEngine>>()
        );
        foreach (var key in new[] { "legacy.tmp", "current.tmp" })
        {
            var result = await restarted.GetObjectAsync("documents", key, cancellationToken: Ct);
            Assert.NotNull(result);
            await using var content = result.Value.Content;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, Ct);
            Assert.Equal(bytes, copy.ToArray());
        }
    }

    [Fact]
    public async Task MultipartOperationsRequireTheStoredUploadIdentity()
    {
        const string bucket = "documents";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        var upload = await _storage.InitiateMultipartUploadAsync(
            bucket,
            "original",
            "text/plain",
            cancellationToken: Ct
        );
        var tag = await _storage.UploadPartAsync(
            bucket,
            "original",
            upload,
            1,
            new MemoryStream([1]),
            Ct
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.ListPartsAsync(bucket, "other", upload, Ct)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.UploadPartAsync(bucket, "other", upload, 1, new MemoryStream([2]), Ct)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.CompleteMultipartUploadAsync(bucket, "other", upload, [(1, tag)], Ct)
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.AbortMultipartUploadAsync(bucket, "other", upload, Ct)
        );
        Assert.Equal(
            tag,
            Assert.Single(await _storage.ListPartsAsync(bucket, "original", upload, Ct)).ETag
        );
    }

    // ---- SSE-S3 (#1): plaintext Size/ETag + decryptable round-trip ----

    [Fact]
    public async Task EncryptedPut_StoresPlaintextSizeAndEtag_AndRoundTrips()
    {
        var bucket = "enc-bucket";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        var plaintext = RandomNumberGenerator.GetBytes(200_000); // > one GCM chunk

        var put = await _storage.PutObjectAsync(
            bucket,
            "obj",
            new MemoryStream(plaintext),
            "application/octet-stream",
            null,
            "AES256",
            Ct
        );

        // Size and ETag must describe the PLAINTEXT, not the ciphertext.
        put.Size.Should().Be(plaintext.Length);
        put.ETag.Should().Be(Md5ETag(plaintext));

        var head = await _storage.HeadObjectAsync(bucket, "obj", null, Ct);
        head!.Size.Should().Be(plaintext.Length);

        var get = await _storage.GetObjectAsync(bucket, "obj", null, Ct);
        using var ms = new MemoryStream();
        await get!.Value.Content.CopyToAsync(ms, Ct);
        ms.ToArray().Should().Equal(plaintext);
    }

    // ---- Object Lock (#6) ----

    [Fact]
    public async Task ObjectLockConfig_RejectedWhenBucketNotCreatedWithLock()
    {
        var bucket = "nolock";
        await _storage.CreateBucketAsync(bucket, false, Ct);

        var act = async () =>
            await _storage.SetObjectLockConfigAsync(
                bucket,
                new DotBucket.Server.Models.ObjectLockConfig { Enabled = true },
                Ct
            );

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be("InvalidBucketState");
    }

    [Fact]
    public async Task Retention_OnMissingKey_ThrowsNoSuchKey()
    {
        var bucket = "lock";
        await _storage.CreateBucketAsync(bucket, objectLock: true, Ct);

        var act = async () =>
            await _storage.SetObjectRetentionAsync(
                bucket,
                "ghost",
                null,
                "GOVERNANCE",
                DateTime.UtcNow.AddDays(1),
                Ct
            );

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be("NoSuchKey");
    }

    [Fact]
    public async Task Retention_OnBucketWithoutLock_ThrowsInvalidRequest()
    {
        var bucket = "plainbucket";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        await _storage.PutObjectAsync(
            bucket,
            "obj",
            new MemoryStream(Encoding.UTF8.GetBytes("x")),
            "text/plain",
            null,
            null,
            Ct
        );

        var act = async () =>
            await _storage.SetObjectRetentionAsync(
                bucket,
                "obj",
                null,
                "GOVERNANCE",
                DateTime.UtcNow.AddDays(1),
                Ct
            );

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be("InvalidRequest");
    }

    // ---- Multipart (#2) ----

    [Fact]
    public async Task UploadPart_ReturnsMd5Etag()
    {
        var bucket = "mp";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        var uploadId = await _storage.InitiateMultipartUploadAsync(
            bucket,
            "k",
            "text/plain",
            null,
            null,
            Ct
        );
        var part = Encoding.UTF8.GetBytes("hello part");

        var etag = await _storage.UploadPartAsync(
            bucket,
            "k",
            uploadId,
            1,
            new MemoryStream(part),
            Ct
        );

        etag.Should().Be(Md5ETag(part));
    }

    [Fact]
    public async Task Complete_WithMismatchedEtag_ThrowsInvalidPart()
    {
        var bucket = "mp2";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        var uploadId = await _storage.InitiateMultipartUploadAsync(
            bucket,
            "k",
            "text/plain",
            null,
            null,
            Ct
        );
        await _storage.UploadPartAsync(
            bucket,
            "k",
            uploadId,
            1,
            new MemoryStream(Encoding.UTF8.GetBytes("data")),
            Ct
        );

        var act = async () =>
            await _storage.CompleteMultipartUploadAsync(
                bucket,
                "k",
                uploadId,
                new[] { (1, "\"deadbeefdeadbeefdeadbeefdeadbeef\"") },
                Ct
            );

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be("InvalidPart");
    }

    [Fact]
    public async Task Complete_WithDescendingParts_ThrowsInvalidPartOrder()
    {
        var bucket = "mp3";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        var uploadId = await _storage.InitiateMultipartUploadAsync(
            bucket,
            "k",
            "text/plain",
            null,
            null,
            Ct
        );
        var p1 = Encoding.UTF8.GetBytes("aaaa");
        var p2 = Encoding.UTF8.GetBytes("bbbb");
        var e1 = await _storage.UploadPartAsync(bucket, "k", uploadId, 1, new MemoryStream(p1), Ct);
        var e2 = await _storage.UploadPartAsync(bucket, "k", uploadId, 2, new MemoryStream(p2), Ct);

        var act = async () =>
            await _storage.CompleteMultipartUploadAsync(
                bucket,
                "k",
                uploadId,
                new[] { (2, e2), (1, e1) },
                Ct
            );

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be("InvalidPartOrder");
    }

    [Fact]
    public async Task Complete_WithTooSmallNonLastPart_ThrowsEntityTooSmall()
    {
        var bucket = "mp4";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        var uploadId = await _storage.InitiateMultipartUploadAsync(
            bucket,
            "k",
            "text/plain",
            null,
            null,
            Ct
        );
        var p1 = Encoding.UTF8.GetBytes("small-first-part"); // < 5 MiB and not the last part
        var p2 = Encoding.UTF8.GetBytes("second");
        var e1 = await _storage.UploadPartAsync(bucket, "k", uploadId, 1, new MemoryStream(p1), Ct);
        var e2 = await _storage.UploadPartAsync(bucket, "k", uploadId, 2, new MemoryStream(p2), Ct);

        var act = async () =>
            await _storage.CompleteMultipartUploadAsync(
                bucket,
                "k",
                uploadId,
                new[] { (1, e1), (2, e2) },
                Ct
            );

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Be("EntityTooSmall");
    }

    [Fact]
    public async Task Complete_SinglePart_HappyPath_RoundTrips()
    {
        var bucket = "mp5";
        await _storage.CreateBucketAsync(bucket, false, Ct);
        var uploadId = await _storage.InitiateMultipartUploadAsync(
            bucket,
            "k",
            "text/plain",
            null,
            null,
            Ct
        );
        var part = Encoding.UTF8.GetBytes("the only part, allowed to be small");
        var etag = await _storage.UploadPartAsync(
            bucket,
            "k",
            uploadId,
            1,
            new MemoryStream(part),
            Ct
        );

        var result = await _storage.CompleteMultipartUploadAsync(
            bucket,
            "k",
            uploadId,
            new[] { (1, etag) },
            Ct
        );

        result.Size.Should().Be(part.Length);
        result.ETag.Should().EndWith("-1\""); // multipart ETag carries the part count
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
