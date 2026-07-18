// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DotBucket.Server.Auth;

/// <summary>
/// Thrown when an aws-chunked request body fails decoding or chunk signature validation.
/// </summary>
public class SigV4StreamingException(string message) : IOException(message);

/// <summary>
/// Signing material needed to validate per-chunk signatures, derived from the
/// already-verified seed (header) signature of the request.
/// </summary>
public record ChunkSigningContext(
    byte[] SigningKey,
    string SeedSignature,
    string AmzDate,
    string CredentialScope
);

/// <summary>
/// Decodes an aws-chunked request body and validates each chunk's signature against
/// the AWS SigV4 streaming specification (AWS4-HMAC-SHA256-PAYLOAD). The chunk
/// signatures form a chain rooted at the seed signature; any tampered, reordered,
/// or truncated chunk fails validation and aborts the stream.
/// When <see cref="ChunkSigningContext"/> is null (STREAMING-UNSIGNED-PAYLOAD-TRAILER),
/// only framing and total decoded length are enforced.
/// </summary>
public class SigV4UnchunkingStream : Stream
{
    private const string EmptySha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const int MaxHeaderLineLength = 4096;
    private const int LineBufferSize = 4096;

    private readonly Stream _inner;
    private readonly long _expectedDecodedLength;
    private readonly bool _hasTrailer;
    private readonly ChunkSigningContext? _signing;

    private long _remainingInChunk;
    private long _totalDecoded;
    private bool _done;
    private string _previousSignature;
    private string _currentChunkSignature = "";
    private IncrementalHash? _chunkHash;

    // Buffered reader state for chunk framing lines. The wire format is short
    // text lines terminated by \r\n, so reading one byte at a time via _inner
    // was a syscall-per-byte cliff. We pull up to LineBufferSize bytes at a
    // time into _lineBuffer and drain it line-by-line.
    private readonly byte[] _lineBuffer = new byte[LineBufferSize];
    private int _lineBufferOffset;
    private int _lineBufferCount;

    public SigV4UnchunkingStream(
        Stream inner,
        long expectedDecodedLength,
        bool hasTrailer = false,
        ChunkSigningContext? signingContext = null
    )
    {
        _inner = inner;
        _expectedDecodedLength = expectedDecodedLength;
        _hasTrailer = hasTrailer;
        _signing = signingContext;
        _previousSignature = signingContext?.SeedSignature ?? "";
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
        if (_done)
            return 0;

        if (_remainingInChunk == 0)
        {
            var startedChunk = await BeginChunkAsync(cancellationToken);
            if (!startedChunk)
            {
                await FinishStreamAsync(cancellationToken);
                return 0;
            }
        }

        int toRead = (int)Math.Min((long)buffer.Length, _remainingInChunk);

        // Drain any payload bytes that were prefetched into the framing line
        // buffer first; only then read fresh bytes from the inner stream. The
        // line buffer is refilled in 4 KiB chunks during ReadLineAsync, so it
        // frequently holds the start of the next chunk's data.
        int read = 0;
        if (_lineBufferOffset < _lineBufferCount)
        {
            read = Math.Min(toRead, _lineBufferCount - _lineBufferOffset);
            new Span<byte>(_lineBuffer, _lineBufferOffset, read).CopyTo(buffer.Span[..read]);
            _lineBufferOffset += read;
        }

        if (read < toRead)
        {
            int fromInner = await _inner.ReadAsync(buffer[read..toRead], cancellationToken);
            if (fromInner == 0 && read == 0 && toRead > 0)
                throw new SigV4StreamingException("Unexpected end of stream inside a chunk.");
            read += fromInner;
        }

        if (read == 0 && toRead > 0)
            throw new SigV4StreamingException("Unexpected end of stream inside a chunk.");

        _chunkHash?.AppendData(buffer.Span[..read]);
        _remainingInChunk -= read;
        _totalDecoded += read;

        if (_remainingInChunk == 0)
        {
            CompleteChunk();
            // Consume \r\n after chunk data
            await ReadLineAsync(cancellationToken);
        }

        return read;
    }

    /// <summary>
    /// Reads the next chunk header. Returns false when the terminal zero-length
    /// chunk was consumed (and validated), true when a data chunk begins.
    /// </summary>
    private async Task<bool> BeginChunkAsync(CancellationToken ct)
    {
        var line = await ReadLineAsync(ct);
        if (string.IsNullOrEmpty(line))
            throw new SigV4StreamingException("Missing chunk header line.");

        var semiIdx = line.IndexOf(';');
        var hexSize = semiIdx != -1 ? line[..semiIdx] : line;
        if (
            !long.TryParse(
                hexSize,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var chunkSize
            )
            || chunkSize < 0
        )
        {
            throw new SigV4StreamingException($"Malformed chunk size '{hexSize}'.");
        }

        if (_signing != null)
        {
            _currentChunkSignature = ParseChunkSignature(line, semiIdx);
        }

        if (_totalDecoded + chunkSize > _expectedDecodedLength)
        {
            throw new SigV4StreamingException(
                "Chunked payload exceeds x-amz-decoded-content-length."
            );
        }

        if (chunkSize == 0)
        {
            // The terminal chunk signs the empty hash.
            if (_signing != null)
                ValidateChunkSignature(EmptySha256);
            return false;
        }

        _remainingInChunk = chunkSize;
        if (_signing != null)
            _chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        return true;
    }

