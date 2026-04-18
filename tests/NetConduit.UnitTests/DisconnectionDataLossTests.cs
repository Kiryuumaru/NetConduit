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

    #region 8. In-Flight Data at Disconnect — No Silent Loss

    [Fact(Timeout = TestTimeout)]
    public async Task InFlightData_SmallChunk_NotSilentlyDropped()
    {
        // Data written moments before disconnect may still be in the Pipe or OS buffer.
        // The receiver must eventually get every byte, even if transport dies mid-flush.
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
            new ChannelOptions { ChannelId = "inflight" }, cts.Token);
        await accept;

        // Write 10 small chunks back-to-back then immediately kill transport
        var allData = new byte[1000];
        for (int i = 0; i < 1000; i++) allData[i] = (byte)(i % 256);

        for (int i = 0; i < 10; i++)
            await writer.WriteAsync(allData.AsMemory(i * 100, 100), cts.Token);

        // Kill immediately — some data may still be in the pipe
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Must get all 1000 bytes
        var received = new byte[1000];
        var totalRead = 0;
        while (totalRead < 1000)
        {
            var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(1000, totalRead);
        Assert.Equal(allData, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task InFlightData_LargeTransfer_MidStreamDisconnect_AllBytesArrived()
    {
        // 1MB transfer. Kill transport at ~50% mark. Must receive ALL bytes eventually.
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "bigstream" }, cts.Token);
        await accept;

        var totalSize = 1024 * 1024;
        var allData = new byte[totalSize];
        for (int i = 0; i < totalSize; i++) allData[i] = (byte)((i * 7 + 13) % 256);

        // Start reading in background
        var received = new byte[totalSize];
        var readTask = Task.Run(async () =>
        {
            var totalRead = 0;
            while (totalRead < totalSize)
            {
                var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
            }
            return totalRead;
        });

        // Write first half
        var half = totalSize / 2;
        for (int offset = 0; offset < half; offset += 8192)
        {
            var chunk = Math.Min(8192, half - offset);
            await writer.WriteAsync(allData.AsMemory(offset, chunk), cts.Token);
        }

        // Kill transport mid-stream
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write second half — must not throw, must eventually deliver
        for (int offset = half; offset < totalSize; offset += 8192)
        {
            var chunk = Math.Min(8192, totalSize - offset);
            await writer.WriteAsync(allData.AsMemory(offset, chunk), cts.Token);
        }

        var totalRead = await readTask;
        Assert.Equal(totalSize, totalRead);
        Assert.Equal(allData, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 9. Channel State Preservation Across Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelState_WriteChannel_StaysOpen_AfterDisconnect()
    {
        // After disconnect, the channel must remain in Open state
        // and be able to write data that eventually reaches the reader.
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
            new ChannelOptions { ChannelId = "stateful" }, cts.Token);
        await accept;

        Assert.Equal(ChannelState.Open, writer.State);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Channel must still be Open
        Assert.Equal(ChannelState.Open, writer.State);
        Assert.True(writer.CanWrite);

        // Prove it by writing data that reaches the reader
        await writer.WriteAsync(new byte[] { 99 }, cts.Token);
        var buf = new byte[1];
        var n = await reader!.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(99, buf[0]);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelState_ReadChannel_StaysOpen_AfterDisconnect()
    {
        // After disconnect, the read channel must still receive data
        // written by the writer. State must be Open.
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
            new ChannelOptions { ChannelId = "readstate" }, cts.Token);
        await accept;

        Assert.Equal(ChannelState.Open, reader!.State);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Read channel must still be Open and able to receive
        Assert.Equal(ChannelState.Open, reader.State);
        Assert.True(reader.CanRead);

        // Prove it by sending data after disconnect and reading it
        await writer.WriteAsync(new byte[] { 77 }, cts.Token);
        var buf = new byte[1];
        var n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(1, n);
        Assert.Equal(77, buf[0]);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ChannelStillUsable_WriteSucceeds_AfterDisconnect()
    {
        // After disconnect, the channel must still allow writes that eventually
        // reach the other side. This verifies the channel stays functional.
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
            new ChannelOptions { ChannelId = "usable" }, cts.Token);
        await accept;

        // Write + verify data flows before disconnect
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        var buf = new byte[3];
        var n = await reader!.ReadAsync(buf, cts.Token);
        Assert.Equal(3, n);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Channel must still allow writes after disconnect
        await writer.WriteAsync(new byte[] { 4, 5, 6 }, cts.Token);

        // And the new data must be received
        buf = new byte[3];
        n = await reader.ReadAsync(buf, cts.Token);
        Assert.Equal(3, n);
        Assert.Equal(new byte[] { 4, 5, 6 }, buf);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 10. AcceptChannelsAsync Survives Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task AcceptChannels_ContinuesAccepting_AfterDisconnect()
    {
        // A channel opened BEFORE disconnect and one opened AFTER disconnect
        // must both be accepted by the same AcceptChannelsAsync iterator.
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var accepted = new List<ReadChannel>();
        var secondAccepted = new TaskCompletionSource();
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            {
                accepted.Add(ch);
                if (accepted.Count == 2)
                {
                    secondAccepted.TrySetResult();
                    break;
                }
            }
        });

        // Open first channel before disconnect
        await using var ch1 = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "before" }, cts.Token);
        await Task.Delay(100); // let accept process it

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Open second channel after disconnect
        await using var ch2 = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "after" }, cts.Token);

        // Both must be accepted
        await Task.WhenAny(secondAccepted.Task, Task.Delay(5000));
        Assert.Equal(2, accepted.Count);
        Assert.Equal("before", accepted[0].ChannelId);
        Assert.Equal("after", accepted[1].ChannelId);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 11. Byte Ordering Preserved Across Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task ByteOrdering_SequentialPattern_PreservedAcrossDisconnect()
    {
        // Write a sequential pattern 0..255 repeated, with disconnect in the middle.
        // Every byte on the receiver must match the exact position in the sequence.
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
            new ChannelOptions { ChannelId = "ordering" }, cts.Token);
        await accept;

        var totalBytes = 10_000;
        var expected = new byte[totalBytes];
        for (int i = 0; i < totalBytes; i++) expected[i] = (byte)(i % 256);

        // Start reading in background
        var received = new byte[totalBytes];
        var readTask = Task.Run(async () =>
        {
            var totalRead = 0;
            while (totalRead < totalBytes)
            {
                var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
            }
            return totalRead;
        });

        // Write first 5000 bytes
        await writer.WriteAsync(expected.AsMemory(0, 5000), cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write remaining 5000 bytes
        await writer.WriteAsync(expected.AsMemory(5000, 5000), cts.Token);

        var totalRead = await readTask;

        // Every single byte must be in the right position
        Assert.Equal(totalBytes, totalRead);
        for (int i = 0; i < totalBytes; i++)
        {
            Assert.Equal((byte)(i % 256), received[i]);
        }

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ByteOrdering_NoDuplicates_AcrossDisconnect()
    {
        // Use a strict counter pattern. If any byte is duplicated or skipped,
        // the assertion catches it. This guards against replay-without-dedup bugs.
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
            new ChannelOptions { ChannelId = "nodedup" }, cts.Token);
        await accept;

        // 200 writes of 4-byte counter values = 800 bytes total
        var totalWrites = 200;
        var totalBytes = totalWrites * 4;
        var expected = new byte[totalBytes];

        // Start reading in background
        var received = new byte[totalBytes];
        var readTask = Task.Run(async () =>
        {
            var totalRead = 0;
            while (totalRead < totalBytes)
            {
                var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
                if (n == 0) break;
                totalRead += n;
            }
            return totalRead;
        });

        for (int seq = 0; seq < totalWrites; seq++)
        {
            var buf = new byte[4];
            BitConverter.TryWriteBytes(buf, seq);
            Buffer.BlockCopy(buf, 0, expected, seq * 4, 4);
            await writer.WriteAsync(buf, cts.Token);

            // Kill transport at the midpoint
            if (seq == totalWrites / 2)
            {
                await pipe.DisposeAsync();
                await Task.Delay(200);
            }
        }

        var totalRead = await readTask;
        Assert.Equal(totalBytes, totalRead);

        // Verify every 4-byte counter is exactly sequential — no gaps, no duplicates
        for (int seq = 0; seq < totalWrites; seq++)
        {
            var actual = BitConverter.ToInt32(received, seq * 4);
            Assert.Equal(seq, actual);
        }

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 12. Concurrent Channels During Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task ConcurrentChannels_AllDataDelivered_AfterDisconnect()
    {
        // 5 channels, each sending 1KB concurrently. Disconnect mid-transfer.
        // All 5 channels must deliver all their data.
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        const int channelCount = 5;
        const int dataSize = 1024;
        var writers = new WriteChannel[channelCount];
        var readers = new ReadChannel[channelCount];
        var sendData = new byte[channelCount][];

        // Accept channels in background
        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            {
                // Map by channel ID suffix to correct index
                var idx = int.Parse(ch.ChannelId.Replace("concurrent-", ""));
                readers[idx] = ch;
                count++;
                if (count == channelCount) break;
            }
        });

        // Open channels
        for (int i = 0; i < channelCount; i++)
        {
            writers[i] = await mux1.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"concurrent-{i}" }, cts.Token);
            sendData[i] = new byte[dataSize];
            for (int j = 0; j < dataSize; j++) sendData[i][j] = (byte)((i * 37 + j) % 256);
        }
        await acceptTask;

        // Start reading on all channels in background
        var readTasks = new Task<byte[]>[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            var idx = i;
            readTasks[i] = Task.Run(async () =>
            {
                var buf = new byte[dataSize];
                var totalRead = 0;
                while (totalRead < dataSize)
                {
                    var n = await readers[idx].ReadAsync(buf.AsMemory(totalRead), cts.Token);
                    if (n == 0) break;
                    totalRead += n;
                }
                Assert.Equal(dataSize, totalRead);
                return buf;
            });
        }

        // Write first half on all channels
        for (int i = 0; i < channelCount; i++)
            await writers[i].WriteAsync(sendData[i].AsMemory(0, dataSize / 2), cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Write second half on all channels
        for (int i = 0; i < channelCount; i++)
            await writers[i].WriteAsync(sendData[i].AsMemory(dataSize / 2), cts.Token);

        // Verify all channels received correct data
        for (int i = 0; i < channelCount; i++)
        {
            var received = await readTasks[i];
            Assert.Equal(sendData[i], received);
        }

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 13. Graceful Close After Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task CloseAsync_AfterDisconnect_DeliversFin()
    {
        // After disconnect, CloseAsync should deliver a FIN to the remote reader.
        // The remote ReadAsync should return 0 (EOF) after all buffered data.
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
            new ChannelOptions { ChannelId = "fin" }, cts.Token);
        await accept;

        // Send some data, then kill transport
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, cts.Token);
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Close the writer after disconnect — must send FIN
        await writer.WriteAsync(new byte[] { 4, 5 }, cts.Token);
        await writer.CloseAsync(cts.Token);

        // Reader must get all 5 bytes, then EOF (ReadAsync returns 0)
        var all = new byte[5];
        var totalRead = 0;
        while (totalRead < 5)
        {
            var n = await reader!.ReadAsync(all.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(5, totalRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, all);

        // After all data, ReadAsync must return 0 (EOF)
        var eof = await reader!.ReadAsync(new byte[1], cts.Token);
        Assert.Equal(0, eof);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task DisposeWriter_AfterDisconnect_ReaderGetsAllData()
    {
        // Disposing the writer after disconnect must still deliver all data
        // to the remote reader before signaling EOF.
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
        var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "dispeof" }, cts.Token);
        await accept;

        // Write data, kill transport, write more, dispose writer
        await writer.WriteAsync(new byte[] { 10, 20, 30 }, cts.Token);
        await pipe.DisposeAsync();
        await Task.Delay(200);
        await writer.WriteAsync(new byte[] { 40, 50 }, cts.Token);
        await writer.DisposeAsync();

        // Reader must get all 5 bytes
        var all = new byte[5];
        var totalRead = 0;
        while (totalRead < 5)
        {
            var n = await reader!.ReadAsync(all.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(5, totalRead);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, all);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion



    #region 15. Bidirectional Data Integrity

    [Fact(Timeout = TestTimeout)]
    public async Task Bidirectional_BothDirections_AllDataReceived()
    {
        // Both sides send data, then disconnect, then send more.
        // Each side must receive all data from the other.
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // mux1 → mux2
        ReadChannel? reader1to2 = null;
        var accept1 = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader1to2 = ch; break; }
        });
        await using var writer1to2 = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "fwd" }, cts.Token);
        await accept1;

        // mux2 → mux1
        ReadChannel? reader2to1 = null;
        var accept2 = Task.Run(async () =>
        {
            await foreach (var ch in mux1.AcceptChannelsAsync(cts.Token))
            { reader2to1 = ch; break; }
        });
        await using var writer2to1 = await mux2.OpenChannelAsync(
            new ChannelOptions { ChannelId = "rev" }, cts.Token);
        await accept2;

        // Send in both directions before disconnect
        var fwdBefore = new byte[] { 1, 2, 3, 4, 5 };
        var revBefore = new byte[] { 10, 20, 30, 40, 50 };
        await writer1to2.WriteAsync(fwdBefore, cts.Token);
        await writer2to1.WriteAsync(revBefore, cts.Token);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Send in both directions after disconnect
        var fwdAfter = new byte[] { 6, 7, 8, 9, 10 };
        var revAfter = new byte[] { 60, 70, 80, 90, 100 };
        await writer1to2.WriteAsync(fwdAfter, cts.Token);
        await writer2to1.WriteAsync(revAfter, cts.Token);

        // Read forward direction — all 10 bytes
        var fwdExpected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var fwdReceived = new byte[10];
        var fwdRead = 0;
        while (fwdRead < 10)
        {
            var n = await reader1to2!.ReadAsync(fwdReceived.AsMemory(fwdRead), cts.Token);
            if (n == 0) break;
            fwdRead += n;
        }
        Assert.Equal(10, fwdRead);
        Assert.Equal(fwdExpected, fwdReceived);

        // Read reverse direction — all 10 bytes
        var revExpected = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var revReceived = new byte[10];
        var revRead = 0;
        while (revRead < 10)
        {
            var n = await reader2to1!.ReadAsync(revReceived.AsMemory(revRead), cts.Token);
            if (n == 0) break;
            revRead += n;
        }
        Assert.Equal(10, revRead);
        Assert.Equal(revExpected, revReceived);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 16. SyncState Byte Tracking Across Disconnect

    [Fact(Timeout = TestTimeout)]
    public async Task SyncState_BytesSent_Monotonic_AcrossDisconnect()
    {
        // BytesSent must keep counting across disconnect, never reset.
        await using var pipe = new DuplexPipe();
        var opts = NoReconnOptions();
        var (mux1, mux2, run1, run2) = await TestMuxHelper.CreateMuxPairAsync(pipe, opts, opts);
        await using var m1 = mux1;
        await using var m2 = mux2;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "synctrack" }, cts.Token);

        // Send 1000 bytes before disconnect
        await writer.WriteAsync(new byte[1000], cts.Token);
        Assert.Equal(1000, writer.SyncState.BytesSent);

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Send 500 bytes after disconnect
        await writer.WriteAsync(new byte[500], cts.Token);

        // BytesSent must be 1500, not reset to 500
        Assert.Equal(1500, writer.SyncState.BytesSent);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 17. Stress — Rapid Write During Disconnect Window

    [Fact(Timeout = TestTimeout)]
    public async Task RapidWrites_AfterDisconnect_AllArrivedInOrder()
    {
        // Multiple rapid writes immediately after disconnect must all arrive.
        // This tests that the write buffer works during the disconnect window.
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
            new ChannelOptions { ChannelId = "rapid" }, cts.Token);
        await accept;

        // Kill transport
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Rapidly write 20 chunks of 50 bytes each immediately after disconnect
        var totalBytes = 20 * 50;
        var expected = new byte[totalBytes];
        for (int i = 0; i < 20; i++)
        {
            var chunk = new byte[50];
            for (int j = 0; j < 50; j++) chunk[j] = (byte)(i * 50 + j);
            Buffer.BlockCopy(chunk, 0, expected, i * 50, 50);
            await writer.WriteAsync(chunk, cts.Token);
        }

        // All 1000 bytes must arrive in order
        var received = new byte[totalBytes];
        var totalRead = 0;
        while (totalRead < totalBytes)
        {
            var n = await reader!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        Assert.Equal(totalBytes, totalRead);
        Assert.Equal(expected, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 18. Read Blocked During Disconnect — Must Resume

    [Fact(Timeout = TestTimeout)]
    public async Task ReadBlocked_DuringDisconnect_ResumesAfterReconnect()
    {
        // A ReadAsync call that is blocked waiting for data when the transport drops
        // must resume and deliver data once the transport is restored.
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
            new ChannelOptions { ChannelId = "blockread" }, cts.Token);
        await accept;

        // Start a ReadAsync that will block — no data sent yet
        var buffer = new byte[100];
        var readTask = Task.Run(async () =>
        {
            return await reader!.ReadAsync(buffer, cts.Token);
        });

        await Task.Delay(100); // ensure read is blocked

        // Kill transport while read is blocked
        await pipe.DisposeAsync();
        await Task.Delay(200);

        // Now send data (after disconnect) — read must eventually get it
        await writer.WriteAsync(new byte[] { 42, 43, 44 }, cts.Token);

        var bytesRead = await readTask;
        Assert.True(bytesRead > 0);
        Assert.Equal(42, buffer[0]);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion
}
