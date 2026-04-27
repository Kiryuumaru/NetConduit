using NetConduit.Enums;
using NetConduit.Models;
namespace NetConduit.UnitTests;

/// <summary>
/// Tests that verify the mux recovers from transport disconnection.
///
/// Reconnection tests (regions 1, 3-6, 9-10, 13, 16-18) use in-memory
/// DuplexPipe — they validate reconnection mechanics only.
///
/// Data integrity tests (regions 2, 7-8, 11-12, 15, 19) use
/// InFlightLossDuplexPipe with HoldableWriteStream to verify that
/// in-flight data is replayed after reconnection. The ring buffer is
/// sized automatically from the credit window (MaxCredits).
/// </summary>
[Collection("HighMemory")]
public class DisconnectionDataLossTests
{
    private const int TestTimeout = 60000;

    /// <summary>
    /// Registers OnReconnected handlers on both muxes and returns a Task
    /// that completes when both have reconnected. Call BEFORE disconnecting
    /// to avoid the race where reconnection completes before handlers fire.
    /// </summary>
    private static Task PrepareReconnectWait(StreamMultiplexer mux1, StreamMultiplexer mux2, CancellationToken ct)
    {
        var tcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mux1.OnReconnected += () => tcs1.TrySetResult();
        mux2.OnReconnected += () => tcs2.TrySetResult();
        return Task.WhenAll(tcs1.Task.WaitAsync(ct), tcs2.Task.WaitAsync(ct));
    }

    #region 1. Write After Disconnect — Channel Must Survive

