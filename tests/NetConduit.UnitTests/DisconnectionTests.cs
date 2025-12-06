namespace NetConduit.UnitTests;

/// <summary>
/// Tests for disconnection behavior, events, and graceful shutdown.
/// </summary>
public class DisconnectionTests
{
    #region Mux Disconnection Tests

    [Fact(Timeout = 120000)]
    public async Task LocalMuxDispose_SendsGoAway_FiresOnDisconnected()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var disconnectEvent = new TaskCompletionSource<DisconnectReason>();
        mux1.OnDisconnected += (reason, ex) => disconnectEvent.TrySetResult(reason);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Act
        await mux1.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(disconnectEvent.Task, Task.Delay(3000));
        Assert.Equal(disconnectEvent.Task, completedTask);
        Assert.Equal(DisconnectReason.LocalDispose, await disconnectEvent.Task);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task LocalMuxDispose_AbortsAllChannels_WithMuxDisposedReason()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channels
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        var channelCloseEvent = new TaskCompletionSource<ChannelCloseReason>();
        writeChannel.OnClosed += (reason, ex) => channelCloseEvent.TrySetResult(reason);

        // Act
        await mux1.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(channelCloseEvent.Task, Task.Delay(3000));
        Assert.Equal(channelCloseEvent.Task, completedTask);
        Assert.Equal(ChannelCloseReason.MuxDisposed, await channelCloseEvent.Task);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task LocalMuxDispose_WaitsForGracefulShutdown_ThenAborts()
    {
        // Arrange - use short timeout for test
        var options = new MultiplexerOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(500) };
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open a channel
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Act - time the dispose
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await mux1.DisposeAsync();
        stopwatch.Stop();

