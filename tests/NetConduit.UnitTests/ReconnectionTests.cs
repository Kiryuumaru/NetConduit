using NetConduit;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for auto-reconnection functionality.
/// </summary>
public class ReconnectionTests
{
    [Fact(Timeout = 120000)]
    public async Task Multiplexer_IsConnected_FalseBeforeRunAsync()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);

        // Assert
        Assert.False(mux.IsConnected);
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_IsConnected_TrueAfterRunAsync()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        // Wait a bit for handshake
        await Task.Delay(100);

        // Assert
        Assert.True(mux1.IsConnected);
        Assert.True(mux2.IsConnected);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_IsReconnecting_FalseInitially()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);

        // Assert
        Assert.False(mux.IsReconnecting);
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_OnDisconnected_EventFired()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions { EnableReconnection = true };
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        var disconnectedEvent = new TaskCompletionSource();
        mux1.OnDisconnected += () => disconnectedEvent.TrySetResult();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);
        mux1.NotifyDisconnected();

        // Assert
        var eventReceived = await Task.WhenAny(
            disconnectedEvent.Task,
            Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Equal(disconnectedEvent.Task, eventReceived);
        Assert.False(mux1.IsConnected);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ReconnectAsync_ThrowsWhenReconnectionDisabled()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions { EnableReconnection = false };
        await using var mux = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);

        await using var newPipe = new DuplexPipe();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mux.ReconnectAsync(newPipe.Stream1, newPipe.Stream1));
    }

    [Fact(Timeout = 120000)]
    public async Task NotifyDisconnected_DoesNothingWhenReconnectionDisabled()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions { EnableReconnection = false };
        await using var mux = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);

        var disconnectedFired = false;
        mux.OnDisconnected += () => disconnectedFired = true;

        // Act
        mux.NotifyDisconnected();

        // Assert - event should not fire when reconnection is disabled
        Assert.False(disconnectedFired);
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_SessionId_IsNotEmpty()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Assert
        Assert.NotEqual(Guid.Empty, mux1.SessionId);
        Assert.NotEqual(Guid.Empty, mux2.SessionId);
        Assert.NotEqual(mux1.SessionId, mux2.SessionId); // Each multiplexer should have unique session ID

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_ReconnectionOptions_AreRespected()
    {
        // Arrange
        var options = new MultiplexerOptions
        {
            EnableReconnection = true,
            ReconnectTimeout = TimeSpan.FromSeconds(30),
            ReconnectBufferSize = 2 * 1024 * 1024 // 2MB
        };

        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // NotifyDisconnected should work when reconnection is enabled
        var disconnectedEvent = new TaskCompletionSource();
        mux1.OnDisconnected += () => disconnectedEvent.TrySetResult();
        mux1.NotifyDisconnected();

        // Assert
        var eventReceived = await Task.WhenAny(
            disconnectedEvent.Task,
            Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Equal(disconnectedEvent.Task, eventReceived);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact]
    public void Multiplexer_SessionMismatch_ErrorCode_Exists()
    {
        // Verify that SessionMismatch error code is properly defined
        Assert.Equal(0x0009, (ushort)ErrorCode.SessionMismatch);
        
        // Verify exception can be created with this error code
        var ex = new MultiplexerException(ErrorCode.SessionMismatch, "Test message");
        Assert.Equal(ErrorCode.SessionMismatch, ex.ErrorCode);
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_IsConnected_FalseAfterCancellation()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);
        Assert.True(mux1.IsConnected);

        // Act
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));

        // Assert - after cancellation, IsConnected should be false
        Assert.False(mux1.IsConnected);
    }

    [Fact(Timeout = 120000)]
    public async Task ReconnectAsync_ThrowsWhenDisposed()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions { EnableReconnection = true };
        var mux = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);

        // Dispose the multiplexer
        await mux.DisposeAsync();

        await using var newPipe = new DuplexPipe();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            mux.ReconnectAsync(newPipe.Stream1, newPipe.Stream1));
    }

    [Fact(Timeout = 120000)]
    public async Task ChannelSyncState_TracksBytesSent()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions { EnableReconnection = true, ReconnectBufferSize = 1024 * 1024 };
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel and send data
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
                return ch;
            return null;
        });

        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
        var readChannel = await acceptTask;

        Assert.NotNull(readChannel);

        // Send 1000 bytes
        var data = new byte[1000];
        Random.Shared.NextBytes(data);
        await writeChannel.WriteAsync(data, cts.Token);

        // Read and verify
        var received = new byte[1000];
        var totalRead = 0;
        while (totalRead < 1000)
        {
            var read = await readChannel!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        // Verify sync state tracked the bytes
        Assert.Equal(1000, writeChannel.SyncState.BytesSent);
        Assert.Equal(1000, readChannel!.SyncState.BytesReceived);
        Assert.Equal(data, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ChannelSyncState_BuffersUnacknowledgedData()
    {
        // Arrange
        var syncState = new NetConduit.Internal.ChannelSyncState(1024 * 1024);

        // Send some data
        var data1 = new byte[100];
        var data2 = new byte[200];
        Random.Shared.NextBytes(data1);
        Random.Shared.NextBytes(data2);

        syncState.RecordSend(data1);
        syncState.RecordSend(data2);

        // Verify bytes sent
        Assert.Equal(300, syncState.BytesSent);
        Assert.Equal(0, syncState.BytesAcked);

        // Get unacknowledged data from position 0
        var replay = syncState.GetUnacknowledgedDataFrom(0);
        Assert.Equal(300, replay.Length);
        Assert.Equal(data1.Concat(data2).ToArray(), replay);

        // Acknowledge first 100 bytes
        syncState.Acknowledge(100);
        Assert.Equal(100, syncState.BytesAcked);

        // Get unacknowledged data - should only be data2
        replay = syncState.GetUnacknowledgedDataFrom(100);
        Assert.Equal(200, replay.Length);
        Assert.Equal(data2, replay);
    }

    [Fact(Timeout = 120000)]
    public async Task ChannelSyncState_GetUnacknowledgedDataFrom_PartialSegment()
    {
        // Arrange
        var syncState = new NetConduit.Internal.ChannelSyncState(1024 * 1024);

        var data = new byte[100];
        for (int i = 0; i < 100; i++) data[i] = (byte)i;
        syncState.RecordSend(data);

        // Get data starting from position 50
        var replay = syncState.GetUnacknowledgedDataFrom(50);
        
        Assert.Equal(50, replay.Length);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal((byte)(i + 50), replay[i]);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ChannelSyncState_BufferEviction_WhenFull()
    {
        // Arrange - small buffer of 100 bytes
        var syncState = new NetConduit.Internal.ChannelSyncState(100);

        // Send 60 bytes
        var data1 = new byte[60];
        for (int i = 0; i < 60; i++) data1[i] = (byte)i;
        syncState.RecordSend(data1);

        // Send another 60 bytes - should evict first segment
        var data2 = new byte[60];
        for (int i = 0; i < 60; i++) data2[i] = (byte)(i + 100);
        syncState.RecordSend(data2);

        // BytesAcked should have been advanced due to eviction
        Assert.True(syncState.BytesAcked >= 60, "First segment should be evicted and acked");
        
        // Get unacknowledged should only return data2
        var replay = syncState.GetUnacknowledgedDataFrom(syncState.BytesAcked);
        Assert.True(replay.Length <= 60, "Should only have second segment");
    }

    [Fact(Timeout = 120000)]
    public async Task DataContinuity_LargeTransfer_NoCorruption()
    {
        // Test that large transfers maintain data integrity
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions 
        { 
            EnableReconnection = true, 
            ReconnectBufferSize = 1024 * 1024 // 1MB buffer
        };
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
                return ch;
            return null;
        });

        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "large-transfer" }, cts.Token);
        var readChannel = await acceptTask;
        Assert.NotNull(readChannel);

        // Transfer 10MB of data with pattern
        var totalSize = 10 * 1024 * 1024;
        var sendData = new byte[totalSize];
        for (int i = 0; i < totalSize; i++)
            sendData[i] = (byte)(i % 256);

        // Send in chunks
        var sendTask = Task.Run(async () =>
        {
            var remaining = sendData.AsMemory();
            while (remaining.Length > 0)
            {
                var chunkSize = Math.Min(64 * 1024, remaining.Length);
                await writeChannel.WriteAsync(remaining[..chunkSize], cts.Token);
                remaining = remaining[chunkSize..];
            }
        });

        // Receive
        var received = new byte[totalSize];
        var totalRead = 0;
        while (totalRead < totalSize)
        {
            var read = await readChannel!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        await sendTask;

        // Verify no corruption
        Assert.Equal(totalSize, totalRead);
        for (int i = 0; i < totalSize; i++)
        {
            Assert.Equal((byte)(i % 256), received[i]);
        }

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ReconnectionSync_ReplaysMissedData_NoCorruption()
    {
        // This test verifies that the reconnection sync mechanism correctly
        // replays missed data without corruption or gaps.
        
        // Simulate: Sender sent 1000 bytes, receiver only got 600 bytes before disconnect
        // After reconnect, the remaining 400 bytes should be replayed correctly
        
        var syncState = new NetConduit.Internal.ChannelSyncState(1024 * 1024);
        
        // Create test data with recognizable pattern
        var totalBytes = 1000;
        var allData = new byte[totalBytes];
        for (int i = 0; i < totalBytes; i++)
            allData[i] = (byte)(i % 256);
        
        // Simulate sending in chunks (as WriteChannel does)
        var chunkSize = 100;
        for (int offset = 0; offset < totalBytes; offset += chunkSize)
        {
            var chunk = new byte[Math.Min(chunkSize, totalBytes - offset)];
            Array.Copy(allData, offset, chunk, 0, chunk.Length);
            syncState.RecordSend(chunk);
        }
        
        Assert.Equal(totalBytes, syncState.BytesSent);
        
        // Simulate: remote received only first 600 bytes
        var receivedByRemote = 600;
        
        // Get replay data (what remote missed)
        var replayData = syncState.GetUnacknowledgedDataFrom(receivedByRemote);
        
        // Should be exactly 400 bytes
        Assert.Equal(400, replayData.Length);
        
        // Verify replay data matches the missing portion exactly
        for (int i = 0; i < replayData.Length; i++)
        {
            var expectedByte = (byte)((receivedByRemote + i) % 256);
            Assert.Equal(expectedByte, replayData[i]);
        }
        
        // Simulate: combine what remote had + replay = complete data
        var completeData = new byte[totalBytes];
        Array.Copy(allData, 0, completeData, 0, receivedByRemote); // What remote already had
        Array.Copy(replayData, 0, completeData, receivedByRemote, replayData.Length); // Replayed data
        
        // Verify complete data matches original
        Assert.Equal(allData, completeData);
    }

    [Fact(Timeout = 120000)]
    public async Task ReconnectionSync_MultipleChunks_PartialAck_CorrectReplay()
    {
        // Test replay when ack happens mid-chunk
        var syncState = new NetConduit.Internal.ChannelSyncState(1024 * 1024);
        
        // Send 3 chunks of 100 bytes each
        var chunk1 = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var chunk2 = Enumerable.Range(100, 100).Select(i => (byte)i).ToArray();
        var chunk3 = Enumerable.Range(200, 100).Select(i => (byte)(i % 256)).ToArray();
        
        syncState.RecordSend(chunk1);
        syncState.RecordSend(chunk2);
        syncState.RecordSend(chunk3);
        
        Assert.Equal(300, syncState.BytesSent);
        
        // Remote received 150 bytes (all of chunk1, half of chunk2)
        var replayData = syncState.GetUnacknowledgedDataFrom(150);
        
        // Should be 150 bytes (second half of chunk2 + all of chunk3)
        Assert.Equal(150, replayData.Length);
        
        // Verify first 50 bytes are second half of chunk2
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal((byte)(150 + i), replayData[i]);
        }
        
        // Verify next 100 bytes are chunk3
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal((byte)((200 + i) % 256), replayData[50 + i]);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task ReconnectionSync_ZeroBytesReceived_ReplaysAll()
    {
        // Edge case: remote received nothing (immediate disconnect)
        var syncState = new NetConduit.Internal.ChannelSyncState(1024 * 1024);
        
        var data = new byte[500];
        for (int i = 0; i < 500; i++) data[i] = (byte)(i % 256);
        syncState.RecordSend(data);
        
        var replayData = syncState.GetUnacknowledgedDataFrom(0);
        
        Assert.Equal(500, replayData.Length);
        Assert.Equal(data, replayData);
    }

    [Fact(Timeout = 120000)]
    public async Task ReconnectionSync_AllBytesReceived_ReplaysNothing()
    {
        // Edge case: remote received everything (no replay needed)
        var syncState = new NetConduit.Internal.ChannelSyncState(1024 * 1024);
        
        var data = new byte[500];
        syncState.RecordSend(data);
        
        var replayData = syncState.GetUnacknowledgedDataFrom(500);
        
        Assert.Empty(replayData);
    }

    [Fact(Timeout = 120000)]
    public async Task ReconnectionSync_LargeDataWithPattern_NoCorruption()
    {
        // Stress test: 5MB of data with complex pattern
        var syncState = new NetConduit.Internal.ChannelSyncState(10 * 1024 * 1024); // 10MB buffer
        
        var totalSize = 5 * 1024 * 1024; // 5MB
        var allData = new byte[totalSize];
        
        // Create pattern: each byte = (position * 7 + 13) % 256
        for (int i = 0; i < totalSize; i++)
            allData[i] = (byte)((i * 7 + 13) % 256);
        
        // Send in random-ish chunk sizes
        var random = new Random(42); // Fixed seed for reproducibility
        var offset = 0;
        while (offset < totalSize)
        {
            var chunkSize = Math.Min(random.Next(1000, 100000), totalSize - offset);
            var chunk = new byte[chunkSize];
            Array.Copy(allData, offset, chunk, 0, chunkSize);
            syncState.RecordSend(chunk);
            offset += chunkSize;
        }
        
        Assert.Equal(totalSize, syncState.BytesSent);
        
        // Simulate disconnect at random point
        var disconnectPoint = 2_500_000; // 2.5MB received
        var replayData = syncState.GetUnacknowledgedDataFrom(disconnectPoint);
        
        Assert.Equal(totalSize - disconnectPoint, replayData.Length);
        
        // Verify every byte of replay matches expected pattern
        for (int i = 0; i < replayData.Length; i++)
        {
            var position = disconnectPoint + i;
            var expected = (byte)((position * 7 + 13) % 256);
            Assert.Equal(expected, replayData[i]);
        }
    }

    [Fact(Timeout = 120000)]
    public async Task WriteChannel_SyncState_TracksCorrectly_ThroughMultiplexer()
    {
        // Integration test: verify SyncState is correctly updated through actual channel writes
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions 
        { 
            EnableReconnection = true, 
            ReconnectBufferSize = 1024 * 1024 
        };
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
                return ch;
            return null;
        });

        await using var writeChannel = await mux1.OpenChannelAsync(
            new ChannelOptions { ChannelId = "sync-test" }, cts.Token);
        var readChannel = await acceptTask;
        Assert.NotNull(readChannel);

        // Send specific amount of data
        var testData = new byte[10000];
        for (int i = 0; i < testData.Length; i++)
            testData[i] = (byte)(i % 256);

        await writeChannel.WriteAsync(testData, cts.Token);

        // Read all data
        var received = new byte[testData.Length];
        var totalRead = 0;
        while (totalRead < testData.Length)
        {
            var read = await readChannel!.ReadAsync(received.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }

        // Verify SyncState tracked correctly
        Assert.Equal(10000, writeChannel.SyncState.BytesSent);
        Assert.Equal(10000, readChannel!.SyncState.BytesReceived);
        
        // Verify data integrity
        Assert.Equal(testData, received);
        
        // Verify replay buffer contains the data
        var replayFromZero = writeChannel.SyncState.GetUnacknowledgedDataFrom(0);
        Assert.Equal(10000, replayFromZero.Length);
        Assert.Equal(testData, replayFromZero);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }
}
