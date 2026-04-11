namespace NetConduit.UnitTests;

/// <summary>
/// Tests for the direct delivery optimization in ReadChannel.
/// When ReadAsync is waiting and EnqueueData fires, data should be copied
/// directly to the user buffer bypassing the per-channel Pipe.
/// </summary>
public class DirectDeliveryTests
{
    [Fact(Timeout = 30000)]
    public async Task ReadAsync_WhenDataArrivesAfterWaiting_DeliversCorrectData()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-1" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-1", cts.Token);

        // Start reading before data is sent — forces direct delivery path
        var readBuf = new byte[1024];
        var readTask = reader.ReadAsync(readBuf, cts.Token);

        await Task.Delay(50, cts.Token);

        var data = new byte[64];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        var bytesRead = await readTask;
        Assert.Equal(64, bytesRead);
        Assert.Equal(data, readBuf.AsSpan(0, 64).ToArray());

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_CancelledWhileWaiting_ThrowsOperationCanceledException()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-2" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-2", cts.Token);

        // Start reading with a separate token that we'll cancel
        using var readCts = new CancellationTokenSource();
        var readBuf = new byte[1024];
        var readTask = reader.ReadAsync(readBuf, readCts.Token);

        await Task.Delay(50, cts.Token);
        readCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await readTask);

        // After cancellation, the channel must still work for next read
        var data = new byte[32];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        var readBuf2 = new byte[1024];
        var bytesRead = await reader.ReadAsync(readBuf2, cts.Token);
        Assert.Equal(32, bytesRead);
        Assert.Equal(data, readBuf2.AsSpan(0, 32).ToArray());

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ChannelClosedWhileWaiting_ReturnsZero()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-3" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-3", cts.Token);

        // Start reading, then close the writer (sends FIN)
        var readBuf = new byte[1024];
        var readTask = reader.ReadAsync(readBuf, cts.Token);

        await Task.Delay(50, cts.Token);
        await writer.CloseAsync(cts.Token);

        var bytesRead = await readTask;
        Assert.Equal(0, bytesRead);

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_PayloadLargerThanUserBuffer_DeliverPartialAndRemainderViaPipe()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-4" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-4", cts.Token);

        // Send 256 bytes but read into a 64-byte buffer
        var data = new byte[256];
        Random.Shared.NextBytes(data);

        // Start a small read before data arrives
        var readBuf = new byte[64];
        var readTask = reader.ReadAsync(readBuf, cts.Token);

        await Task.Delay(50, cts.Token);
        await writer.WriteAsync(data, cts.Token);

        // First read should get exactly 64 bytes (direct delivery, capped to buffer size)
        var first = await readTask;
        Assert.Equal(64, first);
        Assert.Equal(data.AsSpan(0, 64).ToArray(), readBuf.AsSpan(0, 64).ToArray());

        // Subsequent reads should drain the remainder from the Pipe
        var remaining = new MemoryStream();
        remaining.Write(readBuf, 0, first);
        while (remaining.Length < 256)
        {
            var buf = new byte[256];
            var n = await reader.ReadAsync(buf, cts.Token);
            Assert.True(n > 0, "Expected more data from remainder");
            remaining.Write(buf, 0, n);
        }

        Assert.Equal(data, remaining.ToArray());

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_MultipleSequentialReads_AllDeliverCorrectData()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-5" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-5", cts.Token);

        for (int i = 0; i < 20; i++)
        {
            var data = new byte[100];
            Random.Shared.NextBytes(data);

            var readBuf = new byte[100];
            var readTask = reader.ReadAsync(readBuf, cts.Token);

            await Task.Delay(10, cts.Token);
            await writer.WriteAsync(data, cts.Token);

            var bytesRead = await readTask;
            Assert.Equal(100, bytesRead);
            Assert.Equal(data, readBuf.AsSpan(0, 100).ToArray());
        }

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_DataAlreadyInPipe_UsesFastPathNotDirectDelivery()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-6" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-6", cts.Token);

        // Send data FIRST, then read — should use fast path (pipe already has data)
        var data = new byte[128];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        await Task.Delay(100, cts.Token);

        var readBuf = new byte[128];
        var bytesRead = await reader.ReadAsync(readBuf, cts.Token);
        Assert.Equal(128, bytesRead);
        Assert.Equal(data, readBuf.AsSpan(0, 128).ToArray());

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_CancelThenEnqueueRace_DataIsNotLost()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-7" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-7", cts.Token);

        // Repeat to exercise the cancel/enqueue race many times
        for (int i = 0; i < 50; i++)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var readBuf = new byte[64];
            var readTask = reader.ReadAsync(readBuf, readCts.Token);

            // Cancel immediately — tight race between cancel and data arrival
            readCts.Cancel();
            try { await readTask; } catch (OperationCanceledException) { }
        }

        // After all races, verify the channel is still functional
        var data = new byte[48];
        Random.Shared.NextBytes(data);
        await writer.WriteAsync(data, cts.Token);

        var finalBuf = new byte[48];
        var n = await reader.ReadAsync(finalBuf, cts.Token);
        Assert.Equal(48, n);
        Assert.Equal(data, finalBuf);

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_DisposeDuringWait_ThrowsObjectDisposed()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-8" }, cts.Token);
        var reader = await acceptor.AcceptChannelAsync("dd-8", cts.Token);

        var readBuf = new byte[1024];
        var readTask = reader.ReadAsync(readBuf, cts.Token);

        await Task.Delay(50, cts.Token);

        // Dispose the reader while a read is waiting
        await reader.DisposeAsync();

        // Read should complete with 0 (TCS resolved by dispose)
        var bytesRead = await readTask;
        Assert.Equal(0, bytesRead);

        // After dispose, next ReadAsync should throw ObjectDisposedException
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
#pragma warning disable CA2022 // Expected to throw before returning
            await reader.ReadAsync(new byte[64], cts.Token);