        // Assert - dispose should complete (either graceful or after timeout)
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Dispose took too long: {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task LocalMuxDispose_SetsDisconnectReasonProperty()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Act
        await mux1.DisposeAsync();

        // Assert
        Assert.Equal(DisconnectReason.LocalDispose, mux1.DisconnectReason);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task RemoteMuxDispose_ReceivesGoAway_FiresOnDisconnected()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var disconnectEvent = new TaskCompletionSource<DisconnectReason>();
        mux2.OnDisconnected += (reason, ex) => disconnectEvent.TrySetResult(reason);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Act - dispose mux1, mux2 should receive GoAway
        await mux1.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(disconnectEvent.Task, Task.Delay(3000));
        Assert.Equal(disconnectEvent.Task, completedTask);
        // Could be GoAwayReceived or TransportError depending on timing
        var reason = await disconnectEvent.Task;
        Assert.True(
            reason == DisconnectReason.GoAwayReceived ||
            reason == DisconnectReason.TransportError);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task RemoteMuxDispose_SetsDisconnectReasonProperty()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var disconnectEvent = new TaskCompletionSource();
        mux2.OnDisconnected += (reason, ex) => disconnectEvent.TrySetResult();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Act - dispose mux1
        await mux1.DisposeAsync();

        // Assert
        await Task.WhenAny(disconnectEvent.Task, Task.Delay(3000));
        Assert.NotNull(mux2.DisconnectReason);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task RemoteMuxDispose_AbortsAllChannels_WithMuxDisposedReason()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel from mux2
        await using var writeChannel = await mux2.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        var channelCloseEvent = new TaskCompletionSource<ChannelCloseReason>();
        writeChannel.OnClosed += (reason, ex) => channelCloseEvent.TrySetResult(reason);

        // Act - dispose mux1 (remote side)
        await mux1.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(channelCloseEvent.Task, Task.Delay(3000));
        Assert.Equal(channelCloseEvent.Task, completedTask);
        // Should be MuxDisposed or TransportFailed
        var closeReason = await channelCloseEvent.Task;
        Assert.True(
            closeReason == ChannelCloseReason.MuxDisposed ||
            closeReason == ChannelCloseReason.TransportFailed);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task TransportError_FiresOnDisconnected_WithException()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var disconnectEvent = new TaskCompletionSource<(DisconnectReason reason, Exception? ex)>();
        mux1.OnDisconnected += (reason, ex) => disconnectEvent.TrySetResult((reason, ex));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Act - forcibly close the underlying pipe to simulate transport error
        await pipe.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(disconnectEvent.Task, Task.Delay(3000));
        Assert.Equal(disconnectEvent.Task, completedTask);
        Assert.Equal(DisconnectReason.TransportError, await disconnectEvent.Task.ContinueWith(t => t.Result.reason));
        // Exception may or may not be present depending on how stream closes

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task TransportError_SetsDisconnectReasonProperty()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        var disconnectEvent = new TaskCompletionSource();
        mux1.OnDisconnected += (reason, ex) => disconnectEvent.TrySetResult();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Act - forcibly close the underlying pipe
        await pipe.DisposeAsync();

        // Assert
        await Task.WhenAny(disconnectEvent.Task, Task.Delay(3000));
        Assert.Equal(DisconnectReason.TransportError, mux1.DisconnectReason);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task TransportError_AbortsAllChannels_WithTransportFailedReason()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        var channelCloseEvent = new TaskCompletionSource<ChannelCloseReason>();
        writeChannel.OnClosed += (reason, ex) => channelCloseEvent.TrySetResult(reason);

        // Act - forcibly close the underlying pipe
        await pipe.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(channelCloseEvent.Task, Task.Delay(3000));
        Assert.Equal(channelCloseEvent.Task, completedTask);
        Assert.Equal(ChannelCloseReason.TransportFailed, await channelCloseEvent.Task);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Channel Disconnection Tests

    [Fact(Timeout = 120000)]
    public async Task WriteChannelDispose_FiresOnClosed()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        var closeEvent = new TaskCompletionSource<ChannelCloseReason>();
        writeChannel.OnClosed += (reason, ex) => closeEvent.TrySetResult(reason);

        // Act
        await writeChannel.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(closeEvent.Task, Task.Delay(3000));
        Assert.Equal(closeEvent.Task, completedTask);
        Assert.Equal(ChannelCloseReason.LocalClose, await closeEvent.Task);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task WriteChannelDispose_AfterMuxDisposed_CompletesImmediately()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Dispose mux first
        await mux1.DisposeAsync();

        // Act - dispose channel (should complete immediately since mux is disposed)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await writeChannel.DisposeAsync();
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"Channel dispose took too long: {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task WriteChannel_WriteAfterClose_ThrowsChannelClosedException()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);
        await writeChannel.DisposeAsync();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await writeChannel.WriteAsync(new byte[10], cts.Token));

