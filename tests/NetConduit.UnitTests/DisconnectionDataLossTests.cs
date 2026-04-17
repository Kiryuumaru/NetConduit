namespace NetConduit.UnitTests;

/// <summary>
/// Tests that verify the mux recovers from transport disconnection and
/// delivers all data without loss. Each test kills the transport, then
/// performs operations that REQUIRE reconnection to succeed.
///
/// These tests are expected to FAIL with the current implementation.
/// Fixing the src/ to support always-on reconnection should make them pass.
/// DO NOT MODIFY ASSERTIONS — fix the source code to pass these tests.
/// </summary>
public class DisconnectionDataLossTests
{
    private const int TestTimeout = 30000;

    private static MultiplexerOptions NoReconnOptions() => new()
    {
        StreamFactory = _ => null!
    };

    #region 1. Write After Disconnect — Channel Must Survive

    [Fact(Timeout = TestTimeout)]
    public async Task WriteAfterDisconnect_SmallData_Succeeds()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write AFTER disconnect — must not throw
        var data = new byte[] { 1, 2, 3, 4, 5 };
        await writer.WriteAsync(data, cts.Token);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WriteAfterDisconnect_LargeData_Succeeds()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write 64KB after disconnect — must not throw
        var data = new byte[64 * 1024];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task WriteAfterDisconnect_MultipleWrites_AllSucceed()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Multiple writes after disconnect — all must succeed
        for (int i = 0; i < 10; i++)
        {
            var data = new byte[256];
            Random.Shared.NextBytes(data);
            await writer.WriteAsync(data, cts.Token);
        }

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 2. Read After Disconnect — Data Must Eventually Arrive

    [Fact(Timeout = TestTimeout)]
    public async Task ReadAfterDisconnect_DataSentBeforeDisconnect_FullyReceived()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);
        await accept;

        // Send data then immediately kill transport
        var sent = new byte[4096];
        Random.Shared.NextBytes(sent);
        await writer.WriteAsync(sent, cts.Token);
        await pipe.DisposeAsync();

        // Wait for disconnect to propagate
        await Task.Delay(200);

        // Read AFTER disconnect propagated — must still get ALL data
        var received = new byte[sent.Length];
        var totalRead = 0;
        while (totalRead < sent.Length)
        {
            var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(sent.Length, totalRead);
        Assert.Equal(sent, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadAfterDisconnect_DataSentAfterDisconnect_Received()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);
        await accept;

        // Kill transport first
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Send data AFTER disconnect
        var sent = new byte[] { 10, 20, 30, 40, 50 };
        await writer.WriteAsync(sent, cts.Token);

        // Read — must receive the post-disconnect data
        var received = new byte[sent.Length];
        var totalRead = 0;
        while (totalRead < sent.Length)
        {
            var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(sent.Length, totalRead);
        Assert.Equal(sent, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 3. Open New Channel After Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task OpenChannelAfterDisconnect_Succeeds()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Open new channel after disconnect — must not throw
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "newch" }, cts.Token);
        Assert.NotNull(writer);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task OpenChannelAfterDisconnect_CanSendAndReceive()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Open channel, accept on other side, send data
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
                return ch;
            return null;
        });

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "postdisconnect" }, cts.Token);
        var reader = await acceptTask.WaitAsync(cts.Token);
        Assert.NotNull(reader);

        var data = new byte[] { 42, 43, 44 };
        await writer.WriteAsync(data, cts.Token);

        var received = new byte[data.Length];
        var totalRead = 0;
        while (totalRead < data.Length)
        {
            var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(data, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 4. Mux State After Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task MuxIsConnected_TrueAfterReconnect()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(mux1.IsConnected);
        Assert.True(mux2.IsConnected);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(500);

        // Mux must recover to connected state
        Assert.True(mux1.IsConnected);
        Assert.True(mux2.IsConnected);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MuxIsRunning_TrueAfterDisconnect()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.True(mux1.IsRunning);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(500);

        // Mux must still be running (waiting for reconnect, not dead)
        Assert.True(mux1.IsRunning);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 5. Multiple Channels After Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task MultipleChannels_WriteAfterDisconnect_AllSucceed()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writers = new WriteChannel[3];
        for (int i = 0; i < 3; i++)
        {
            writers[i] = await mux1.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"ch{i}" }, cts.Token);
        }

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write on all 3 channels after disconnect — all must succeed
        for (int i = 0; i < 3; i++)
        {
            var data = new byte[512];
            Random.Shared.NextBytes(data);
            await writers[i].WriteAsync(data, cts.Token);
        }

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MultipleChannels_MixedOldAndNew_AfterDisconnect()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Open channel before disconnect
        await using var existingCh = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "existing" }, cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write on existing channel after disconnect
        await existingCh.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);

        // Open NEW channel after disconnect and write
        await using var newCh = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "new" }, cts.Token);
        await newCh.WriteAsync(new byte[] { 4, 5, 6 }, cts.Token);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 6. Bidirectional After Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task Bidirectional_WriteAfterDisconnect_BothDirectionsSucceed()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Forward: mux1 → mux2
        await using var fwdWriter = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "fwd" }, cts.Token);

        // Reverse: mux2 → mux1
        await using var revWriter = await mux2.OpenChannelAsync(
            new ChannelOptions { ChannelId = "rev" }, cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write in both directions after disconnect — both must succeed
        await fwdWriter.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await revWriter.WriteAsync(new byte[] { 4, 5, 6 }, cts.Token);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 7. Data Integrity Across Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task DataIntegrity_BeforeAndAfterDisconnect_AllBytesReceived()
    {
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "integrity" }, cts.Token);
        await accept;

        // Send data BEFORE disconnect
        var before = new byte[] { 1, 2, 3, 4, 5 };
        await writer.WriteAsync(before, cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Send data AFTER disconnect
        var after = new byte[] { 6, 7, 8, 9, 10 };
        await writer.WriteAsync(after, cts.Token);

        // Must receive ALL 10 bytes in order
        var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var received = new byte[expected.Length];
        var totalRead = 0;
        while (totalRead < expected.Length)
        {
            var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(expected.Length, totalRead);
        Assert.Equal(expected, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion
}
