using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using NetConduit.Interfaces;

namespace NetConduit.Transit.Message.UnitTests;

// Issue #618: peer-supplied JSON that fits the 16MB frame but is hostile in
// shape (deep nesting past 64, token count past 1M) must be rejected with
// JsonException on the MessageTransit receive path. Well-formed traffic,
// including boundary-64 payloads, must be unaffected.
public sealed class MessageJsonHardeningTests
{
    private sealed class Payload
    {
        public string? Data { get; set; }
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveAsync_DepthOver64_ThrowsJsonException()
    {
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var senderWrite = client.OpenChannel("msg-deep");
            var receiverRead = await server.AcceptChannelAsync("msg-deep", cts.Token);
            await Task.WhenAll(
                senderWrite.WaitForReadyAsync(cts.Token),
                receiverRead.WaitForReadyAsync(cts.Token));

            // Bypass the sender's serialization (which would serialize the
            // deep JSON as an escaped string): handcraft a frame whose
            // payload IS the 70-deep JSON array, the hostile-peer shape.
            // A 70-deep array exceeds the depth-64 backstop at the receiver.
            var deep = Encoding.UTF8.GetBytes(new string('[', 70) + "1" + new string(']', 70));
            await WriteRawFrameAsync(senderWrite, deep, cts.Token);

#pragma warning disable IL2026, IL3050
            var receiver = new MessageTransit<JsonElement, JsonElement>(null, receiverRead);
#pragma warning restore IL2026, IL3050
            await Assert.ThrowsAnyAsync<JsonException>(async () =>
                await receiver.ReceiveAsync(cts.Token));

            await receiver.DisposeAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact(Timeout = 30000)]
    public async Task ReceiveAsync_Boundary64_RoundTrips()
    {
        var (client, server) = await CreateReadyPairAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var senderWrite = client.OpenChannel("msg-bound");
            var receiverRead = await server.AcceptChannelAsync("msg-bound", cts.Token);
            await Task.WhenAll(
                senderWrite.WaitForReadyAsync(cts.Token),
                receiverRead.WaitForReadyAsync(cts.Token));

#pragma warning disable IL2026, IL3050
            var sender = new MessageTransit<string, string>(senderWrite, null);
            var receiver = new MessageTransit<string, string>(null, receiverRead);
#pragma warning restore IL2026, IL3050

            // 63 nested single-element arrays around "x": depth 64 exactly.
            var boundary = new string('[', 63) + "\"x\"" + new string(']', 63);
            await sender.SendAsync(boundary, cts.Token);
            var received = await receiver.ReceiveAsync(cts.Token);
            Assert.Equal(boundary, received);

            await sender.DisposeAsync();
            await receiver.DisposeAsync();
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void GateDocument_TokenBudgetExceeded_ThrowsJsonException()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 1_000_005; i++)
            sb.Append(i).Append(',');
        sb.Append("0]");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var ex = Assert.Throws<JsonException>(() =>
            JsonHardening.GateDocument(bytes.AsMemory()));
        Assert.Contains("token budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClampSerializerOptions_WideMaxDepth_ClampedDown_CallerUntouched()
    {
        var caller = new JsonSerializerOptions { MaxDepth = 128 };
        var effective = TransitJsonLimits.ClampSerializerOptions(caller);
        Assert.Equal(64, effective.MaxDepth);
        Assert.Equal(128, caller.MaxDepth);
    }

    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    private static async Task WriteRawFrameAsync(IWriteChannel ch, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        await ch.WriteAsync(frame, ct);
    }
}