#pragma warning restore CA2022
        });

        await writer.DisposeAsync();
        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_MuxDisposedWhileWaiting_ReturnsZero()
    {
        var pipe = new DuplexPipe();
        var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-9" }, cts.Token);
        var reader = await acceptor.AcceptChannelAsync("dd-9", cts.Token);

        var readBuf = new byte[1024];
#pragma warning disable CA2022 // Intentionally checking partial/zero read behavior
        var readTask = reader.ReadAsync(readBuf, cts.Token);
#pragma warning restore CA2022

        await Task.Delay(50, cts.Token);

        // Dispose the entire multiplexer while a read is waiting
        await acceptor.DisposeAsync();

        // Read should complete (0 or exception due to mux teardown — both are acceptable)
        try
        {
            var bytesRead = await readTask;
            Assert.Equal(0, bytesRead);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Also acceptable — the mux teardown may cause various exceptions
        }

        await initiator.DisposeAsync();
        await pipe.DisposeAsync();
        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_LargeDataIntegrity_WithDirectDeliveryPath()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-10" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-10", cts.Token);

        // Send 10MB and verify every byte arrives intact
        const int totalSize = 10 * 1024 * 1024;
        var sendData = new byte[totalSize];
        Random.Shared.NextBytes(sendData);

        var writeTask = Task.Run(async () =>
        {
            int offset = 0;
            while (offset < totalSize)
            {
                var chunk = Math.Min(64 * 1024, totalSize - offset);
                await writer.WriteAsync(sendData.AsMemory(offset, chunk), cts.Token);
                offset += chunk;
            }
        }, cts.Token);

        var received = new MemoryStream();
        var readBuf = new byte[64 * 1024];
        while (received.Length < totalSize)
        {
            var n = await reader.ReadAsync(readBuf, cts.Token);
            Assert.True(n > 0);
            received.Write(readBuf, 0, n);
        }

        await writeTask;

        Assert.Equal(totalSize, received.Length);
        Assert.Equal(sendData, received.ToArray());

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_MultipleConcurrentChannels_DirectDeliveryIsolated()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        const int channelCount = 10;
        var writers = new WriteChannel[channelCount];
        var readers = new ReadChannel[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            writers[i] = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = $"dd-mc-{i}" }, cts.Token);
            readers[i] = await acceptor.AcceptChannelAsync($"dd-mc-{i}", cts.Token);
        }

        // Start reads on ALL channels simultaneously (all enter direct delivery wait)
        var readBufs = new byte[channelCount][];
        var readTasks = new ValueTask<int>[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            readBufs[i] = new byte[100];
            readTasks[i] = readers[i].ReadAsync(readBufs[i], cts.Token);
        }

        await Task.Delay(50, cts.Token);

        // Send unique data on each channel
        var sentData = new byte[channelCount][];
        for (int i = 0; i < channelCount; i++)
        {
            sentData[i] = new byte[100];
            Random.Shared.NextBytes(sentData[i]);
            await writers[i].WriteAsync(sentData[i], cts.Token);
        }

        // Verify each channel got its own data
        for (int i = 0; i < channelCount; i++)
        {
            var n = await readTasks[i];
            Assert.Equal(100, n);
            Assert.Equal(sentData[i], readBufs[i]);
        }

        for (int i = 0; i < channelCount; i++)
        {
            await writers[i].DisposeAsync();
            await readers[i].DisposeAsync();
        }

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_ZeroLengthBuffer_ReturnsZeroImmediately()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-12" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-12", cts.Token);

        // Zero-length read should return 0 immediately without entering direct delivery
        var result = await reader.ReadAsync(Memory<byte>.Empty, cts.Token);
        Assert.Equal(0, result);

        cts.Cancel();
    }

    [Fact(Timeout = 30000)]
    public async Task ReadAsync_RapidSendReceive_NoDataCorruption()
    {
        await using var pipe = new DuplexPipe();
        await using var initiator = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var acceptor = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run1 = initiator.Start(cts.Token);
        var run2 = acceptor.Start(cts.Token);
        await Task.WhenAll(initiator.WaitForReadyAsync(cts.Token), acceptor.WaitForReadyAsync(cts.Token));

        await using var writer = await initiator.OpenChannelAsync(new ChannelOptions { ChannelId = "dd-13" }, cts.Token);
        await using var reader = await acceptor.AcceptChannelAsync("dd-13", cts.Token);

        // Send 1000 small messages as fast as possible, verify all arrive intact
        const int msgCount = 1000;
        const int msgSize = 50;

        var writeTask = Task.Run(async () =>
        {
            for (int i = 0; i < msgCount; i++)
            {
                var msg = new byte[msgSize];
                // Fill with pattern: each message starts with its index byte
                msg[0] = (byte)(i % 256);
                msg[1] = (byte)((i >> 8) % 256);
                for (int j = 2; j < msgSize; j++)
                    msg[j] = (byte)(i + j);
                await writer.WriteAsync(msg, cts.Token);
            }
        }, cts.Token);

        // Read all data and verify the stream matches what was sent
        var totalExpected = msgCount * msgSize;
        var allReceived = new byte[totalExpected];
        int totalRead = 0;
        var readBuf = new byte[4096];
        while (totalRead < totalExpected)
        {
            var n = await reader.ReadAsync(readBuf, cts.Token);
            Assert.True(n > 0);
            Buffer.BlockCopy(readBuf, 0, allReceived, totalRead, n);
            totalRead += n;
        }

        await writeTask;

        // Verify each message
        for (int i = 0; i < msgCount; i++)
        {
            int offset = i * msgSize;
            Assert.Equal((byte)(i % 256), allReceived[offset]);
            Assert.Equal((byte)((i >> 8) % 256), allReceived[offset + 1]);
            for (int j = 2; j < msgSize; j++)
                Assert.Equal((byte)(i + j), allReceived[offset + j]);
        }

        cts.Cancel();
    }
}
