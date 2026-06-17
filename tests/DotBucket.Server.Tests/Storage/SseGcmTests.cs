// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Security.Cryptography;
using AwesomeAssertions;
using DotBucket.Server.Storage;

namespace DotBucket.Server.Tests.Storage;

public class SseGcmTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, byte[] key)
    {
        using var src = new MemoryStream(plaintext);
        using var dst = new MemoryStream();
        long observed = 0;
        await SseGcm.EncryptAsync(src, dst, key, c => observed += c.Length, CancellationToken.None);
        observed.Should().Be(plaintext.Length, "onPlaintext must see exactly the plaintext bytes");
        return dst.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] key)
    {
        await using var enc = new MemoryStream(ciphertext);
        await using var dec = SseGcm.CreateDecryptingStream(enc, key);
        using var outMs = new MemoryStream();
        await dec.CopyToAsync(outMs);
        return outMs.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(SseGcm.ChunkSize - 1)]
    [InlineData(SseGcm.ChunkSize)]
    [InlineData(SseGcm.ChunkSize + 1)]
    [InlineData(3 * SseGcm.ChunkSize + 123)]
    public async Task RoundTrips_AcrossChunkBoundaries(int size)
    {
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(size);

        var ciphertext = await EncryptAsync(plaintext, key);

        // Ciphertext carries a header + per-frame auth tags, so it is strictly larger.
        ciphertext.Length.Should().BeGreaterThan(plaintext.Length);
        ciphertext[0].Should().Be(SseGcm.FormatVersion);

        var decrypted = await DecryptAsync(ciphertext, key);
        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public async Task Tampering_WithCiphertext_IsDetected()
    {
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(SseGcm.ChunkSize + 500);
        var ciphertext = await EncryptAsync(plaintext, key);

        // Flip a byte inside the first frame's ciphertext (after the 8-byte header).
        ciphertext[20] ^= 0xFF;

        var act = async () => await DecryptAsync(ciphertext, key);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task Truncation_IsDetected()
    {
        var key = Key();
        var plaintext = RandomNumberGenerator.GetBytes(2 * SseGcm.ChunkSize);
        var ciphertext = await EncryptAsync(plaintext, key);

        // Drop the trailing bytes (the final frame), simulating a truncated object.
        var truncated = ciphertext[..(ciphertext.Length - 32)];

        var act = async () => await DecryptAsync(truncated, key);
        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task WrongKey_FailsAuthentication()
    {
        var plaintext = RandomNumberGenerator.GetBytes(1024);
        var ciphertext = await EncryptAsync(plaintext, Key());

        var act = async () => await DecryptAsync(ciphertext, Key());
        await act.Should().ThrowAsync<CryptographicException>();
    }
}
