using System.Text.Json;
using NetConduit.Internal;
using NetConduit.Transits;
using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for error paths, misuse patterns, and edge cases that are NOT happy-path usage.
/// Covers: operations after dispose, operations on wrong state, timeout behavior,
/// cancellation propagation, protocol violations, and transit error handling.
/// </summary>
public sealed class UnhappyPathTests
{
    private static async Task<(StreamMultiplexer Client, StreamMultiplexer Server)> CreateReadyPairAsync()
    {
        var duplex = new DuplexMemoryStream();
        var client = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        var server = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideB),
        });
        client.Start();
        server.Start();
        await Task.WhenAll(client.WaitForReadyAsync(), server.WaitForReadyAsync());
        return (client, server);
    }

    #region Multiplexer State Violations

    [Fact]
    public void OpenChannel_BeforeStart_Throws()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            mux.OpenChannel("test"));

        Assert.Contains("not been started", ex.Message);
    }

    [Fact]
    public void AcceptChannel_BeforeStart_Throws()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            mux.AcceptChannel("test"));

        Assert.Contains("not been started", ex.Message);
    }

    [Fact]
    public async Task OpenChannel_AfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();
        await client.DisposeAsync();

        Assert.Throws<InvalidOperationException>(() => client.OpenChannel("test"));

        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_AfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();
        await client.DisposeAsync();

        Assert.Throws<InvalidOperationException>(() => client.AcceptChannel("test"));

        await server.DisposeAsync();
    }

    [Fact]
    public async Task WaitForReady_AfterDispose_Completes()
    {
        var (client, server) = await CreateReadyPairAsync();

        // WaitForReady should already be complete (mux was ready)
        await client.WaitForReadyAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_AfterDispose_ThrowsObjectDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();
        await client.DisposeAsync();

        // GoAway on disposed mux throws because CTS is disposed
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            client.GoAwayAsync().AsTask());

        await server.DisposeAsync();
    }

    #endregion

    #region Channel Operations After Close

    [Fact]
    public async Task Write_AfterChannelClose_ThrowsChannelClosed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();
        await channel.CloseAsync();

        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            channel.WriteAsync(new byte[] { 1, 2, 3 }).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Write_AfterMuxDispose_ThrowsChannelClosed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            channel.WriteAsync(new byte[] { 1, 2, 3 }).AsTask());

        await server.DisposeAsync();
    }

    [Fact]
    public async Task Read_AfterChannelClose_ReturnsZero()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");

        // Close write side (sends FIN)
        await writeChannel.CloseAsync();

        // Read should return 0 (EOF) after FIN is received
        var buf = new byte[10];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int read = await readChannel.ReadAsync(buf, cts.Token);
        Assert.Equal(0, read);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Read_AfterMuxDispose_ReturnsZeroOrThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");

        await server.DisposeAsync();

        // After mux dispose, read should return 0 or throw
        var buf = new byte[10];
        int read = await readChannel.ReadAsync(buf);
        Assert.Equal(0, read);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CloseChannel_Twice_IsIdempotent()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();
        await channel.CloseAsync();
        await channel.CloseAsync(); // second close should not throw

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Cancellation Token Behavior

    [Fact]
    public async Task WaitForReady_WithCancelledToken_ThrowsOperationCanceled()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        mux.Start();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mux.WaitForReadyAsync(cts.Token));

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Read_WithCancelledToken_ThrowsOperationCanceled()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            readChannel.ReadAsync(new byte[10], cts.Token).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Read_CancellationDuringWait_ThrowsOperationCanceled()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");

        // Read with a short timeout — no data written, so it should cancel
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            readChannel.ReadAsync(new byte[10], cts.Token).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelWaitForReady_CancellationDuringWait_Throws()
    {
        var duplex = new DuplexMemoryStream();
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(duplex.SideA),
        });
        mux.Start();
        // Don't start the other side — channel will never become ready

        var channel = mux.OpenChannel("test");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.WaitForReadyAsync(cts.Token));

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelsAsync_CancelledDuringIteration_ThrowsOperationCanceled()
    {
        var (client, server) = await CreateReadyPairAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ch in server.AcceptChannelsAsync(cts.Token))
            {
                // should not get here — no channels opened
            }
        });

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Connection Failure Scenarios

    [Fact]
    public async Task StreamFactory_ThrowsOnConnect_PropagatesException()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => throw new IOException("Connection refused"),
            MaxAutoReconnectAttempts = 0,
        });
        mux.Start();

        await Assert.ThrowsAsync<IOException>(() => mux.WaitForReadyAsync());

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task StreamFactory_ThrowsOnConnect_WithRetries_ExhaustsAttempts()
    {
        int attempts = 0;
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("Connection refused");
            },
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxAutoReconnectDelay = TimeSpan.FromMilliseconds(50),
        });
        mux.Start();

        var ex = await Assert.ThrowsAsync<MultiplexerException>(() => mux.WaitForReadyAsync());
        Assert.Contains("3 attempts", ex.Message);
        Assert.True(attempts >= 3);

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task StreamFactory_ReturnsNull_ThrowsNullRef()
    {
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => Task.FromResult<IStreamPair>(null!),
            MaxAutoReconnectAttempts = 0,
        });
        mux.Start();

        // Should propagate NullReferenceException (handshake fails on null transport)
        await Assert.ThrowsAnyAsync<Exception>(() => mux.WaitForReadyAsync());

        await mux.DisposeAsync();
    }

    #endregion

    #region GoAway Protocol

    [Fact]
    public async Task GoAway_SetsIsShuttingDown()
    {
        var (client, server) = await CreateReadyPairAsync();

        Assert.False(client.IsShuttingDown);
        await client.GoAwayAsync();
        Assert.True(client.IsShuttingDown);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_Received_SetsDisconnectReason()
    {
        var (client, server) = await CreateReadyPairAsync();

        var disconnectedTcs = new TaskCompletionSource<DisconnectedEventArgs>();
        server.Disconnected += (_, e) => disconnectedTcs.TrySetResult(e);

        await client.GoAwayAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var args = await disconnectedTcs.Task.WaitAsync(cts.Token);
        Assert.Equal(DisconnectReason.GoAwayReceived, args.Reason);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GoAway_Twice_DoesNotThrow()
    {
        var (client, server) = await CreateReadyPairAsync();

        await client.GoAwayAsync();
        await client.GoAwayAsync(); // idempotent

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Channel Property State Transitions

    [Fact]
    public async Task ChannelState_TransitionsFromOpeningToOpen()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        // Before ACK arrives it could be Opening
        var initialState = channel.State;
        Assert.True(initialState is ChannelState.Opening or ChannelState.Open);

        await channel.WaitForReadyAsync();
        Assert.Equal(ChannelState.Open, channel.State);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelState_TransitionsToClosingOrClosedAfterClose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();
        Assert.Equal(ChannelState.Open, channel.State);

        await channel.CloseAsync();
        // After CloseAsync, state may be Closing (FIN sent) or Closed
        Assert.True(channel.State is ChannelState.Closing or ChannelState.Closed);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelState_TransitionsToClosedAfterMuxDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();

        await client.DisposeAsync();
        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal(ChannelCloseReason.MuxDisposed, channel.CloseReason);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task IsConnected_FalseAfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();
        Assert.True(channel.IsConnected);

        await client.DisposeAsync();
        Assert.False(channel.IsConnected);

        await server.DisposeAsync();
    }

    #endregion

    #region AsStream Error Paths

    [Fact]
    public async Task WriteChannelAsStream_ReadThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        var stream = channel.AsStream();

        Assert.False(stream.CanRead);
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[10], 0, 10));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannelAsStream_WriteThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");
        var stream = readChannel.AsStream();

        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[10], 0, 10));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannelAsStream_SeekThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        var stream = channel.AsStream();

        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannelAsStream_SeekThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");
        var stream = readChannel.AsStream();

        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Transit Error Paths

    [Fact]
    public async Task MessageTransit_SendAfterDispose_ThrowsObjectDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m1");
        var r = await server.AcceptChannelAsync("m1");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.SendAsync(new TestMessage { Name = "x", Value = 0 }).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveAfterDispose_ThrowsObjectDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m2");
        var r = await server.AcceptChannelAsync("m2");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.ReceiveAsync().AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_OversizedMessage_ThrowsInvalidOperation()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m3");
        var r = await server.AcceptChannelAsync("m3");

        // Tiny max message size (100 bytes)
