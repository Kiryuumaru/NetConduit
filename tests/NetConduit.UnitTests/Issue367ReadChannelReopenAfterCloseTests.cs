using NetConduit.Enums;

namespace NetConduit.UnitTests;

/// <summary>
/// #367 regression: a peer that closes a channel and reopens the same channel
/// ID before the local user disposes the closed ReadChannel must not fault the
/// reader. ReadChannel must mirror WriteChannel and release its registry slot
/// on close (any reason — RemoteFin, RemoteError, LocalClose), so the
/// subsequent INIT for the same channel ID can register successfully.
/// </summary>
public sealed class Issue367ReadChannelReopenAfterCloseTests
{
    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
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
        return (client, server);
    }

    [Fact]
    public async Task PeerReopensChannelId_WhileLocalReadChannelStillRegistered_DoesNotFaultMux()
    {
        var (client, server) = CreatePair();
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // 1) Open / accept channel "X", exchange data, close from the writer side.
        var wc1 = client.OpenChannel("X");
        var rc1 = await server.AcceptChannelAsync("X", cts.Token);
        await wc1.WriteAsync(new byte[] { 1, 2, 3, 4 }, cts.Token);

        var closedTcs = new TaskCompletionSource<ChannelCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        rc1.Closed += (_, e) => closedTcs.TrySetResult(e.Reason);

        // Drain the payload + observe EOF so the FIN propagates into SetClosed.
        var buf = new byte[16];
        int read = await rc1.ReadAsync(buf.AsMemory(0, 4), cts.Token);
        Assert.Equal(4, read);

        await wc1.DisposeAsync();

        int eof = await rc1.ReadAsync(buf, cts.Token);
        Assert.Equal(0, eof);

        var closeReason = await closedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
        Assert.Equal(ChannelCloseReason.RemoteFin, closeReason);

        // 2) DO NOT dispose rc1 — this is the bug scenario. The local user is
        //    still holding the closed read channel (e.g. to inspect CloseReason)
        //    when the peer reopens the same channel ID. The reopened channel's
        //    INIT must arrive at the server BEFORE the second AcceptChannelAsync
        //    looks up "X" — otherwise the lookup would (legitimately) return
        //    the still-registered closed rc1 instance. Use a Ready watcher on
        //    the writer side to confirm the round-trip INIT/INIT-ACK completed.
        var wc2 = client.OpenChannel("X");
        await wc2.WaitForReadyAsync(cts.Token);

        var rc2 = await server.AcceptChannelAsync("X", cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.NotSame(rc1, rc2);
        Assert.Equal(ChannelState.Closed, rc1.State);

        // 3) The mux is still healthy — round-trip data on the reopened channel.
        await wc2.WriteAsync(new byte[] { 9, 9, 9, 9 }, cts.Token);
        int read2 = await rc2.ReadAsync(buf.AsMemory(0, 4), cts.Token);
        Assert.Equal(4, read2);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, buf.AsMemory(0, 4).ToArray());

        await wc2.DisposeAsync();
        await rc2.DisposeAsync();
        await rc1.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
