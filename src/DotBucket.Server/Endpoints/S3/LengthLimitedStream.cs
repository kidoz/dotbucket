// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

namespace DotBucket.Server.Endpoints.S3;

/// <summary>
/// A wrapper stream that limits reading to a specific length.
/// </summary>
public class LengthLimitedStream : Stream
{
    private readonly Stream _innerStream;
    private long _remaining;

    public LengthLimitedStream(Stream innerStream, long length)
    {
        _innerStream = innerStream;
        _remaining = length;
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _remaining;
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
            return 0;
        int toRead = (int)Math.Min(count, _remaining);
        int read = _innerStream.Read(buffer, offset, toRead);
        _remaining -= read;
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        if (_remaining <= 0)
            return 0;
        int toRead = (int)Math.Min(count, _remaining);
        int read = await _innerStream.ReadAsync(buffer, offset, toRead, cancellationToken);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (_remaining <= 0)
            return 0;
        int toRead = (int)Math.Min(buffer.Length, _remaining);
        int read = await _innerStream.ReadAsync(buffer[..toRead], cancellationToken);
        _remaining -= read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
