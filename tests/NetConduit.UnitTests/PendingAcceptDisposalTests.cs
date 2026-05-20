using NetConduit.Enums;
using NetConduit.Exceptions;

namespace NetConduit.UnitTests;

public sealed class PendingAcceptDisposalTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static (StreamMultiplexer Client, StreamMultiplexer Server) CreatePair()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
            PingInterval = TimeSpan.Zero,
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
            PingInterval = TimeSpan.Zero,
        });
        return (client, server);
    }

    [Fact]
    public async Task DisposedPendingAccept_IsNotResurrected_WhenPeerInitArrives()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        // Pre-register accept for "ghost-channel" then dispose before peer INIT.
        var pending = server.AcceptChannel("ghost-channel");
        var pendingCh = (NetConduit.Internal.ReadChannel)pending;

        bool closedFired = false;
        pendingCh.Closed += (_, _) => closedFired = true;

        await pendingCh.DisposeAsync();

        Assert.True(closedFired, "Closed event must fire on dispose");
        Assert.Equal(ChannelState.Closed, pendingCh.State);

        // Peer opens the channel — INIT will arrive at the server.
        client.OpenChannel("ghost-channel");

        // Wait long enough for the INIT frame to be dispatched.
        await Task.Delay(200);

        // The disposed instance must NOT be resurrected.
        Assert.Equal(ChannelState.Closed, pendingCh.State);

        // The server side should observe a brand-new ReadChannel via the
        // default accept stream — same id, different instance.
        using var acceptCts = new CancellationTokenSource(TestTimeout);
        var freshEnumerator = server.AcceptChannelsAsync(ct: acceptCts.Token).GetAsyncEnumerator();
        try
        {
            var moved = await freshEnumerator.MoveNextAsync().AsTask().WaitAsync(TestTimeout);
            Assert.True(moved);
            var fresh = freshEnumerator.Current;
            Assert.Equal("ghost-channel", fresh.ChannelId);
            Assert.NotSame(pendingCh, fresh);
            Assert.Equal(ChannelState.Open, fresh.State);
        }
        finally
        {
            acceptCts.Cancel();
            try { await freshEnumerator.DisposeAsync(); } catch (OperationCanceledException) { }
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task DisposedPendingAccept_AllowsReAcceptOfSameChannelId()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(
            client.WaitForReadyAsync().WaitAsync(TestTimeout),
            server.WaitForReadyAsync().WaitAsync(TestTimeout));

        // First pending accept, then dispose.
        var first = server.AcceptChannel("retry-channel");
        await ((IAsyncDisposable)first).DisposeAsync();

        // A fresh AcceptChannel for the same id should return a NEW pending
        // instance, not the disposed corpse.
        var second = server.AcceptChannel("retry-channel");
        Assert.NotSame(first, second);
        Assert.Equal(ChannelState.Opening, second.State);

        await ((IAsyncDisposable)second).DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
