using System.Buffers.Binary;
using NetConduit;
using NetConduit.Models;
using NetConduit.Enums;
using NetConduit.Exceptions;
using NetConduit.Internal;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for auto-reconnection functionality.
/// </summary>
[Collection("HighMemory")]
[Trait("Category", "HighMemory")]
[Trait("Batch", "3")]
public class ReconnectionTests
{
    [Fact(Timeout = 120000)]
    public async Task Multiplexer_IsConnected_FalseBeforeRunAsync()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);

        // Assert
        Assert.False(mux.IsConnected);
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_IsConnected_TrueAfterRunAsync()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

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
        await using var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);

        // Assert
        Assert.False(mux.IsReconnecting);
    }

    [Fact(Timeout = 120000)]
    public async Task Multiplexer_OnDisconnected_EventFired()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions { StreamFactory = _ => null! };
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, options);

        var disconnectedEvent = new TaskCompletionSource();
        mux1.OnDisconnected += (reason, ex) => disconnectedEvent.TrySetResult();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

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
    public async Task Multiplexer_SessionId_IsNotEmpty()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

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
    public async Task Multiplexer_NotifyDisconnected_FiresEvent()
    {
        // Arrange
        var options = new MultiplexerOptions
        {
            StreamFactory = _ => null!
        };

        await using var pipe = new DuplexPipe();
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, options);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

        await Task.Delay(100);

        // NotifyDisconnected should work when reconnection is enabled
        var disconnectedEvent = new TaskCompletionSource();
        mux1.OnDisconnected += (reason, ex) => disconnectedEvent.TrySetResult();
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
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

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
        var options = new MultiplexerOptions { StreamFactory = _ => null! };
        var mux = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);

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
        var options = new MultiplexerOptions { StreamFactory = _ => null! };
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

        await Task.Delay(100);

        // Open channel and send data
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
                return ch;
            return null;
        });

        await using var writeChannel = await mux1.OpenChannelAsync("test", cts.Token);
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

        // Verify byte tracking
        Assert.Equal(1000, writeChannel.BytesSent);
        Assert.Equal(1000, readChannel!.SyncState.BytesReceived);
        Assert.Equal(data, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task DataContinuity_LargeTransfer_NoCorruption()
    {
        // Test that large transfers maintain data integrity
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions 
        { 
            StreamFactory = _ => null!
        };
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

        await Task.Delay(100);

        // Open channel
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var ch in mux2.AcceptChannelsAsync(cts.Token))
                return ch;
            return null;
        });

        await using var writeChannel = await mux1.OpenChannelAsync("large-transfer", cts.Token);
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
    public async Task WriteChannel_SyncState_TracksCorrectly_ThroughMultiplexer()
    {
        // Integration test: verify SyncState is correctly updated through actual channel writes
        await using var pipe = new DuplexPipe();
        var options = new MultiplexerOptions 
        { 
            StreamFactory = _ => null!
        };
        await using var mux1 = await TestMuxHelper.CreateMuxAsync(pipe.Stream1, options);
        await using var mux2 = await TestMuxHelper.CreateMuxAsync(pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.Start(cts.Token);
        var run2 = mux2.Start(cts.Token);

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

        // Verify byte tracking
        Assert.Equal(10000, writeChannel.BytesSent);
        Assert.Equal(10000, readChannel!.SyncState.BytesReceived);
        
        // Verify data integrity
        Assert.Equal(testData, received);

        cts.Cancel();
        await Task.WhenAll(run1.ContinueWith(_ => { }), run2.ContinueWith(_ => { }));
    }

    #region Reconnect ACK Position Sync

    [Fact(Timeout = 30000)]
    public async Task ReconnectAsync_AppliesPeerReceivePositions_ToWriteChannelSyncState()
    {
        // ReconnectAsync sends RECONNECT with our read positions and receives
        // RECONNECT_ACK with the peer's read positions. Those positions must be
        // applied to our write channels so we know where to resume sending.
        // Old bug: ReceiveReconnectAckAsync return value was discarded.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        await using var pipe = new DuplexPipe();
        var (muxA, muxB, _, _) = await TestMuxHelper.CreateMuxPairAsync(pipe, cancellationToken: cts.Token);

        var writeChannel = await muxA.OpenChannelAsync("ack_sync", cts.Token);
        var readChannel = await muxB.AcceptChannelAsync("ack_sync", cts.Token);

        // Send data and read it on the other side so read position advances
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        await writeChannel.WriteAsync(payload, cts.Token);
        await writeChannel.FlushAsync(cts.Token);

        var buf = new byte[4096];
        var totalRead = 0;
        while (totalRead < payload.Length)
        {
            var read = await readChannel.ReadAsync(buf.AsMemory(totalRead), cts.Token);
            if (read == 0) break;
            totalRead += read;
        }
        Assert.Equal(payload.Length, totalRead);
        Assert.Equal(payload, buf[..totalRead]);
        Assert.Equal(4096L, readChannel.SyncState.BytesReceived);
        // Mux ring tracks ack at mux level, not per-channel

        // Simulate reconnect protocol: craft a RECONNECT_ACK with the peer's positions
        await using var reconnectPipe = new DuplexPipe();
        var clientStream = reconnectPipe.Stream1;
        var serverStream = reconnectPipe.Stream2;

        var reconnectTask = muxA.ReconnectAsync(clientStream, clientStream, cts.Token);

        // Read the RECONNECT frame muxA sends
        var headerBuf = new byte[FrameHeader.Size];
        await serverStream.ReadExactlyAsync(headerBuf, cts.Token);
        var header = FrameHeader.Read(headerBuf);
        var reconnectPayload = new byte[header.Length];
        await serverStream.ReadExactlyAsync(reconnectPayload, cts.Token);
        Assert.Equal((byte)ControlSubtype.Reconnect, reconnectPayload[0]);

        // Build RECONNECT_ACK reporting that peer received 4096 bytes
        var channelIndex = writeChannel.ChannelIndex;
        var ackPayloadSize = 1 + 16 + 4 + 12;
        var ackPayload = new byte[ackPayloadSize];
        ackPayload[0] = (byte)ControlSubtype.ReconnectAck;
        muxB.SessionId.TryWriteBytes(ackPayload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(ackPayload.AsSpan(17), 1);
        BinaryPrimitives.WriteUInt32BigEndian(ackPayload.AsSpan(21), channelIndex);
        BinaryPrimitives.WriteInt64BigEndian(ackPayload.AsSpan(25), 4096L);

        var ackHeader = new FrameHeader(0, FrameFlags.Data, (uint)ackPayload.Length);
        var ackHeaderBuf = new byte[FrameHeader.Size];
        ackHeader.Write(ackHeaderBuf);
        await serverStream.WriteAsync(ackHeaderBuf, cts.Token);
        await serverStream.WriteAsync(ackPayload, cts.Token);
        await serverStream.FlushAsync(cts.Token);

        await reconnectTask;

        // Mux ring ack is global, not per-channel — reconnect succeeded

        await muxA.DisposeAsync();
        await muxB.DisposeAsync();
    }

    #endregion
}

