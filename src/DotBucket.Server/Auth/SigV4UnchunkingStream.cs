// Licensed under the MIT License.
// See LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text;

namespace DotBucket.Server.Auth;

public class SigV4UnchunkingStream : Stream
{
    private readonly Stream _inner;
    private long _remainingInChunk;
    private bool _done;

    public SigV4UnchunkingStream(Stream inner)
    {
        _inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_done) return 0;

        if (_remainingInChunk == 0)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line))
            {
                _done = true;
                return 0;
            }

            var semiIdx = line.IndexOf(';');
            var hexSize = semiIdx != -1 ? line[..semiIdx] : line;
            if (!long.TryParse(hexSize, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _remainingInChunk))
            {
                _done = true;
                return 0;
            }

            if (_remainingInChunk == 0)
            {
                _done = true;
                // Final \r\n after the 0 chunk
                await ReadLineAsync(cancellationToken); 
                return 0;
            }
        }

        int toRead = (int)Math.Min((long)buffer.Length, _remainingInChunk);
        int read = await _inner.ReadAsync(buffer[..toRead], cancellationToken);
        if (read == 0 && toRead > 0) throw new EndOfStreamException();
        
        _remainingInChunk -= read;

        if (_remainingInChunk == 0)
        {
            // Consume \r\n after chunk data
            await ReadLineAsync(cancellationToken);
        }

        return read;
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var buffer = new byte[1];
            int read = await _inner.ReadAsync(buffer, ct);
            if (read == 0) break;
            
            char c = (char)buffer[0];
            sb.Append(c);
            if (sb.Length >= 2 && sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
            {
                sb.Length -= 2;
                return sb.ToString();
            }
            if (sb.Length > 2048) break; 
        }
        return sb.ToString();
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
