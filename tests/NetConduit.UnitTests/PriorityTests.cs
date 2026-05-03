namespace NetConduit.UnitTests;

public sealed class PriorityTests
{
    [Fact]
    public async Task HighPriorityChannel_SentBeforeLowPriority()
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

        await Task.WhenAll(client.Start(), server.Start());

        // Open channels with different priorities
        var lowCh = await client.OpenChannelAsync(new ChannelOptions
        {
            ChannelId = "low",
            Priority = ChannelPriority.Low,
        });

        var highCh = await client.OpenChannelAsync(new ChannelOptions
        {
            ChannelId = "high",
            Priority = ChannelPriority.High,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lowRead = await server.AcceptChannelAsync("low", cts.Token);
        var highRead = await server.AcceptChannelAsync("high", cts.Token);

        // Write to both channels — high priority should arrive
        await highCh.WriteAsync(new byte[] { 0xAA });
        await lowCh.WriteAsync(new byte[] { 0xBB });

        // Both should eventually be received
        byte[] buf = new byte[1];
        int read = await highRead.ReadAsync(buf, cts.Token);
        Assert.Equal(1, read);
        Assert.Equal(0xAA, buf[0]);

        read = await lowRead.ReadAsync(buf, cts.Token);
        Assert.Equal(1, read);
        Assert.Equal(0xBB, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }
}
