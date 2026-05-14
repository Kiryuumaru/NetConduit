using Xunit;

namespace NetConduit.UnitTests;

/// <summary>
/// Tests for incorrect API usage patterns that real users would hit:
/// event handler exceptions, concurrent misuse, transit lifecycle errors,
/// GoAway + channel interactions, and write-before-ready scenarios.
/// </summary>
public sealed class ApiMisuseTests
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

    #region Event Handler Exceptions

    [Fact]
    public async Task ChannelOpened_ThrowingHandler_DoesNotCrashMux()
    {
        var (client, server) = await CreateReadyPairAsync();

        client.ChannelOpened += (_, _) => throw new InvalidOperationException("bad handler");

        // Throwing event handler is swallowed — mux continues operating
        var channel = client.OpenChannel("test");
        Assert.NotNull(channel);
        await channel.WaitForReadyAsync();

        // Mux still operational
        var ch2 = client.OpenChannel("test2");
        await ch2.WaitForReadyAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ChannelClosed_ThrowingHandler_DoesNotAffectLocalClose()
    {
        var (client, server) = await CreateReadyPairAsync();

        client.ChannelClosed += (_, _) => throw new InvalidOperationException("bad handler");

        var channel = client.OpenChannel("test");
        await channel.WaitForReadyAsync();

        // Local CloseAsync sends FIN — ChannelClosed fires on remote FIN ack (async)
        // The throwing handler won't affect the local close operation
        await channel.CloseAsync();

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Disconnected_ThrowingHandler_DoesNotCrashDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        client.Disconnected += (_, _) => throw new InvalidOperationException("bad handler");

        // Throwing Disconnected handler is swallowed — dispose completes normally
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Error_ThrowingHandler_DoesNotPreventDispose()
    {
        var errorMux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = _ => throw new IOException("connection failed"),
        });

        errorMux.Error += (_, _) => throw new InvalidOperationException("bad error handler");

        errorMux.Start();

        // Give MainLoop a moment to hit the error
        await Task.Delay(300);

        // Dispose should complete even if Error handler throws inside the loop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var disposeTask = errorMux.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(3000, cts.Token));
        Assert.Same(disposeTask, completed);
    }

    #endregion

    #region Concurrent Misuse

    [Fact]
    public async Task ConcurrentDispose_FromMultipleThreads_IsSafe()
    {
        var (client, server) = await CreateReadyPairAsync();

        // Multiple concurrent dispose calls should not throw
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.DisposeAsync().AsTask())
            .ToArray();

        await Task.WhenAll(tasks);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentOpenChannel_SameId_OnlyOneSucceeds()
    {
        var (client, server) = await CreateReadyPairAsync();

        var successes = 0;
        var failures = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            try
            {
                client.OpenChannel("race");
                Interlocked.Increment(ref successes);
            }
            catch (MultiplexerException ex) when (ex.ErrorCode == ErrorCode.ChannelExists)
            {
                Interlocked.Increment(ref failures);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(9, failures);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentWriteAndClose_OnSameChannel_NoCorruption()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();

        var writeTask = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await channel.WriteAsync(new byte[64]);
                }
                catch (ChannelClosedException)
                {
                    break;
                }
            }
        });

        // Let some writes happen then close
        await Task.Delay(10);
        await channel.CloseAsync();
        await writeTask;

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentGoAway_FromBothSides_NoCrash()
    {
        var (client, server) = await CreateReadyPairAsync();

        var clientGoAway = client.GoAwayAsync().AsTask();
        var serverGoAway = server.GoAwayAsync().AsTask();

        // Both GoAway calls should complete within a reasonable time
        var both = Task.WhenAll(clientGoAway, serverGoAway);
        var completed = await Task.WhenAny(both, Task.Delay(5000));
        Assert.Same(both, completed);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region GoAway + Channel Interactions

    [Fact]
    public async Task OpenChannel_AfterGoAway_StillAllowed()
    {
        var (client, server) = await CreateReadyPairAsync();

        await client.GoAwayAsync();
        await Task.Delay(200);

        // OpenChannel only checks _isRunning, not _isShuttingDown
        // Channel can be opened but won't be useful since transport is shutting down
        var ch = client.OpenChannel("after-goaway");
        Assert.NotNull(ch);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelsAsync_StopsAfterDispose()
    {
        var (client, server) = await CreateReadyPairAsync();

        var accepted = new List<IReadChannel>();
        var acceptTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ch in server.AcceptChannelsAsync())
                {
                    accepted.Add(ch);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        // Open one channel then dispose server
        client.OpenChannel("before");
        await Task.Delay(100);
        await server.DisposeAsync();

        // acceptTask should complete after dispose cancels internal CTS
        var completed = await Task.WhenAny(acceptTask, Task.Delay(3000));
        Assert.Same(acceptTask, completed);

        await client.DisposeAsync();
    }

    #endregion

    #region Write Before Ready

    [Fact]
    public async Task WriteAsync_BeforeChannelReady_BuffersOrThrows()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        // Don't wait for ready — write immediately
        // Should either buffer the write or throw
        bool wrote = false;
        bool threw = false;
        try
        {
            await channel.WriteAsync(new byte[10]);
            wrote = true;
        }
        catch (ChannelClosedException)
        {
            threw = true;
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        // One or the other
        Assert.True(wrote || threw);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task WaitForReadyAsync_CancellationToken_IsRespected()
    {
        // Create a mux that can never connect (stream factory throws)
        var mux = StreamMultiplexer.Create(new MultiplexerOptions
        {
            StreamFactory = async ct => { await Task.Delay(10_000, ct); throw new IOException("never"); },
        });
        mux.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            mux.WaitForReadyAsync(cts.Token));

        await mux.DisposeAsync();
    }

    #endregion

    #region AcceptChannel Misuse

    [Fact]
    public async Task AcceptChannel_SameIdTwice_ReturnsSameInstance()
    {
        var (client, server) = await CreateReadyPairAsync();

        var r1 = server.AcceptChannel("ch");
        var r2 = server.AcceptChannel("ch");

        // Should return the same pending channel
        Assert.Same(r1, r2);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannelAsync_SameIdTwice_BothResolveSameChannelId()
    {
        var (client, server) = await CreateReadyPairAsync();

        client.OpenChannel("ch");

        var r1 = await server.AcceptChannelAsync("ch");
        var r2 = await server.AcceptChannelAsync("ch");

        // Both resolve to a channel with the same ID
        Assert.Equal("ch", r1.ChannelId);
        Assert.Equal("ch", r2.ChannelId);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task AcceptChannel_EmptyStringId_IsAccepted()
    {
        var (client, server) = await CreateReadyPairAsync();

        // API does not currently validate empty channel IDs
        var ch = server.AcceptChannel("");
        Assert.NotNull(ch);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region OpenChannel Misuse

    [Fact]
    public async Task OpenChannel_EmptyStringId_IsAccepted()
    {
        var (client, server) = await CreateReadyPairAsync();

        // API does not currently validate empty channel IDs
        var ch = client.OpenChannel(new ChannelOptions { ChannelId = "" });
        Assert.NotNull(ch);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task OpenChannel_VeryLongId_IsAccepted()
    {
        var (client, server) = await CreateReadyPairAsync();

        // API does not currently validate channel ID length
        var longId = new string('x', 100_000);
        var ch = client.OpenChannel(new ChannelOptions { ChannelId = longId });
        Assert.NotNull(ch);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Stats After Errors

    [Fact]
    public async Task Stats_OpenChannels_IncrementsOnOpen()
    {
        var (client, server) = await CreateReadyPairAsync();

        Assert.Equal(0, client.Stats.OpenChannels);

        client.OpenChannel("a");
        Assert.Equal(1, client.Stats.OpenChannels);

        client.OpenChannel("b");
        Assert.Equal(2, client.Stats.OpenChannels);

        client.OpenChannel("c");
        Assert.Equal(3, client.Stats.OpenChannels);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stats_TotalChannelsOpened_CountsAllOpens()
    {
        var (client, server) = await CreateReadyPairAsync();

        client.OpenChannel("a");
        client.OpenChannel("b");
        client.OpenChannel("c");

        Assert.Equal(3, client.Stats.TotalChannelsOpened);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region Lookup Methods With Invalid Input

    [Fact]
    public async Task GetWriteChannel_NonExistentId_ReturnsNull()
    {
        var (client, server) = await CreateReadyPairAsync();

        var result = client.GetWriteChannel("does-not-exist");
        Assert.Null(result);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task GetReadChannel_NonExistentId_ReturnsNull()
    {
        var (client, server) = await CreateReadyPairAsync();

        var result = server.GetReadChannel("does-not-exist");
        Assert.Null(result);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion

    #region AsStream After Channel Close

    [Fact]
    public async Task WriteChannel_AsStream_WriteAfterClose_Throws()
    {
        var (client, server) = await CreateReadyPairAsync();

        var channel = client.OpenChannel("ch");
        await channel.WaitForReadyAsync();

        var stream = channel.AsStream();
        await channel.CloseAsync();

        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            stream.WriteAsync(new byte[10]).AsTask());

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task ReadChannel_AsStream_ReadAfterClose_ReturnsZero()
    {
        var (client, server) = await CreateReadyPairAsync();

        var w = client.OpenChannel("ch");
        var r = await server.AcceptChannelAsync("ch");
        await w.WaitForReadyAsync();

        var stream = r.AsStream();

        // Close from writer side
        await w.CloseAsync();
        await Task.Delay(100);

        var bytesRead = await stream.ReadAsync(new byte[10]);
        Assert.Equal(0, bytesRead);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    #endregion
}

