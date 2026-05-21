using System.Net.WebSockets;

namespace NetConduit.Transport.WebSocket;

/// <summary>
/// Adapts a WebSocket to a Stream interface for use with StreamMultiplexer.
/// WebSockets are message-based; this adapter converts to a byte stream.
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

        if (_receiveBufferCount > 0)
        {
            int bytesToCopy = Math.Min(buffer.Length, _receiveBufferCount);
            _receiveBuffer.AsSpan(_receiveBufferOffset, bytesToCopy).CopyTo(buffer.Span);
            _receiveBufferOffset += bytesToCopy;
            _receiveBufferCount -= bytesToCopy;
            return bytesToCopy;
        }

        while (true)
        {
            var result = await _webSocket.ReceiveAsync(_receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (_webSocket.State == WebSocketState.CloseReceived)
                {
                    try
                    {
                        await _webSocket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (WebSocketException) { }
                    catch (ObjectDisposedException) { }
                }
                return 0;
            }

            // NetConduit's framing layer is binary. Any non-Binary data frame (Text,
            // or future WebSocket message types) must be rejected here — otherwise its
            // payload would be fed to FrameHeader.Parse and either tear down the mux
            // with a misattributed ProtocolError or, worse, inject bytes into the
            // wrong channel's read stream (issue #217).
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new IOException(
                    $"WebSocket peer sent unsupported message type {result.MessageType}; " +
                    "NetConduit requires Binary frames only.");
            }

            if (result.Count > 0)
            {
                int bytesToCopy = Math.Min(buffer.Length, result.Count);
                _receiveBuffer.AsSpan(0, bytesToCopy).CopyTo(buffer.Span);

                if (result.Count > bytesToCopy)
                {
                    _receiveBufferOffset = bytesToCopy;
                    _receiveBufferCount = result.Count - bytesToCopy;
                }

                return bytesToCopy;
            }
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await _webSocket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException) when (_webSocket.State != WebSocketState.Open)
        {
            throw new IOException("WebSocket connection was closed.");
        }
        catch (InvalidOperationException) when (_webSocket.State != WebSocketState.Open)
        {
            throw new IOException("WebSocket connection was closed.");
        }
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
        }
        base.Dispose(disposing);
    }
}
