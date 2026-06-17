// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DotBucket.Server.Storage;

/// <summary>
/// Streaming authenticated encryption for SSE-S3 using AES-256-GCM in a chunked
/// "STREAM"-style construction. Data is encrypted/decrypted in fixed-size frames so that
/// arbitrarily large objects can be processed without buffering the whole object in memory,
/// while every byte is integrity-protected (tamper, truncation, and reordering are detected).
///
/// On-disk layout:
///   [1 byte version=0x02][7 byte random nonce prefix]   (the "header", also used as GCM AAD)
///   then one or more frames:
///     non-final frame: exactly ChunkSize bytes ciphertext + 16 byte tag
///     final   frame:   &lt; (ChunkSize + 16) bytes  (0..ChunkSize-1 ciphertext + 16 byte tag)
///   Per-frame nonce = noncePrefix(7) || counter(4, big-endian) || lastFlag(1).
///
/// Because non-final frames are always exactly ChunkSize+16 bytes and the final frame is always
/// strictly smaller, the reader can unambiguously detect the final frame (and thus truncation)
/// from frame length alone.
/// </summary>
public static class SseGcm
{
    public const byte FormatVersion = 0x02;
    public const int ChunkSize = 64 * 1024;
    private const int TagSize = 16;
    private const int NoncePrefixSize = 7;
    private const int NonceSize = 12;
    private const int HeaderSize = 1 + NoncePrefixSize;

