using System.Net.WebSockets;
using NetConduit.Transport.WebSocket;

namespace NetConduit.Transport.WebSocket.IntegrationTests;

public class WebSocketStreamTests
{
    /// <summary>
    /// Minimal fake <see cref="System.Net.WebSockets.WebSocket"/> that returns a scripted
    /// sequence of <see cref="WebSocketReceiveResult"/>s. Only the members touched by
    /// <see cref="WebSocketStream"/> are implemented; the rest throw to surface accidental
    /// dependence in tests.
    /// </summary>
    private sealed class ScriptedWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Queue<(byte[] Payload, WebSocketMessageType Type)> _frames;

        public ScriptedWebSocket(params (byte[] Payload, WebSocketMessageType Type)[] frames)
        {
            _frames = new Queue<(byte[], WebSocketMessageType)>(frames);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_frames.Count == 0)
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

            var (payload, type) = _frames.Dequeue();
            payload.AsSpan().CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(payload.Length, type, endOfMessage: true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task ReadAsync_PeerSendsTextFrame_ThrowsIOException()
    {
        // Regression for #217: a Text-mode message must be rejected at the stream
        // adapter — otherwise its UTF-8 bytes get fed to FrameHeader.Parse and
        // either tear down the mux with a misattributed ProtocolError or inject
        // bytes into the wrong channel's read stream.
        var textPayload = "HELLO WORLD!!!"u8.ToArray();
        var ws = new ScriptedWebSocket((textPayload, WebSocketMessageType.Text));
        var stream = new WebSocketStream(ws);

        var buffer = new byte[64];
        var ex = await Assert.ThrowsAsync<IOException>(async () =>
        {
            _ = await stream.ReadAsync(buffer.AsMemory(), default);
        });

        Assert.Contains("Text", ex.Message);
    }

    [Fact]
    public async Task ReadAsync_PeerSendsBinaryFrame_ReturnsPayload()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var ws = new ScriptedWebSocket((payload, WebSocketMessageType.Binary));
        var stream = new WebSocketStream(ws);

        var buffer = new byte[payload.Length];
        await stream.ReadExactlyAsync(buffer.AsMemory(), default);

        Assert.Equal(payload, buffer);
    }

    [Fact]
    public async Task ReadAsync_PeerSendsCloseFrame_ReturnsZero()
    {
        var ws = new ScriptedWebSocket(([], WebSocketMessageType.Close));
        var stream = new WebSocketStream(ws);

        var buffer = new byte[64];
        int read = await stream.ReadAsync(buffer.AsMemory(), default);

        Assert.Equal(0, read);
    }
}