    [Fact(Timeout = TestTimeout)]
    public async Task WriteAfterDisconnect_SmallData_Succeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);

        // Kill transport
        await pipe.DisconnectAsync();
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);

        // Kill transport
        await pipe.DisconnectAsync();
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);

        // Kill transport
        await pipe.DisconnectAsync();
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
        // Data written before disconnect includes bytes in-flight in the transport
        // buffer. All bytes — including in-flight — must eventually reach the receiver.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "ch1" }, cts.Token);
        await accept;

        // Phase 1: send baseline data (delivered normally)
        var baseline = new byte[2000];
        Random.Shared.NextBytes(baseline);
        await writer.WriteAsync(baseline, cts.Token);

        // Phase 2: hold writes — simulates OS socket send buffer
        pipe.StartHolding();
        var inFlight = new byte[2096];
        Random.Shared.NextBytes(inFlight);
        await writer.WriteAsync(inFlight, cts.Token);
        await Task.Delay(200);

        // Drop held data + disconnect
        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Must receive ALL data — baseline + in-flight
        var totalExpected = baseline.Length + inFlight.Length;
        var expected = new byte[totalExpected];
        Buffer.BlockCopy(baseline, 0, expected, 0, baseline.Length);
        Buffer.BlockCopy(inFlight, 0, expected, baseline.Length, inFlight.Length);

        var received = new byte[totalExpected];
        var totalRead = 0;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (totalRead < totalExpected)
            {
                var n = await reader!.ReadAsync(received.AsMemory(totalRead), readCts.Token);
                if (n == 0) break;
                totalRead += n;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(totalExpected, totalRead);
        Assert.Equal(expected, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ReadAfterDisconnect_DataSentAfterDisconnect_Received()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        // Kill transport
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        // Kill transport
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        Assert.True(mux1.IsConnected);
        Assert.True(mux2.IsConnected);

        // Kill transport
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Mux must recover to connected state
        Assert.True(mux1.IsConnected);
        Assert.True(mux2.IsConnected);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task MuxIsRunning_TrueAfterDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        Assert.True(mux1.IsRunning);

        // Kill transport
        await pipe.DisconnectAsync();
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        var writers = new WriteChannel[3];
        for (int i = 0; i < 3; i++)
        {
            writers[i] = await mux1.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"ch{i}" }, cts.Token);
        }

        // Kill transport
        await pipe.DisconnectAsync();
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        // Open channel before disconnect
        await using var existingCh = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "existing" }, cts.Token);

        // Kill transport
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        // Forward: mux1 → mux2
        await using var fwdWriter = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "fwd" }, cts.Token);

        // Reverse: mux2 → mux1
        await using var revWriter = await mux2.OpenChannelAsync(
            new ChannelOptions { ChannelId = "rev" }, cts.Token);

        // Kill transport
        await pipe.DisconnectAsync();
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
        // Baseline data delivered normally. In-flight data held in transport buffer
        // during disconnect. Post-reconnect data sent on new transport.
        // All three phases must be received without gaps.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "integrity" }, cts.Token);
        await accept;

        // Phase 1: baseline
        var before = new byte[] { 1, 2, 3, 4, 5 };
        await writer.WriteAsync(before, cts.Token);

        // Phase 2: in-flight during disconnect
        pipe.StartHolding();
        var inFlight = new byte[] { 100, 101, 102, 103, 104 };
        await writer.WriteAsync(inFlight, cts.Token);
        await Task.Delay(200);

        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Phase 3: after reconnect
        var after = new byte[] { 6, 7, 8, 9, 10 };
        await writer.WriteAsync(after, cts.Token);

        // Must receive all 15 bytes in order
        var totalExpected = before.Length + inFlight.Length + after.Length;
        var received = new byte[totalExpected];
        var totalRead = 0;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (totalRead < totalExpected)
            {
                var n = await reader!.ReadAsync(received.AsMemory(totalRead), readCts.Token);
                if (n == 0) break;
                totalRead += n;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(totalExpected, totalRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 100, 101, 102, 103, 104, 6, 7, 8, 9, 10 }, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 8. In-Flight Data at Disconnect — No Silent Loss

    [Fact(Timeout = TestTimeout)]
    public async Task InFlightData_SmallChunks_NotSilentlyDropped()
    {
        // 10 small chunks written while transport buffer is held. After drop + disconnect,
        // the receiver must still get every byte if replay is implemented.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "inflight" }, cts.Token);
        await accept;

        var allData = new byte[1000];
        for (int i = 0; i < 1000; i++) allData[i] = (byte)(i % 256);

        // Hold writes — all 10 chunks go into simulated OS buffer
        pipe.StartHolding();
        for (int i = 0; i < 10; i++)
            await writer.WriteAsync(allData.AsMemory(i * 100, 100), cts.Token);
        await Task.Delay(200);

        // Drop + disconnect — simulates in-flight data loss
        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Must get all 1000 bytes
        var received = new byte[1000];
        var totalRead = 0;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (totalRead < 1000)
            {
                var n = await reader!.ReadAsync(received.AsMemory(totalRead), readCts.Token);
                if (n == 0) break;
                totalRead += n;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(1000, totalRead);
        Assert.Equal(allData, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task InFlightData_LargeTransfer_MidStreamDisconnect_AllBytesArrived()
    {
        // 1MB transfer in three phases: baseline (confirmed received),
        // in-flight (held + dropped), post-reconnect. All bytes must arrive.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var third = totalSize / 3;
        var allData = new byte[totalSize];
        for (int i = 0; i < totalSize; i++) allData[i] = (byte)((i * 7 + 13) % 256);

        // Phase 1: send first third normally, confirm receipt
        var phase1Received = new byte[third];
        var phase1Read = 0;
        var readPhase1 = Task.Run(async () =>
        {
            while (phase1Read < third)
            {
                var n = await reader!.ReadAsync(phase1Received.AsMemory(phase1Read), cts.Token);
                if (n == 0) break;
                phase1Read += n;
            }
        });
        for (int offset = 0; offset < third; offset += 8192)
        {
            var chunk = Math.Min(8192, third - offset);
            await writer.WriteAsync(allData.AsMemory(offset, chunk), cts.Token);
        }
        await readPhase1;
        Assert.Equal(third, phase1Read);

        // Phase 2: hold writes — in-flight data
        pipe.StartHolding();
        for (int offset = third; offset < third * 2; offset += 8192)
        {
            var chunk = Math.Min(8192, third * 2 - offset);
            await writer.WriteAsync(allData.AsMemory(offset, chunk), cts.Token);
        }
        await Task.Delay(200);

        // Drop + disconnect
        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Phase 3: send last third after reconnect
        for (int offset = third * 2; offset < totalSize; offset += 8192)
        {
            var chunk = Math.Min(8192, totalSize - offset);
            await writer.WriteAsync(allData.AsMemory(offset, chunk), cts.Token);
        }

        // Read remaining (phase 2 + phase 3)
        var remaining = totalSize - phase1Read;
        var restReceived = new byte[remaining];
        var restRead = 0;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (restRead < remaining)
            {
                var n = await reader!.ReadAsync(restReceived.AsMemory(restRead), readCts.Token);
                if (n == 0) break;
                restRead += n;
            }
        }
        catch (OperationCanceledException) { }

        var totalRead = phase1Read + restRead;
        Assert.Equal(totalSize, totalRead);

        // Verify all content matches — phase 1 was already checked, verify phases 2+3
        Assert.Equal(allData.AsSpan(phase1Read, restRead).ToArray(), restReceived.AsSpan(0, restRead).ToArray());

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Open second channel after disconnect
        await using var ch2 = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "after" }, cts.Token);

        // Both must be accepted
        await secondAccepted.Task.WaitAsync(cts.Token);
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
        // Sequential pattern 0..255 repeated across 10KB. Middle section is in-flight
        // (held in transport buffer) during disconnect. Every byte must arrive in order.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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

        // Phase 1: first 3000 bytes normally
        await writer.WriteAsync(expected.AsMemory(0, 3000), cts.Token);

        // Phase 2: next 4000 bytes in-flight
        pipe.StartHolding();
        await writer.WriteAsync(expected.AsMemory(3000, 4000), cts.Token);
        await Task.Delay(200);

        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Phase 3: remaining 3000 bytes after reconnect
        await writer.WriteAsync(expected.AsMemory(7000, 3000), cts.Token);

        // Read all with timeout
        var received = new byte[totalBytes];
        var totalRead = 0;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (totalRead < totalBytes)
            {
                var n = await reader!.ReadAsync(received.AsMemory(totalRead), readCts.Token);
                if (n == 0) break;
                totalRead += n;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(totalBytes, totalRead);
        for (int i = 0; i < totalBytes; i++)
            Assert.Equal((byte)(i % 256), received[i]);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = TestTimeout)]
    public async Task ByteOrdering_NoDuplicates_AcrossDisconnect()
    {
        // 200 sequential 4-byte counters. Middle 40 are in-flight during disconnect.
        // Every counter must arrive exactly once, in order — no gaps, no duplicates.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "nodedup" }, cts.Token);
        await accept;

        var totalWrites = 200;
        var totalBytes = totalWrites * 4;
        var expected = new byte[totalBytes];
        for (int seq = 0; seq < totalWrites; seq++)
        {
            var buf = new byte[4];
            BitConverter.TryWriteBytes(buf, seq);
            Buffer.BlockCopy(buf, 0, expected, seq * 4, 4);
        }

        // Phase 1: first 80 counters normally — confirm receipt before holding
        for (int seq = 0; seq < 80; seq++)
            await writer.WriteAsync(expected.AsMemory(seq * 4, 4), cts.Token);
        var phase1Buf = new byte[320];
        var phase1Read = 0;
        while (phase1Read < 320)
        {
            var n = await reader!.ReadAsync(phase1Buf.AsMemory(phase1Read), cts.Token);
            if (n == 0) break;
            phase1Read += n;
        }

        // Phase 2: next 40 counters in-flight
        pipe.StartHolding();
        for (int seq = 80; seq < 120; seq++)
            await writer.WriteAsync(expected.AsMemory(seq * 4, 4), cts.Token);
        await Task.Delay(200);

        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Phase 3: remaining 80 counters after reconnect
        for (int seq = 120; seq < totalWrites; seq++)
            await writer.WriteAsync(expected.AsMemory(seq * 4, 4), cts.Token);

        // Read remaining (phase 2 replayed + phase 3)
        var remaining = totalBytes - phase1Read;
        var restBuf = new byte[remaining];
        var restRead = 0;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (restRead < remaining)
            {
                var n = await reader!.ReadAsync(restBuf.AsMemory(restRead), readCts.Token);
                if (n == 0) break;
                restRead += n;
            }
        }
        catch (OperationCanceledException) { }

        var received = new byte[totalBytes];
        Buffer.BlockCopy(phase1Buf, 0, received, 0, phase1Read);
        Buffer.BlockCopy(restBuf, 0, received, phase1Read, restRead);
        var totalRead = phase1Read + restRead;

        Assert.Equal(totalBytes, totalRead);
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
        // 5 channels, each with three phases: baseline, in-flight (held), post-reconnect.
        // All channels must deliver all data including in-flight bytes.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        const int channelCount = 5;
        const int dataSize = 1024;
        var writers = new WriteChannel[channelCount];
        var readers = new ReadChannel[channelCount];
        var sendData = new byte[channelCount][];

        var acceptTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            {
                var idx = int.Parse(ch.ChannelId.Replace("concurrent-", ""));
                readers[idx] = ch;
                count++;
                if (count == channelCount) break;
            }
        });

        for (int i = 0; i < channelCount; i++)
        {
            writers[i] = await mux1.OpenChannelAsync(
                new ChannelOptions { ChannelId = $"concurrent-{i}" }, cts.Token);
            sendData[i] = new byte[dataSize];
            for (int j = 0; j < dataSize; j++) sendData[i][j] = (byte)((i * 37 + j) % 256);
        }
        await acceptTask;

        // Phase 1: first third on all channels
        var third = dataSize / 3;
        for (int i = 0; i < channelCount; i++)
            await writers[i].WriteAsync(sendData[i].AsMemory(0, third), cts.Token);

        // Phase 2: middle third in-flight
        pipe.StartHolding();
        for (int i = 0; i < channelCount; i++)
            await writers[i].WriteAsync(sendData[i].AsMemory(third, third), cts.Token);
        await Task.Delay(200);

        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Phase 3: last third after reconnect
        for (int i = 0; i < channelCount; i++)
            await writers[i].WriteAsync(sendData[i].AsMemory(third * 2), cts.Token);

        // Verify all channels
        for (int i = 0; i < channelCount; i++)
        {
            var buf = new byte[dataSize];
            var totalRead = 0;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                while (totalRead < dataSize)
                {
                    var n = await readers[i].ReadAsync(buf.AsMemory(totalRead), readCts.Token);
                    if (n == 0) break;
                    totalRead += n;
                }
            }
            catch (OperationCanceledException) { }
            Assert.Equal(dataSize, totalRead);
            Assert.Equal(sendData[i], buf);
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;
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
        // Both directions send baseline, in-flight, and post-reconnect data.
        // Forward direction (mux1→mux2) has in-flight data via HoldableWriteStream.
        // Both directions must receive all bytes.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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

        // Phase 1: baseline in both directions
        var fwdBefore = new byte[] { 1, 2, 3, 4, 5 };
        var revBefore = new byte[] { 10, 20, 30, 40, 50 };
        await writer1to2.WriteAsync(fwdBefore, cts.Token);
        await writer2to1.WriteAsync(revBefore, cts.Token);

        // Phase 2: in-flight data (HoldableWriteStream wraps mux1's write side)
        pipe.StartHolding();
        var fwdInFlight = new byte[] { 100, 101, 102, 103, 104 };
        await writer1to2.WriteAsync(fwdInFlight, cts.Token);
        await Task.Delay(200);

        pipe.DropHeld();
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Phase 3: after reconnect
        var fwdAfter = new byte[] { 6, 7, 8, 9, 10 };
        var revAfter = new byte[] { 60, 70, 80, 90, 100 };
        await writer1to2.WriteAsync(fwdAfter, cts.Token);
        await writer2to1.WriteAsync(revAfter, cts.Token);

        // Read forward direction — all 15 bytes (baseline + in-flight + after)
        var fwdExpected = new byte[] { 1, 2, 3, 4, 5, 100, 101, 102, 103, 104, 6, 7, 8, 9, 10 };
        var fwdReceived = new byte[15];
        var fwdRead = 0;
        using var fwdCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        fwdCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (fwdRead < 15)
            {
                var n = await reader1to2!.ReadAsync(fwdReceived.AsMemory(fwdRead), fwdCts.Token);
                if (n == 0) break;
                fwdRead += n;
            }
        }
        catch (OperationCanceledException) { }

        Assert.Equal(15, fwdRead);
        Assert.Equal(fwdExpected, fwdReceived);

        // Read reverse direction — all 10 bytes (no in-flight loss in this direction)
        var revExpected = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
        var revReceived = new byte[10];
        var revRead = 0;
        using var revCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        revCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (revRead < 10)
            {
                var n = await reader2to1!.ReadAsync(revReceived.AsMemory(revRead), revCts.Token);
                if (n == 0) break;
                revRead += n;
            }
        }
        catch (OperationCanceledException) { }

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "synctrack" }, cts.Token);

        // Send 1000 bytes before disconnect
        await writer.WriteAsync(new byte[1000], cts.Token);
        Assert.Equal(1000, writer.SyncState.BytesSent);

        // Kill transport
        await pipe.DisconnectAsync();
        await Task.Delay(200);

        // Send 500 bytes after disconnect
        await writer.WriteAsync(new byte[500], cts.Token);

        // BytesSent must be 1500, not reset to 500
        Assert.Equal(1500, writer.SyncState.BytesSent);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 17. Rapid Writes Buffered During Reconnect

    [Fact(Timeout = TestTimeout)]
    public async Task RapidWrites_AfterDisconnect_AllArrivedInOrder()
    {
        // All writes happen after disconnect. They buffer in the FrameWriter Pipe
        // and drain to the new transport after reconnect.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateReconnectableMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

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
        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();
        await reconnected;

        // Now send data (after disconnect) — read must eventually get it
        await writer.WriteAsync(new byte[] { 42, 43, 44 }, cts.Token);

        var bytesRead = await readTask;
        Assert.True(bytesRead > 0);
        Assert.Equal(42, buffer[0]);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion

    #region 19. In-Flight Transport Data Loss — Holdable Write Stream

    /// <summary>
    /// Proves that data accepted by the transport but not delivered to the
    /// receiver is recovered via the Layer 4 replay buffer after reconnection.
    ///
    /// Uses a HoldableWriteStream to intercept mux1's transport writes.
    /// When holding is active, the FlushLoop's writes are accepted (sender
    /// thinks they were sent) but buffered locally — simulating an OS socket
    /// send buffer. DropHeld() discards the buffer, then disconnect kills the
    /// transport. After reconnect, the ring buffer replays unacked bytes.
    ///
    /// Sequence:
    ///   1. Send 2000 bytes, confirm receipt (baseline)
    ///   2. StartHolding — mux1's transport writes go to hold buffer
    ///   3. Send 5000 bytes — FlushLoop drains FrameWriter Pipe, writes go
    ///      to hold buffer (accepted but never delivered to transport pipe)
    ///   4. DropHeld — 5000 bytes discarded (simulating OS buffer loss)
    ///   5. Disconnect + auto-reconnect
    ///   6. Send 2000 more bytes on new transport
    ///   7. Assert total received = 9000 (replay recovers in-flight data)
    /// </summary>
    [Fact(Timeout = TestTimeout)]
    public async Task InFlightData_HoldableTransport_LostOnDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (mux1, mux2, run1, run2, pipe) = await TestMuxHelper.CreateInFlightLossMuxPairAsync(cancellationToken: cts.Token);
        await using var m1 = mux1;
        await using var m2 = mux2;

        ReadChannel? reader = null;
        var accept = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
            { reader = ch; break; }
        });
        await using var writer = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "inflight-hold" }, cts.Token);
        await accept;

        // --- Phase 1: baseline — send 2000 bytes, confirm receipt ---
        var phase1 = new byte[2000];
        for (int i = 0; i < phase1.Length; i++) phase1[i] = (byte)(i % 251);
        await writer.WriteAsync(phase1, cts.Token);

        var phase1Buf = new byte[2000];
        var phase1Read = 0;
        while (phase1Read < 2000)
        {
            var n = await reader!.ReadAsync(phase1Buf.AsMemory(phase1Read), cts.Token);
            Assert.True(n > 0, "EOF before receiving phase 1 data");
            phase1Read += n;
        }
        Assert.Equal(phase1, phase1Buf);

        // --- Phase 2: hold writes, send data that becomes "in-flight" ---
        pipe.StartHolding();

        var phase2 = new byte[5000];
        for (int i = 0; i < phase2.Length; i++) phase2[i] = (byte)((i + 100) % 251);
        await writer.WriteAsync(phase2, cts.Token);

        // Wait for FlushLoop to drain FrameWriter's Pipe to the HoldableWriteStream.
        // FlushMode.Batched fires every 1ms; 200ms is more than enough.
        await Task.Delay(200);

        // --- Phase 3: drop held data + disconnect ---
        var droppedBytes = pipe.DropHeld();
        Assert.True(droppedBytes > 0, "Expected held bytes > 0 (FlushLoop should have drained to hold buffer)");

        var reconnected = PrepareReconnectWait(mux1, mux2, cts.Token);
        await pipe.DisconnectAsync();

        // Wait for auto-reconnect
        await reconnected;

        // --- Phase 4: send more data on new transport ---
        var phase3 = new byte[2000];
        for (int i = 0; i < phase3.Length; i++) phase3[i] = (byte)((i + 200) % 251);
        await writer.WriteAsync(phase3, cts.Token);

        // --- Phase 5: read everything and assert ---
        var totalExpected = phase1.Length + phase2.Length + phase3.Length; // 9000
        var allReceived = new byte[totalExpected];
        Buffer.BlockCopy(phase1Buf, 0, allReceived, 0, phase1Read);
        var totalRead = phase1Read;

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (totalRead < totalExpected)
            {
                var n = await reader!.ReadAsync(allReceived.AsMemory(totalRead), readCts.Token);
                if (n == 0) break;
                totalRead += n;
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out — didn't get all expected bytes (expected behavior when data is lost)
        }

        // Without replay, totalRead ≈ 4000 (2000 phase1 + 2000 phase3).
        // With replay, all 9000 bytes arrive including the 5000 held+dropped.
        Assert.Equal(totalExpected, totalRead);

        // Verify content of replayed + post-reconnect data
        var expectedRest = new byte[phase2.Length + phase3.Length];
        Buffer.BlockCopy(phase2, 0, expectedRest, 0, phase2.Length);
        Buffer.BlockCopy(phase3, 0, expectedRest, phase2.Length, phase3.Length);
        Assert.Equal(expectedRest, allReceived.AsSpan(phase1Read, totalRead - phase1Read).ToArray());

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #endregion
}
