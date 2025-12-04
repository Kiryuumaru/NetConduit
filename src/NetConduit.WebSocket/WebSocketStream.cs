using System.Net.WebSockets;

namespace NetConduit.WebSocket;

/// <summary>
/// Adapts a WebSocket to a Stream interface for use with StreamMultiplexer.
/// WebSockets are message-based, so this adapter handles the conversion to a byte stream.
/// </summary>
internal sealed class WebSocketStream : Stream
{
    private readonly System.Net.WebSockets.WebSocket _webSocket;
    private readonly byte[] _receiveBuffer;
    private int _receiveBufferOffset;
    private int _receiveBufferCount;
    private bool _disposed;

    public WebSocketStream(System.Net.WebSockets.WebSocket webSocket, int bufferSize = 65536)
    {
        _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _receiveBuffer = new byte[bufferSize];
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // If we have buffered data, return from buffer
        if (_receiveBufferCount > 0)
        {
            int bytesToCopy = Math.Min(buffer.Length, _receiveBufferCount);
            _receiveBuffer.AsSpan(_receiveBufferOffset, bytesToCopy).CopyTo(buffer.Span);
            _receiveBufferOffset += bytesToCopy;
            _receiveBufferCount -= bytesToCopy;
            return bytesToCopy;
        }

        // Read from WebSocket
        while (true)
        {
            var result = await _webSocket.ReceiveAsync(_receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return 0; // End of stream
            }

            if (result.Count > 0)
            {
                int bytesToCopy = Math.Min(buffer.Length, result.Count);
                _receiveBuffer.AsSpan(0, bytesToCopy).CopyTo(buffer.Span);

                // Buffer any remaining data
                if (result.Count > bytesToCopy)
                {
                    _receiveBufferOffset = bytesToCopy;
                    _receiveBufferCount = result.Count - bytesToCopy;
                }

                return bytesToCopy;
            }

            // Continue reading if we got an empty frame
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not open.");
        }

        await _webSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            // Don't dispose the WebSocket here - it's owned by WebSocketMultiplexerConnection
        }
        base.Dispose(disposing);
    }
}
