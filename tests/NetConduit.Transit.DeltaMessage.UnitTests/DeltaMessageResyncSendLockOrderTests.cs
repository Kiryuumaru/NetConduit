using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace NetConduit.Transit.DeltaMessage.UnitTests;

/// <summary>
/// <see cref="DeltaMessageTransit{T}"/>'s receive path triggers a resync
/// request to the peer when a 0x01 delta arrives before any 0x00 full state.
/// Before the fix the resync write was issued from inside the receive lock
/// and bypassed <c>_sendLock</c>, so its 5-byte frame could interleave bytes
/// with a concurrent <c>SendAsync</c> on the same write channel, corrupting
/// wire framing.
///
/// The fix flags the resync request from inside <c>_receiveLock</c> and drains
/// it from the <see cref="DeltaMessageTransit{T}.ReceiveAsync"/> wrapper after
/// <c>_receiveLock</c> is released, acquiring <c>_sendLock</c> first. This test
/// asserts the lock invariant deterministically: holding <c>_sendLock</c>
/// externally must block the resync write until released.
/// </summary>
public sealed class DeltaMessageResyncSendLockOrderTests
{
    [Fact]
    public async Task ReceiveAsync_DeltaBeforeFullState_ResyncWriteWaitsForSendLock()
    {
        var (client, server) = await CreateReadyPairAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // Channel client -> server carries the synthetic 0x01 delta frame that
        // triggers the receiver-side resync request.
        var clientWrite = client.OpenChannel("dt-resync-in");
        var serverRead = await server.AcceptChannelAsync("dt-resync-in", ct);

        // Channel server -> client carries the 0x02 resync frame the receiver
        // emits in response.
        var serverWrite = server.OpenChannel("dt-resync-out");
        var clientRead = await client.AcceptChannelAsync("dt-resync-out", ct);

        await serverRead.WaitForReadyAsync(ct);
        await clientRead.WaitForReadyAsync(ct);

        var serverTransit = new DeltaMessageTransit<JsonObject>(serverWrite, serverRead);

        // Pre-acquire serverTransit._sendLock from outside, simulating an
        // in-flight SendAsync that has not yet released the lock. Under the
        // pre-fix code the resync write happened inside _receiveLock and never
        // touched _sendLock, so it would complete immediately even though
        // _sendLock is held. Under the fix the drain waits for _sendLock.
        var sendLockField = typeof(DeltaMessageTransit<JsonObject>)
            .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sendLockField);
        var sendLock = (SemaphoreSlim)sendLockField!.GetValue(serverTransit)!;
        await sendLock.WaitAsync(ct);

        // Write a single 0x01 delta frame via the raw write channel:
        // [4-byte big-endian length=1, 0x01 type].
        var deltaFrame = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x01 };
        await clientWrite.WriteAsync(deltaFrame, ct);

        // ReceiveAsync reads the delta, sees _lastReceivedState is null,
        // flags _outgoingResyncPending, releases _receiveLock, then in the
        // outer finally tries to acquire _sendLock for the resync write.
        var receiveTask = serverTransit.ReceiveAsync(ct).AsTask();

        // Give the receive path ample time to read the frame and reach the
        // _sendLock acquisition.
        for (int i = 0; i < 50 && !receiveTask.IsCompleted; i++)
            await Task.Delay(20, ct);

        Assert.False(receiveTask.IsCompleted,
            "ReceiveAsync must block on _sendLock before emitting the resync frame so the 5-byte resync cannot interleave with a concurrent SendAsync.");

        // Release _sendLock. The drain proceeds and writes the resync frame.
        sendLock.Release();

        var received = await receiveTask;
        Assert.Null(received);

        // Verify the peer observed the resync frame [length=1, 0x02].
        var resyncBuf = new byte[5];
        int total = 0;
        while (total < 5)
        {
            var n = await clientRead.ReadAsync(resyncBuf.AsMemory(total, 5 - total), ct);
            Assert.True(n > 0, "client must receive the resync frame after _sendLock is released");
            total += n;
        }
        Assert.Equal(0x00, resyncBuf[0]);
        Assert.Equal(0x00, resyncBuf[1]);
        Assert.Equal(0x00, resyncBuf[2]);
        Assert.Equal(0x01, resyncBuf[3]);
        Assert.Equal(0x02, resyncBuf[4]);

        await serverTransit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
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
}
