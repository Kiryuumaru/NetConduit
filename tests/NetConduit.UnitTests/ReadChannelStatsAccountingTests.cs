using NetConduit.Interfaces;

namespace NetConduit.UnitTests;

/// <summary>
/// Regression tests for issue. <c>MultiplexerStats.OpenChannels</c> must
/// decrement and <c>TotalChannelsClosed</c> must increment exactly once per
/// channel close, regardless of whether the close was driven by an inbound
/// <c>Fin</c> frame or by a local <c>DisposeAsync</c>/<c>Dispose</c>. The
/// mux-level <c>ChannelClosed</c> event is documented FIN-only and fires
/// only on the inbound-FIN path; local dispose decrements stats without
/// raising the event.
/// </summary>
public sealed class ReadChannelStatsAccountingTests
{
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
    public async Task ReadChannel_DisposedLocallyBeforeFin_DecrementsStats()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        const int n = 25;
        for (int i = 0; i < n; i++)
        {
            string id = $"ch-{i}";
            var writeTask = Task.Run(async () =>
            {
                await using var w = client.OpenChannel(id);
                await w.WaitForReadyAsync(cts.Token);
                // Hold open — do NOT close locally on the writer side, so no FIN
                // is emitted to the server before the server disposes its read channel.
                await Task.Delay(50, cts.Token);
            }, cts.Token);

            var rc = await server.AcceptChannelAsync(id, cts.Token);
            await rc.WaitForReadyAsync(cts.Token);
            await rc.DisposeAsync();

            await writeTask;
        }

        Assert.Equal(n, server.Stats.TotalChannelsOpened);
        Assert.Equal(0, server.Stats.OpenChannels);
        Assert.Equal(n, server.Stats.TotalChannelsClosed);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_DisposedLocally_DoesNotRaiseChannelClosed()
    {
        // The mux-level ChannelClosed event is documented FIN-only
        // (see IStreamMultiplexer.ChannelClosed). Local DisposeAsync must
        // NOT fire it; the stats decrement (covered by
        // ReadChannel_DisposedLocallyBeforeFin_DecrementsStats) still runs.
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var closedIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        server.ChannelClosed += (_, e) => closedIds.Add(e.ChannelId);

        var writeTask = Task.Run(async () =>
        {
            await using var w = client.OpenChannel("ephemeral");
            await w.WaitForReadyAsync(cts.Token);
            await Task.Delay(50, cts.Token);
        }, cts.Token);

        var rc = await server.AcceptChannelAsync("ephemeral", cts.Token);
        await rc.WaitForReadyAsync(cts.Token);
        await rc.DisposeAsync();
        await writeTask;

        // Allow any straggling FIN handlers a moment, then assert no event.
        await Task.Delay(100, cts.Token);

        Assert.DoesNotContain("ephemeral", closedIds);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_RemoteFin_RaisesChannelClosedExactlyOnce()
    {
        var (client, server) = CreatePair();
        client.Start(); server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var closedIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        server.ChannelClosed += (_, e) => closedIds.Add(e.ChannelId);

        var w = client.OpenChannel("fin-driven");
        var rc = await server.AcceptChannelAsync("fin-driven", cts.Token);
        await w.WaitForReadyAsync(cts.Token);
        await rc.WaitForReadyAsync(cts.Token);

        // Writer closes — peer observes FIN.
        await w.CloseAsync(cts.Token);

        // Drain so FIN frame has been processed and ReadAsync sees EOF.
        var buf = new byte[64];
        while (await rc.ReadAsync(buf, cts.Token) > 0) { }

        // Now the consumer disposes the read channel — second potential
        // accounting site. CAS guarantees only one decrement / one event.
        await rc.DisposeAsync();

        await Task.Delay(100, cts.Token);

        Assert.Single(closedIds, "fin-driven");
        Assert.Equal(0, server.Stats.OpenChannels);
        Assert.Equal(1, server.Stats.TotalChannelsClosed);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
