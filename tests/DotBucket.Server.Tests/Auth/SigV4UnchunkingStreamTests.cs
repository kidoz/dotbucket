// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using DotBucket.Server.Auth;

namespace DotBucket.Server.Tests.Auth;

public class SigV4UnchunkingStreamTests
{
    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string AmzDate = "20260611T120000Z";
    private const string Scope = "20260611/us-east-1/s3/aws4_request";

    private static readonly byte[] SigningKey = Encoding.UTF8.GetBytes(
        "test-signing-key-32-bytes-long!!"
    );
    private static readonly string SeedSignature = new('a', 64);

    private static string HmacHex(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return Convert
            .ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)))
            .ToLowerInvariant();
    }

    private static string SignChunk(string previousSignature, byte[] chunkData)
    {
        var chunkHash = Convert.ToHexString(SHA256.HashData(chunkData)).ToLowerInvariant();
        var stringToSign =
            $"AWS4-HMAC-SHA256-PAYLOAD\n{AmzDate}\n{Scope}\n{previousSignature}\n{EmptySha256}\n{chunkHash}";
        return HmacHex(SigningKey, stringToSign);
    }

    private static byte[] BuildSignedBody(out string finalSignature, params byte[][] chunks)
    {
        var body = new MemoryStream();
        var previous = SeedSignature;

        foreach (var chunk in chunks)
        {
            var signature = SignChunk(previous, chunk);
            var header = Encoding.ASCII.GetBytes(
                $"{chunk.Length:x};chunk-signature={signature}\r\n"
            );
            body.Write(header);
            body.Write(chunk);
            body.Write("\r\n"u8);
            previous = signature;
        }

        finalSignature = SignChunk(previous, []);
        body.Write(Encoding.ASCII.GetBytes($"0;chunk-signature={finalSignature}\r\n"));
        body.Write("\r\n"u8);
        body.Position = 0;
        return body.ToArray();
    }

    private static SigV4UnchunkingStream CreateSignedStream(
        byte[] body,
        long expectedDecodedLength,
        bool hasTrailer = false
    ) =>
        new(
            new MemoryStream(body),
            expectedDecodedLength,
            hasTrailer,
            new ChunkSigningContext(SigningKey, SeedSignature, AmzDate, Scope)
        );

    [Fact]
    public async Task ReadAsync_DecodesPayload_WhenChunkSignaturesAreValid()
    {
        // Arrange
        var chunk1 = Encoding.UTF8.GetBytes(new string('x', 100));
        var chunk2 = Encoding.UTF8.GetBytes("hello world");
        var body = BuildSignedBody(out _, chunk1, chunk2);
        await using var stream = CreateSignedStream(body, chunk1.Length + chunk2.Length);

        // Act
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, TestContext.Current.CancellationToken);

        // Assert
        output.ToArray().Should().Equal([.. chunk1, .. chunk2]);
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenChunkDataIsTampered()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("original payload data");
        var body = BuildSignedBody(out _, chunk);
        // Flip a byte inside the chunk data (after the header line).
        var headerLength = Array.IndexOf(body, (byte)'\n') + 1;
        body[headerLength] ^= 0xFF;
        await using var stream = CreateSignedStream(body, chunk.Length);

        // Act
        var act = async () =>
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        };

        // Assert
        await act.Should().ThrowAsync<SigV4StreamingException>();
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenChunkSignatureIsWrong()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("payload");
        var bogusSignature = new string('b', 64);
        var body = Encoding
            .ASCII.GetBytes($"{chunk.Length:x};chunk-signature={bogusSignature}\r\n")
            .Concat(chunk)
            .Concat("\r\n"u8.ToArray())
            .ToArray();
        await using var stream = CreateSignedStream(body, chunk.Length);

        // Act
        var act = async () =>
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        };

        // Assert
        await act.Should().ThrowAsync<SigV4StreamingException>();
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenChunkSignatureIsMissingInSignedMode()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("payload");
        var body = Encoding
            .ASCII.GetBytes($"{chunk.Length:x}\r\n")
            .Concat(chunk)
            .Concat("\r\n0\r\n\r\n"u8.ToArray())
            .ToArray();
        await using var stream = CreateSignedStream(body, chunk.Length);

        // Act
        var act = async () =>
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        };

        // Assert
        await act.Should().ThrowAsync<SigV4StreamingException>();
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenPayloadExceedsDeclaredDecodedLength()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("twenty-one byte chunk");
        var body = BuildSignedBody(out _, chunk);
        await using var stream = CreateSignedStream(body, expectedDecodedLength: 5);

        // Act
        var act = async () =>
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        };

        // Assert
        await act.Should()
            .ThrowAsync<SigV4StreamingException>()
            .WithMessage("*exceeds x-amz-decoded-content-length*");
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenPayloadIsShorterThanDeclaredDecodedLength()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("short");
        var body = BuildSignedBody(out _, chunk);
        await using var stream = CreateSignedStream(body, expectedDecodedLength: 100);

        // Act
        var act = async () =>
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        };

        // Assert
        await act.Should()
            .ThrowAsync<SigV4StreamingException>()
            .WithMessage("*does not match x-amz-decoded-content-length*");
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenChunkSizeIsMalformed()
    {
        // Arrange
        var body = "not-hex;chunk-signature=abc\r\n"u8.ToArray();
        await using var stream = CreateSignedStream(body, 10);

        // Act
        var act = async () =>
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        };

        // Assert
        await act.Should()
            .ThrowAsync<SigV4StreamingException>()
            .WithMessage("*Malformed chunk size*");
    }

    [Fact]
    public async Task ReadAsync_DecodesPayloadAndConsumesTrailer_InUnsignedTrailerMode()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("unsigned trailer payload");
        var body = Encoding
            .ASCII.GetBytes($"{chunk.Length:x}\r\n")
            .Concat(chunk)
            .Concat("\r\n0\r\nx-amz-checksum-crc32c:wdBDMA==\r\n\r\n"u8.ToArray())
            .ToArray();
        await using var stream = new SigV4UnchunkingStream(
            new MemoryStream(body),
            chunk.Length,
            hasTrailer: true
        );

        // Act
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, TestContext.Current.CancellationToken);

        // Assert
        output.ToArray().Should().Equal(chunk);
    }

    [Fact]
    public async Task ReadAsync_ValidatesTrailerSignature_InSignedTrailerMode()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("signed trailer payload");
        var chunkSignature = SignChunk(SeedSignature, chunk);
        var finalSignature = SignChunk(chunkSignature, []);

        const string trailerName = "x-amz-checksum-crc32c";
        const string trailerValue = "wdBDMA==";
        var trailerHash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{trailerName}:{trailerValue}\n")))
            .ToLowerInvariant();
        var trailerSignature = HmacHex(
            SigningKey,
            $"AWS4-HMAC-SHA256-TRAILER\n{AmzDate}\n{Scope}\n{finalSignature}\n{trailerHash}"
        );

        var body = Encoding
            .ASCII.GetBytes($"{chunk.Length:x};chunk-signature={chunkSignature}\r\n")
            .Concat(chunk)
            .Concat(
                Encoding.ASCII.GetBytes(
                    $"\r\n0;chunk-signature={finalSignature}\r\n{trailerName}:{trailerValue}\r\nx-amz-trailer-signature:{trailerSignature}\r\n\r\n"
                )
            )
            .ToArray();
        await using var stream = CreateSignedStream(body, chunk.Length, hasTrailer: true);

        // Act
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, TestContext.Current.CancellationToken);

        // Assert
        output.ToArray().Should().Equal(chunk);
    }

    [Fact]
    public async Task ReadAsync_Throws_WhenTrailerSignatureIsMissing_InSignedTrailerMode()
    {
        // Arrange
        var chunk = Encoding.UTF8.GetBytes("payload");
        var chunkSignature = SignChunk(SeedSignature, chunk);
        var finalSignature = SignChunk(chunkSignature, []);
        var body = Encoding
            .ASCII.GetBytes($"{chunk.Length:x};chunk-signature={chunkSignature}\r\n")
            .Concat(chunk)
            .Concat(
                Encoding.ASCII.GetBytes(
                    $"\r\n0;chunk-signature={finalSignature}\r\nx-amz-checksum-crc32c:wdBDMA==\r\n\r\n"
                )
            )
            .ToArray();
        await using var stream = CreateSignedStream(body, chunk.Length, hasTrailer: true);

        // Act
        var act = async () =>
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        };

        // Assert
        await act.Should()
            .ThrowAsync<SigV4StreamingException>()
            .WithMessage("*x-amz-trailer-signature*");
    }
}