    private void CompleteChunk()
    {
        if (_chunkHash == null)
            return;

        var chunkDataHash = Convert.ToHexString(_chunkHash.GetHashAndReset()).ToLowerInvariant();
        _chunkHash.Dispose();
        _chunkHash = null;
        ValidateChunkSignature(chunkDataHash);
    }

    private void ValidateChunkSignature(string chunkDataHash)
    {
        var signing = _signing!;
        var stringToSign =
            $"AWS4-HMAC-SHA256-PAYLOAD\n{signing.AmzDate}\n{signing.CredentialScope}\n{_previousSignature}\n{EmptySha256}\n{chunkDataHash}";
        var expected = ComputeHmacHex(signing.SigningKey, stringToSign);

        if (
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(_currentChunkSignature)
            )
        )
        {
            throw new SigV4StreamingException("Chunk signature does not match.");
        }

        _previousSignature = _currentChunkSignature;
    }

    private async Task FinishStreamAsync(CancellationToken ct)
    {
        _done = true;

        if (_totalDecoded != _expectedDecodedLength)
        {
            throw new SigV4StreamingException(
                $"Decoded payload length {_totalDecoded} does not match x-amz-decoded-content-length {_expectedDecodedLength}."
            );
        }

        if (_hasTrailer)
        {
            await ConsumeTrailerAsync(ct);
        }
        else
        {
            // Final \r\n after the terminal chunk (lenient: may be absent at EOF).
            await ReadLineAsync(ct, allowEof: true);
        }
    }

    private async Task ConsumeTrailerAsync(CancellationToken ct)
    {
        var trailers = new List<(string Name, string Value)>();
        string? trailerSignature = null;

        while (true)
        {
            var line = await ReadLineAsync(ct, allowEof: true);
            if (string.IsNullOrEmpty(line))
                break;

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0)
                throw new SigV4StreamingException("Malformed trailer header line.");

            var name = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            if (name == "x-amz-trailer-signature")
                trailerSignature = value;
            else
                trailers.Add((name, value));
        }

        if (_signing == null)
            return;

        if (string.IsNullOrEmpty(trailerSignature))
            throw new SigV4StreamingException("Missing x-amz-trailer-signature in signed trailer.");

        var canonicalTrailers = string.Concat(
            trailers
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .Select(t => $"{t.Name}:{t.Value}\n")
        );
        var trailerHash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTrailers)))
            .ToLowerInvariant();
        var stringToSign =
            $"AWS4-HMAC-SHA256-TRAILER\n{_signing.AmzDate}\n{_signing.CredentialScope}\n{_previousSignature}\n{trailerHash}";
        var expected = ComputeHmacHex(_signing.SigningKey, stringToSign);

        if (
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(trailerSignature)
            )
        )
        {
            throw new SigV4StreamingException("Trailer signature does not match.");
        }
    }

    private static string ParseChunkSignature(string headerLine, int semiIdx)
    {
        const string prefix = "chunk-signature=";
        if (semiIdx == -1)
            throw new SigV4StreamingException("Chunk header is missing chunk-signature.");

        foreach (var ext in headerLine[(semiIdx + 1)..].Split(';'))
        {
            if (ext.StartsWith(prefix, StringComparison.Ordinal))
            {
                var sig = ext[prefix.Length..];
                if (sig.Length != 64 || !sig.All(Uri.IsHexDigit))
                    throw new SigV4StreamingException("Malformed chunk-signature value.");
                return sig;
            }
        }

        throw new SigV4StreamingException("Chunk header is missing chunk-signature.");
    }

    private static string ComputeHmacHex(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return Convert
            .ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)))
            .ToLowerInvariant();
    }

    private async Task<string> ReadLineAsync(CancellationToken ct, bool allowEof = false)
    {
        var sb = new StringBuilder();
        while (true)
        {
            // Refill the line buffer when drained.
            if (_lineBufferOffset >= _lineBufferCount)
            {
                _lineBufferOffset = 0;
                _lineBufferCount = await _inner.ReadAsync(
                    _lineBuffer.AsMemory(0, LineBufferSize),
                    ct
                );
                if (_lineBufferCount == 0)
                {
                    if (allowEof || sb.Length == 0)
                        return sb.ToString();
                    throw new SigV4StreamingException(
                        "Unexpected end of stream inside chunk framing."
                    );
                }
            }

            // Scan the buffered bytes up to the next \n (inclusive).
            var span = _lineBuffer.AsSpan(_lineBufferOffset, _lineBufferCount - _lineBufferOffset);
            var newlineIdx = span.IndexOf((byte)'\n');
            var segmentLength = newlineIdx < 0 ? span.Length : newlineIdx + 1;
            var segment = span[..segmentLength];

            // Strip a trailing \r\n (or a bare \n) from the appended segment.
            var trim = segment.Length > 0 && segment[^1] == (byte)'\n' ? 1 : 0;
            if (trim == 1 && segment.Length > 1 && segment[^2] == (byte)'\r')
                trim = 2;

            sb.Append(Encoding.ASCII.GetString(segment[..^trim]));
            _lineBufferOffset += segmentLength;

            if (newlineIdx >= 0)
                return sb.ToString();

            if (sb.Length > MaxHeaderLineLength)
                throw new SigV4StreamingException("Chunk framing line exceeds maximum length.");
        }
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _chunkHash?.Dispose();
        base.Dispose(disposing);
    }
}