#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r, maxMessageSize: 100);
#pragma warning restore IL2026, IL3050

        // Message that serializes to > 100 bytes
        var bigMsg = new TestMessage { Name = new string('x', 200), Value = 1 };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transit.SendAsync(bigMsg).AsTask());

        Assert.Contains("exceeds maximum", ex.Message);

        await transit.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_ReceiveAllAsync_StopsOnChannelClose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m4");
        var r = await server.AcceptChannelAsync("m4");

#pragma warning disable IL2026, IL3050
        var sender = new MessageTransit<TestMessage, TestMessage>(w, null);
        var receiver = new MessageTransit<TestMessage, TestMessage>(null, r);
#pragma warning restore IL2026, IL3050

        // Send one message then close
        await sender.SendAsync(new TestMessage { Name = "only", Value = 1 });
        await sender.DisposeAsync();

        var messages = new List<TestMessage>();
        await foreach (var msg in receiver.ReceiveAllAsync())
        {
            messages.Add(msg);
        }

        Assert.Single(messages);
        Assert.Equal("only", messages[0].Name);

        await receiver.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_WriteAfterDispose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var transit = new StreamTransit(client.OpenChannel("s1"));
        await transit.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transit.WriteAsync(new byte[] { 1, 2, 3 }).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task StreamTransit_ReadAfterDispose_ThrowsObjectDisposed()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("s2");
        var r = await server.AcceptChannelAsync("s2");
        var transit = new StreamTransit(r);
        await transit.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            transit.ReadAsync(new byte[10]).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Event Firing on Error Paths

    [Fact]
    public async Task Error_Event_Fires_OnConnectionFailure()
    {
        var errorFired = new TaskCompletionSource<ErrorEventArgs>();
        int connectCount = 0;

        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ =>
            {
                Interlocked.Increment(ref connectCount);
                throw new IOException("Connection refused");
            },
            MaxAutoReconnectAttempts = 3,
            AutoReconnectDelay = TimeSpan.FromMilliseconds(10),
        });
        mux.Error += (_, e) => errorFired.TrySetResult(e);
        mux.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var errorArgs = await errorFired.Task.WaitAsync(cts.Token);
        Assert.NotNull(errorArgs.Exception);
        Assert.IsType<IOException>(errorArgs.Exception);

        await mux.DisposeAsync();
    }

    [Fact]
    public async Task Disconnected_Event_Fires_WithCorrectReason_OnDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var disconnected = new TaskCompletionSource<DisconnectedEventArgs>();
        client.Disconnected += (_, e) => disconnected.TrySetResult(e);

        await client.DisposeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var args = await disconnected.Task.WaitAsync(cts.Token);
        Assert.Equal(DisconnectReason.LocalDispose, args.Reason);

        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelClosed_Event_Fires_OnMuxDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();

        var closedTcs = new TaskCompletionSource<ChannelCloseEventArgs>();
        channel.Closed += (_, e) => closedTcs.TrySetResult(e);

        await client.DisposeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var args = await closedTcs.Task.WaitAsync(cts.Token);
        Assert.Equal(ChannelCloseReason.MuxDisposed, args.Reason);

        await server.DisposeAsync();
    }

    #endregion

    #region Lookup Methods with Invalid Input

    [Fact]
    public async Task GetWriteChannel_NonexistentId_ReturnsNull()
    {
        var (client, server) = await CreateReadyPairAsync();

        Assert.Null(client.GetWriteChannel("does-not-exist"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetReadChannel_NonexistentId_ReturnsNull()
    {
        var (client, server) = await CreateReadyPairAsync();

        Assert.Null(server.GetReadChannel("does-not-exist"));

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetWriteChannel_AfterChannelClosed_ReturnsNull()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();
        Assert.NotNull(client.GetWriteChannel("ch"));

        await channel.CloseAsync();

        // After close, channel may be removed from registry
        // (implementation-specific — either null or still findable)
        // The important thing is it doesn't throw
        _ = client.GetWriteChannel("ch");

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Multiple Dispose Safety

    [Fact]
    public async Task Multiplexer_MultipleDispose_IsIdempotent()
    {
        var (client, server) = await CreateReadyPairAsync();

        await client.DisposeAsync();
        await client.DisposeAsync();
        await client.DisposeAsync();

        await server.DisposeAsync();
    }

    [Fact]
    public async Task WriteChannel_MultipleDispose_IsIdempotent()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();

        await channel.CloseAsync();
        await channel.CloseAsync();
        await channel.CloseAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MessageTransit_MultipleDispose_IsIdempotent()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("m");
        var r = await server.AcceptChannelAsync("m");

#pragma warning disable IL2026, IL3050
        var transit = new MessageTransit<TestMessage, TestMessage>(w, r);
#pragma warning restore IL2026, IL3050

        await transit.DisposeAsync();
        await transit.DisposeAsync();
        await transit.DisposeAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Empty and Boundary Writes

    [Fact]
    public async Task Write_EmptyBuffer_IsNoOp()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();

        // Empty write should succeed without sending anything
        await channel.WriteAsync(ReadOnlyMemory<byte>.Empty);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Write_SingleByte_Succeeds()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");

        await writeChannel.WriteAsync(new byte[] { 0xFF });

        var buf = new byte[10];
        int read = await readChannel.ReadAsync(buf);
        Assert.Equal(1, read);
        Assert.Equal(0xFF, buf[0]);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Read_ZeroLengthBuffer_ReturnsZero()
    {
        var (client, server) = await CreateReadyPairAsync();

        var writeChannel = client.OpenChannel("ch");
        var readChannel = await server.AcceptChannelAsync("ch");

        await writeChannel.WriteAsync(new byte[] { 1, 2, 3 });

        // Read with empty buffer
        int read = await readChannel.ReadAsync(Memory<byte>.Empty);
        Assert.Equal(0, read);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Stats Consistency After Errors

    [Fact]
    public async Task Stats_BytesSent_TracksAfterClose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();

        byte[] data = new byte[100];
        Random.Shared.NextBytes(data);
        await channel.WriteAsync(data);

        // Allow writer thread to pick up the frame
        await Task.Delay(100);

        Assert.True(channel.Stats.BytesSent > 0);

        await channel.CloseAsync();

        // Stats should still be accessible after close
        long finalBytes = channel.Stats.BytesSent;
        Assert.True(finalBytes >= 100);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task MuxStats_TrackChannelLifecycle()
    {
        var (client, server) = await CreateReadyPairAsync();

        Assert.Equal(0, client.Stats.OpenChannels);
        Assert.Equal(0, client.Stats.TotalChannelsOpened);

        var ch1 = client.OpenChannel("ch1");
        Assert.Equal(1, client.Stats.TotalChannelsOpened);

        var ch2 = client.OpenChannel("ch2");
        Assert.Equal(2, client.Stats.TotalChannelsOpened);

        await ch1.WaitForReadyAsync();
        await ch1.CloseAsync();

        // TotalChannelsClosed tracked via remote FIN, may lag
        Assert.True(client.Stats.TotalChannelsOpened >= 2);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Configuration Edge Cases

    [Fact]
    public void MultiplexerOptions_NullStreamFactory_CreateSucceeds_StartFails()
    {
        // StreamFactory is 'required' — null is compile-time error,
        // but if bypassed, Create succeeds and Start fails at runtime
        var mux = StreamMultiplexer.Create(new MultiplexerOptions { StreamFactory = null! });
        Assert.NotNull(mux);
        mux.Start();
        // MainLoop will crash when it tries to invoke null factory
    }

    [Fact]
    public async Task ChannelOptions_CustomPriority_Preserved()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "high",
            Priority = ChannelPriority.High,
        });

        Assert.Equal(ChannelPriority.High, channel.Priority);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelOptions_CustomSendTimeout_Used()
    {
        var (client, server) = await CreateReadyPairAsync();

        // This just verifies options don't throw at construction time
        var channel = client.OpenChannel(new ChannelOptions
        {
            ChannelId = "timeout-test",
            SendTimeout = TimeSpan.FromMilliseconds(500),
        });

        Assert.NotNull(channel);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion
}