        Assert.Equal("test", ex.ChannelId);
        Assert.Equal(ChannelCloseReason.LocalClose, ex.CloseReason);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task WriteChannel_WriteAfterMuxDisposed_ThrowsChannelClosedException()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Dispose mux
        await mux1.DisposeAsync();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await writeChannel.WriteAsync(new byte[10], cts.Token));

        Assert.Equal("test", ex.ChannelId);
        Assert.Equal(ChannelCloseReason.MuxDisposed, ex.CloseReason);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task WriteChannel_CloseReason_SetCorrectly()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Initially null
        Assert.Null(writeChannel.CloseReason);

        // Close the channel
        await writeChannel.DisposeAsync();

        // Assert
        Assert.Equal(ChannelCloseReason.LocalClose, writeChannel.CloseReason);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ReadChannelDispose_FiresOnClosed()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel from mux1
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Accept on mux2
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var channel in mux2.AcceptChannelsAsync(cts.Token))
            {
                return channel;
            }
            return null;
        });

        var readChannel = await acceptTask.WaitAsync(cts.Token);
        Assert.NotNull(readChannel);

        var closeEvent = new TaskCompletionSource<ChannelCloseReason>();
        readChannel!.OnClosed += (reason, ex) => closeEvent.TrySetResult(reason);

        // Act
        await readChannel.DisposeAsync();

        // Assert
        var completedTask = await Task.WhenAny(closeEvent.Task, Task.Delay(3000));
        Assert.Equal(closeEvent.Task, completedTask);
        Assert.Equal(ChannelCloseReason.LocalClose, await closeEvent.Task);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ReadChannelDispose_AfterMuxDisposed_CompletesImmediately()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel from mux1
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Accept on mux2
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var channel in mux2.AcceptChannelsAsync(cts.Token))
            {
                return channel;
            }
            return null;
        });

        var readChannel = await acceptTask.WaitAsync(cts.Token);
        Assert.NotNull(readChannel);

        // Dispose mux first
        await mux2.DisposeAsync();

        // Act - dispose channel (should complete immediately since mux is disposed)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await readChannel!.DisposeAsync();
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"Channel dispose took too long: {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ReadChannel_CloseReason_SetCorrectly()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel from mux1
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Accept on mux2
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var channel in mux2.AcceptChannelsAsync(cts.Token))
            {
                return channel;
            }
            return null;
        });

        var readChannel = await acceptTask.WaitAsync(cts.Token);
        Assert.NotNull(readChannel);

        // Initially null
        Assert.Null(readChannel!.CloseReason);

        // Close the channel
        await readChannel.DisposeAsync();

        // Assert
        Assert.Equal(ChannelCloseReason.LocalClose, readChannel.CloseReason);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Timeout Tests

    [Fact(Timeout = 120000)]
    public async Task MuxDispose_TimeoutExpires_AbortsForcefully()
    {
        // Arrange - use very short timeout
        var options = new MultiplexerOptions { GracefulShutdownTimeout = TimeSpan.FromMilliseconds(100) };
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Act - dispose should complete within reasonable time even with open channels
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await mux1.DisposeAsync();
        stopwatch.Stop();

        // Assert - should complete in about timeout + buffer time
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Dispose took too long: {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task CustomGracefulShutdownTimeout_IsRespected()
    {
        // Arrange - use custom timeout
        var timeout = TimeSpan.FromMilliseconds(200);
        var options = new MultiplexerOptions { GracefulShutdownTimeout = timeout };
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1, options);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2, options);

        // Assert options is set correctly
        Assert.Equal(timeout, mux1.Options.GracefulShutdownTimeout);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await mux1.DisposeAsync();
        stopwatch.Stop();

        // Assert - should respect timeout
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Dispose took too long: {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Event Ordering Tests

    [Fact(Timeout = 120000)]
    public async Task MuxDispose_ChannelOnClosedFiresBeforeMuxOnDisconnected()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        var events = new List<string>();
        writeChannel.OnClosed += (reason, ex) => events.Add("ChannelClosed");
        mux1.OnDisconnected += (reason, ex) => events.Add("MuxDisconnected");

        // Act
        await mux1.DisposeAsync();

        // Wait a bit for events to fire
        await Task.Delay(100);

        // Assert - channel should close before mux disconnects
        Assert.Equal(2, events.Count);
        Assert.Equal("ChannelClosed", events[0]);
        Assert.Equal("MuxDisconnected", events[1]);

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task TransportError_ChannelOnClosedFiresBeforeMuxOnDisconnected()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        var events = new List<string>();
        var eventLock = new object();
        writeChannel.OnClosed += (reason, ex) => { lock (eventLock) events.Add("ChannelClosed"); };
        mux1.OnDisconnected += (reason, ex) => { lock (eventLock) events.Add("MuxDisconnected"); };

        // Act - force transport error
        await pipe.DisposeAsync();

        // Wait for events
        await Task.Delay(500);

        // Assert - channel should close before mux disconnects
        lock (eventLock)
        {
            Assert.Equal(2, events.Count);
            Assert.Equal("ChannelClosed", events[0]);
            Assert.Equal("MuxDisconnected", events[1]);
        }

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Silent Disposal Tests

    [Fact(Timeout = 120000)]
    public async Task MuxDispose_SwallowsAllExceptions()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Dispose underlying pipe first to cause potential exceptions
        await pipe.DisposeAsync();

        await Task.Delay(50);

        // Act & Assert - should not throw
        await mux1.DisposeAsync();
        await mux2.DisposeAsync();

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task WriteChannelDispose_SwallowsAllExceptions()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Dispose mux to put channel in bad state
        await mux1.DisposeAsync();

        // Act & Assert - channel dispose should not throw
        await writeChannel.DisposeAsync();

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ReadChannelDispose_SwallowsAllExceptions()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open channel from mux1
        await using var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Accept on mux2
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var channel in mux2.AcceptChannelsAsync(cts.Token))
            {
                return channel;
            }
            return null;
        });

        var readChannel = await acceptTask.WaitAsync(cts.Token);
        Assert.NotNull(readChannel);

        // Dispose mux to put channel in bad state
        await mux2.DisposeAsync();

        // Act & Assert - channel dispose should not throw
        await readChannel!.DisposeAsync();

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task MuxDispose_CalledTwice_CompletesImmediately()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Act
        await mux1.DisposeAsync();

        // Second dispose should complete immediately
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await mux1.DisposeAsync();
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Second dispose took too long: {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task ChannelDispose_CalledTwice_CompletesImmediately()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        var writeChannel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = "test" }, cts.Token);

        // Act
        await writeChannel.DisposeAsync();

        // Second dispose should complete immediately
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await writeChannel.DisposeAsync();
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Second dispose took too long: {stopwatch.ElapsedMilliseconds}ms");

        // Cleanup
        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    #endregion

    #region Additional Tests

    [Fact(Timeout = 120000)]
    public async Task MultipleChannels_AllAbortedOnMuxDispose()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux1 = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);
        await using var mux2 = new StreamMultiplexer(pipe.Stream2, pipe.Stream2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run1 = mux1.RunAsync(cts.Token);
        var run2 = mux2.RunAsync(cts.Token);

        await Task.Delay(100);

        // Open multiple channels
        var channels = new List<WriteChannel>();
        var closeEvents = new List<TaskCompletionSource<ChannelCloseReason>>();

        for (int i = 0; i < 5; i++)
        {
            var channel = await mux1.OpenChannelAsync(new ChannelOptions { ChannelId = $"test{i}" }, cts.Token);
            channels.Add(channel);

            var tcs = new TaskCompletionSource<ChannelCloseReason>();
            channel.OnClosed += (reason, ex) => tcs.TrySetResult(reason);
            closeEvents.Add(tcs);
        }

        // Act
        await mux1.DisposeAsync();

        // Assert - all channels should receive close event
        var results = await Task.WhenAll(closeEvents.Select(e =>
            Task.WhenAny(e.Task, Task.Delay(3000).ContinueWith(_ => ChannelCloseReason.LocalClose))));

        foreach (var result in closeEvents)
        {
            Assert.True(result.Task.IsCompleted);
            Assert.Equal(ChannelCloseReason.MuxDisposed, await result.Task);
        }

        // Cleanup
        foreach (var ch in channels)
            await ch.DisposeAsync();

        cts.Cancel();
        await Task.WhenAll(
            run1.ContinueWith(_ => { }),
            run2.ContinueWith(_ => { }));
    }

    [Fact(Timeout = 120000)]
    public async Task DisposeBeforeRunAsync_DoesNotThrow()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        var mux = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);

        // Act & Assert - dispose before RunAsync should not throw
        await mux.DisposeAsync();
    }

    [Fact(Timeout = 120000)]
    public async Task DisconnectReason_NullBeforeDispose()
    {
        // Arrange
        await using var pipe = new DuplexPipe();
        await using var mux = new StreamMultiplexer(pipe.Stream1, pipe.Stream1);

        // Assert
        Assert.Null(mux.DisconnectReason);
    }

    #endregion
}