    private static void BuildNonce(
        ReadOnlySpan<byte> noncePrefix,
        uint counter,
        bool last,
        Span<byte> nonce
    )
    {
        noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.Slice(NoncePrefixSize, 4), counter);
        nonce[NonceSize - 1] = last ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Encrypts <paramref name="source"/> (plaintext) to <paramref name="destination"/>
    /// (ciphertext) while invoking <paramref name="onPlaintext"/> for every plaintext span read,
    /// allowing the caller to compute the plaintext size and MD5 (ETag) in the same pass.
    /// </summary>
    public static async Task EncryptAsync(
        Stream source,
        Stream destination,
        byte[] key,
        Action<ReadOnlyMemory<byte>> onPlaintext,
        CancellationToken cancellationToken
    )
    {
        var writer = new GcmEncryptingWriter(destination, key);
        await writer.WriteHeaderAsync(cancellationToken);
        var readBuffer = new byte[ChunkSize];
        int read;
        while ((read = await source.ReadAsync(readBuffer, cancellationToken)) > 0)
        {
            onPlaintext(readBuffer.AsMemory(0, read));
            await writer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken);
        }
        await writer.FinishAsync(cancellationToken);
    }

    /// <summary>
    /// Push-based authenticated encryptor: callers write plaintext spans and call
    /// <see cref="FinishAsync"/> exactly once. Used when the plaintext is produced incrementally
    /// (e.g. concatenating multipart parts) rather than read from a single source stream.
    /// </summary>
    public sealed class GcmEncryptingWriter
    {
        private readonly Stream _destination;
        private readonly AesGcm _gcm;
        private readonly byte[] _header = new byte[HeaderSize];
        private readonly byte[] _plainChunk = new byte[ChunkSize];
        private readonly byte[] _frame = new byte[ChunkSize + TagSize];
        private readonly byte[] _nonce = new byte[NonceSize];
        private uint _counter;
        private int _filled;
        private bool _headerWritten;
        private bool _finished;

        public GcmEncryptingWriter(Stream destination, byte[] key)
        {
            _destination = destination;
            _gcm = new AesGcm(key, TagSize);
            _header[0] = FormatVersion;
            RandomNumberGenerator.Fill(_header.AsSpan(1, NoncePrefixSize));
        }

        public async ValueTask WriteHeaderAsync(CancellationToken cancellationToken)
        {
            if (_headerWritten)
                return;
            await _destination.WriteAsync(_header, cancellationToken);
            _headerWritten = true;
        }

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken
        )
        {
            await WriteHeaderAsync(cancellationToken);
            var offset = 0;
            while (offset < data.Length)
            {
                var toCopy = Math.Min(ChunkSize - _filled, data.Length - offset);
                data.Slice(offset, toCopy).CopyTo(_plainChunk.AsMemory(_filled));
                _filled += toCopy;
                offset += toCopy;
                if (_filled == ChunkSize)
                {
                    await FlushFrameAsync(ChunkSize, last: false, cancellationToken);
                    _filled = 0;
                }
            }
        }

        public async ValueTask FinishAsync(CancellationToken cancellationToken)
        {
            if (_finished)
                return;
            await WriteHeaderAsync(cancellationToken);
            // Always emit exactly one final frame (plaintext length 0..ChunkSize-1) so the
            // reader can detect a properly terminated (non-truncated) stream.
            await FlushFrameAsync(_filled, last: true, cancellationToken);
            _filled = 0;
            _finished = true;
            _gcm.Dispose();
        }

        private async ValueTask FlushFrameAsync(int length, bool last, CancellationToken ct)
        {
            BuildNonce(_header.AsSpan(1, NoncePrefixSize), _counter, last, _nonce);
            _gcm.Encrypt(
                _nonce,
                _plainChunk.AsSpan(0, length),
                _frame.AsSpan(0, length),
                _frame.AsSpan(length, TagSize),
                _header
            );
            await _destination.WriteAsync(_frame.AsMemory(0, length + TagSize), ct);
            _counter++;
        }
    }

    /// <summary>
    /// Wraps an encrypted file stream and exposes the decrypted plaintext as a forward-only,
    /// non-seekable read stream. Authentication failures (tampering/truncation) surface as a
    /// <see cref="CryptographicException"/> while reading.
    /// </summary>
    public static Stream CreateDecryptingStream(Stream encrypted, byte[] key) =>
        new GcmDecryptingStream(encrypted, key);

    private sealed class GcmDecryptingStream : Stream
    {
        private readonly Stream _inner;
        private readonly AesGcm _gcm;
        private readonly byte[] _header = new byte[HeaderSize];
        private readonly byte[] _frame = new byte[ChunkSize + TagSize];
        private readonly byte[] _plain = new byte[ChunkSize];
        private readonly byte[] _nonce = new byte[NonceSize];
        private bool _headerRead;
        private bool _eof;
        private uint _counter;
        private int _plainOffset;
        private int _plainLength;

        public GcmDecryptingStream(Stream inner, byte[] key)
        {
            _inner = inner;
            _gcm = new AesGcm(key, TagSize);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (!_headerRead)
            {
                await ReadExactAsync(_header, HeaderSize, cancellationToken);
                if (_header[0] != FormatVersion)
                    throw new CryptographicException("Unsupported SSE object format version.");
                _headerRead = true;
            }

            if (_plainOffset >= _plainLength)
            {
                if (_eof)
                    return 0;
                await DecryptNextFrameAsync(cancellationToken);
                if (_plainLength == 0 && _eof)
                    return 0;
            }

            var available = _plainLength - _plainOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _plain.AsMemory(_plainOffset, toCopy).CopyTo(buffer);
            _plainOffset += toCopy;
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        private async Task DecryptNextFrameAsync(CancellationToken cancellationToken)
        {
            var n = await FillAsync(_frame, _frame.Length, cancellationToken);
            if (n < TagSize)
            {
                if (n == 0)
                    throw new CryptographicException(
                        "Encrypted object is truncated (missing final frame)."
                    );
                throw new CryptographicException("Encrypted object frame is corrupt.");
            }

            // A short frame (< ChunkSize + tag) is the final frame; a full-size frame is not.
            var last = n < _frame.Length;
            var cipherLen = n - TagSize;
            BuildNonce(_header.AsSpan(1, NoncePrefixSize), _counter, last, _nonce);
            _gcm.Decrypt(
                _nonce,
                _frame.AsSpan(0, cipherLen),
                _frame.AsSpan(cipherLen, TagSize),
                _plain.AsSpan(0, cipherLen),
                _header
            );
            _counter++;
            _plainOffset = 0;
            _plainLength = cipherLen;
            if (last)
                _eof = true;
        }

        // Reads up to "count" bytes; returns the number actually read (0 at clean EOF).
        private async Task<int> FillAsync(byte[] target, int count, CancellationToken ct)
        {
            var total = 0;
            while (total < count)
            {
                var read = await _inner.ReadAsync(target.AsMemory(total, count - total), ct);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }

        private async Task ReadExactAsync(byte[] target, int count, CancellationToken ct)
        {
            if (await FillAsync(target, count, ct) != count)
                throw new CryptographicException("Encrypted object header is truncated.");
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _gcm.Dispose();
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
