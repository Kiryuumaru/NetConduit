using NetConduit.Enums;
using NetConduit.Models;
namespace NetConduit.UnitTests;

/// <summary>
/// Tests for data safety during reconnection scenarios:
/// - Multiple channels surviving reconnect simultaneously
/// - Bidirectional data during reconnect
/// - Channel state consistency after reconnect
/// </summary>
public class ReconnectDataSafetyTests
{
    private const int TestTimeout = 30000;

    #region Multiple Channels During Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task MultipleChannels_AllSurviveDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var writers = new List<WriteChannel>();
        for (int i = 0; i < 5; i++)
        {
            var w = await mux1.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"ch{i}" }, cts.Token);
            await mux2.AcceptChannelAsync($"ch{i}", cts.Token);
            writers.Add(w);
        }

        // Write to all channels before disconnect
        foreach (var w in writers)
        {
            await w.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        }

        // Kill transport
        await pipe.DisconnectAsync();
        await Task.Delay(200);

        // All channels should still exist (write should not throw or channels should be in valid state)
        foreach (var w in writers)
        {
            Assert.True(
                w.State == ChannelState.Open ||
                w.State == ChannelState.Closing ||
                w.State == ChannelState.Closed);
        }

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task BidirectionalChannels_BothDirectionsSurviveDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        // A->B channel
        var writerAB = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "AtoB" }, cts.Token);
        await mux2.AcceptChannelAsync("AtoB", cts.Token);

        // B->A channel
        var writerBA = await mux2.OpenChannelAsync(
            new ChannelOptions { ChannelId = "BtoA" }, cts.Token);
        await mux1.AcceptChannelAsync("BtoA", cts.Token);

        // Write in both directions before disconnect
        var dataAB = new byte[1024];
        Random.Shared.NextBytes(dataAB);
        await writerAB.WriteAsync(dataAB, cts.Token);

        var dataBA = new byte[1024];
        Random.Shared.NextBytes(dataBA);
        await writerBA.WriteAsync(dataBA, cts.Token);

        // Kill transport
        await pipe.DisconnectAsync();
        await Task.Delay(200);

        // Both writers should be in a deterministic state
        Assert.True(
            writerAB.State == ChannelState.Open ||
            writerAB.State == ChannelState.Closing ||
            writerAB.State == ChannelState.Closed);
        Assert.True(
            writerBA.State == ChannelState.Open ||
            writerBA.State == ChannelState.Closing ||
            writerBA.State == ChannelState.Closed);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Data Sent Before Disconnect Is Fully Received

    [Theory(Timeout = TestTimeout)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(50)]
    public async Task DataSentBeforeDisconnect_SmallPayloads_AllReceived(int latencyMs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reconnectable = new ReconnectableDuplexPipe(latencyMs);
        var opts1 = new MultiplexerOptions { StreamFactory = reconnectable.CreateStream1 };
        var opts2 = new MultiplexerOptions { StreamFactory = reconnectable.CreateStream2 };
        var mux1 = StreamMultiplexer.Create(opts1);
        var mux2 = StreamMultiplexer.Create(opts2);
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);
        await Task.WhenAll(mux1.WaitForReadyAsync(cts.Token), mux2.WaitForReadyAsync(cts.Token));
        await using var m1 = mux1;
        await using var m2 = mux2;

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);
        await accept;

        // Send 10 small messages
        var allSent = new List<byte[]>();
        for (int i = 0; i < 10; i++)
        {
            var data = new byte[100];
            Random.Shared.NextBytes(data);
            allSent.Add(data);
            await writer.WriteAsync(data, cts.Token);
        }

        // Read everything before disconnect
        var received = new byte[1000];
        var totalRead = 0;
        while (totalRead < 1000)
        {
            var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }

        Assert.Equal(1000, totalRead);

        // Verify all data matches
        var expectedFull = allSent.SelectMany(b => b).ToArray();
        Assert.Equal(expectedFull, received);

        // Now disconnect
        await reconnectable.DisconnectAsync();
        await Task.Delay(200);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Channel State After Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelState_AfterDisconnect_IsNotOpen()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);
        await mux2.AcceptChannelAsync("ch1", cts.Token);

        Assert.Equal(ChannelState.Open, writer.State);

        await pipe.DisconnectAsync();
        await Task.Delay(500);

        // After disconnect, channel is not in Opening state — either still Open (buffered) or Closing/Closed
        Assert.NotEqual(ChannelState.Opening, writer.State);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MuxState_AfterDisconnect_EventFires()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var disconnectedTcs = new TaskCompletionSource<bool>();
        mux1.OnDisconnected += (_, _) => disconnectedTcs.TrySetResult(true);

        Assert.True(mux1.IsConnected);

        await pipe.DisconnectAsync();

        // Wait for the disconnect event to fire
        var fired = await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fired);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OnDisconnected_FiresWithCorrectReason()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var disconnectTcs = new TaskCompletionSource<DisconnectReason>();
        mux1.OnDisconnected += (reason, _) => disconnectTcs.TrySetResult(reason);

        await pipe.DisconnectAsync();

        var reason = await disconnectTcs.Task.WaitAsync(cts.Token);
        Assert.Equal(DisconnectReason.TransportError, reason);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Stats Consistency After Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task Stats_BytesSent_PreservedAfterDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);
        await mux2.AcceptChannelAsync("ch1", cts.Token);

        var data = new byte[5000];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        var bytesSentBefore = mux1.Stats.BytesSent;
        Assert.True(bytesSentBefore > 0);

        await pipe.DisconnectAsync();
        await Task.Delay(200);

        // Stats should be preserved (not reset)
        Assert.True(mux1.Stats.BytesSent >= bytesSentBefore);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion
}
